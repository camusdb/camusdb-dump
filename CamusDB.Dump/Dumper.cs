
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using System.Text;
using System.Text.RegularExpressions;
using CamusDB.Client;

namespace CamusDB.Dump;

/// <summary>
/// Writes the SQL that reproduces a database: its table definitions, the indexes on each table, and the
/// rows themselves.
/// </summary>
internal sealed partial class Dumper
{
    private readonly CamusConnection connection;

    private readonly CamusTransaction? transaction;

    private readonly Options opts;

    /// <summary>The database this dumper reads — the one its connection is scoped to.</summary>
    private readonly string database;

    private readonly TextWriter output;

    private readonly DumpWarnings warnings;

    private readonly DumpProgress progress;

    // Latched false the first time a server rejects WITHOUT INDEXES, so an older server costs one
    // failed statement for the whole dump rather than one per table.
    private bool serverRendersIndexFreeDdl = true;

    /// <summary>The instant the rows are read at, or null when the dump reads the latest data.</summary>
    private readonly PointInTime? pointInTime;

    public Dumper(CamusConnection connection, CamusTransaction? transaction, Options opts, string database, PointInTime? pointInTime, TextWriter output, DumpWarnings warnings, DumpProgress progress)
    {
        this.connection = connection;
        this.transaction = transaction;
        this.opts = opts;
        this.database = database;
        this.pointInTime = pointInTime;
        this.output = output;
        this.warnings = warnings;
        this.progress = progress;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        (string endpoint, _, string protocol) = ConnectionFactory.Describe(opts, database);

        List<string> tables = await ResolveTablesAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, List<string>> definitions = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<ForeignKeyReference>> references = new(StringComparer.OrdinalIgnoreCase);

        // Every definition is read before anything is written, because the foreign keys in them decide
        // the order of the tables and, in a table that references itself, the order of its rows. With
        // neither CREATE TABLE nor INSERT in the dump, there is nothing whose order matters.
        if (!opts.NoCreateTable || !opts.NoData)
        {
            foreach (string table in tables)
            {
                List<string> ddls = await ReadTableDefinitionsAsync(table, cancellationToken).ConfigureAwait(false);

                definitions[table] = ddls;
                references[table] = ddls.SelectMany(ForeignKeyOrder.Parse).ToList();
            }

            tables = OrderByForeignKeys(tables, references);
        }

        progress.TablesResolved(tables.Count);

        if (!opts.NoHeader)
            WriteHeader(endpoint, protocol, tables);

        // With --all-databases the pair is not optional: each section has to create its database and then
        // point the client at it, or everything that follows would land in whichever database the client
        // happened to connect to.
        if (opts.CreateDatabase || opts.AllDatabases)
        {
            output.WriteLine($"CREATE DATABASE IF NOT EXISTS {SqlLiteral.Identifier(database, "database")};");
            output.WriteLine($"USE {UseTarget(database)};\n");
        }

        // The server refuses to drop a table that another table references, so every child is dropped
        // before its parent: all the drops come first, in the reverse of the creation order.
        if (opts.AddDropTable && !opts.NoCreateTable && tables.Count > 0)
        {
            for (int i = tables.Count - 1; i >= 0; i--)
                output.WriteLine($"DROP TABLE IF EXISTS {SqlLiteral.Identifier(tables[i], "table")};");

            output.WriteLine();
        }

        foreach (string table in tables)
        {
            progress.TableStarted(table);

            List<string> indexStatements = opts.NoIndexes
                ? []
                : await DumpIndexesAsync(table, cancellationToken).ConfigureAwait(false);

            if (!opts.NoCreateTable)
                WriteTableDefinitions(definitions[table]);

            if (!opts.DeferIndexes)
                WriteIndexes(indexStatements);

            if (!opts.NoData)
            {
                List<ForeignKeyReference> selfReferences = references[table]
                    .Where(r => string.Equals(r.ParentTable, table, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                await DumpTableDataAsync(table, selfReferences, cancellationToken).ConfigureAwait(false);
            }

            if (opts.DeferIndexes)
                WriteIndexes(indexStatements);

            progress.TableCompleted();
        }

        if (!opts.NoHeader)
            WriteFooter();
    }

    /// <summary>
    /// The database name as <c>USE</c> takes it, which is the bare name. <c>USE</c> is read by the client
    /// rather than the server — it is a shell command in camus-cli, which takes a bare name — so the
    /// backticks the rest of the dump uses would be part of the name here. Every identifier is checked
    /// against the CamusDB grammar, so a bare name is always writable.
    /// </summary>
    private static string UseTarget(string database)
    {
        SqlLiteral.RequirePlainIdentifier(database, "database");

        return database;
    }

    private void WriteHeader(string endpoint, string protocol, List<string> tables)
    {
        string version = typeof(Dumper).Assembly.GetName().Version?.ToString(3) ?? "";

        output.WriteLine($"-- camus-dump {version}");
        output.WriteLine($"-- Host: {endpoint}    Database: {database}    Protocol: {protocol}");
        output.WriteLine($"-- Tables: {(tables.Count == 0 ? "(none)" : string.Join(", ", tables))}");

        if (opts.AllDatabases)
            output.WriteLine($"-- One section of an --all-databases dump. The USE below switches to {database}; a client that does not read USE has to connect to it directly.");

        if (opts.SingleTransaction)
            output.WriteLine("-- Consistent snapshot: serializable read-only transaction");

        if (pointInTime is not null)
        {
            string requested = pointInTime.Requested is null ? "" : $" (requested as {pointInTime.Requested})";

            output.WriteLine($"-- Point in time: {pointInTime.Timestamp}{requested}");
            output.WriteLine($"-- Rows read with AS OF SYSTEM TIME '{pointInTime.Timestamp}'; table definitions and indexes are current.");
        }
        else if (!opts.SingleTransaction)
        {
            output.WriteLine("-- Point in time: latest committed data (--no-as-of)");
        }

        output.WriteLine();
    }

    private void WriteFooter()
    {
        output.WriteLine("-- Dump completed");

        foreach (string summary in warnings.Summaries())
            output.WriteLine("-- WARNING: " + summary);

        // Keeps the next database's header off the last line of this one when several share a stream.
        output.WriteLine();
    }

    /// <summary>
    /// The tables to dump: everything in the database, or the <c>--table</c> selection, minus anything
    /// <c>--exclude-table</c> names. Names are matched case-insensitively, as CamusDB compares identifiers.
    /// </summary>
    private async Task<List<string>> ResolveTablesAsync(CancellationToken cancellationToken)
    {
        HashSet<string> excluded = new(opts.ExcludeTables.Select(t => t.Trim()).Where(t => t.Length > 0), StringComparer.OrdinalIgnoreCase);

        List<string> requested = opts.Tables.Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

        List<string> tables = requested.Count > 0
            ? requested
            : await FetchTablesAsync(cancellationToken).ConfigureAwait(false);

        return tables.Where(table => !excluded.Contains(table)).ToList();
    }

    private async Task<List<string>> FetchTablesAsync(CancellationToken cancellationToken)
    {
        using CamusCommand cmd = CreateCommand("SHOW TABLES");
        using CamusDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<string> tables = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            tables.Add(reader.GetString(reader.GetOrdinal("tables")));

        return tables;
    }

    /// <summary>
    /// Puts each table after the tables its foreign keys reference, so a loader that enforces them
    /// accepts the dump. See <see cref="ForeignKeyOrder"/>.
    ///
    /// <para>A table outside the dump cannot be put in order. A <c>--table</c> or an
    /// <c>--exclude-table</c> can leave out a parent, and the load then needs that parent to exist
    /// already, with the rows its children reference. That is reported, not refused, because a dump of
    /// some tables onto a database that has the rest is a valid use.</para>
    /// </summary>
    private List<string> OrderByForeignKeys(List<string> tables, Dictionary<string, List<ForeignKeyReference>> references)
    {
        HashSet<string> included = new(tables, StringComparer.OrdinalIgnoreCase);

        foreach (string table in tables)
        {
            foreach (string parent in references[table].Select(r => r.ParentTable).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (included.Contains(parent))
                    continue;

                warnings.Note(
                    $"foreign-key-parent-missing|{table}|{parent}",
                    $"table '{table}' references table '{parent}', which this dump does not include. "
                    + $"Before you load the dump, make sure that '{parent}' exists and holds the rows that '{table}' references.");
            }
        }

        List<string> ordered = ForeignKeyOrder.OrderTables(tables, references, out List<string> cycle);

        if (cycle.Count > 0)
        {
            warnings.Note(
                "foreign-key-table-cycle",
                $"the foreign keys of the tables {string.Join(", ", cycle)} form a cycle, so no order of the "
                + "tables satisfies them. These tables are written last, in their original order, and the load "
                + "can fail on them. The server refuses such a cycle, so the schema probably changed during the dump.");
        }

        return ordered;
    }

    /// <summary>
    /// Opens the reader for the table DDL, asking for the index-free form when the dump defers index
    /// builds. A server that predates <c>WITHOUT INDEXES</c> rejects the statement; the dump then
    /// falls back to the full DDL and warns, because continuing silently would produce a file whose
    /// <c>--defer-indexes</c> is inert — the very defect this option was fixed for.
    /// </summary>
    private async Task<CamusDataReader> ReadTableDefinitionAsync(string table, CancellationToken cancellationToken)
    {
        string quoted = SqlLiteral.Identifier(table, "table");

        if (opts.DeferIndexes && serverRendersIndexFreeDdl)
        {
            try
            {
                using CamusCommand deferred = CreateCommand("SHOW CREATE TABLE " + quoted + " WITHOUT INDEXES");

                return await deferred.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (CamusException)
            {
                serverRendersIndexFreeDdl = false;

                warnings.Note(
                    "defer-indexes-unsupported",
                    "--defer-indexes had no effect: this server does not support "
                    + "SHOW CREATE TABLE ... WITHOUT INDEXES, so each table's indexes are declared in its "
                    + "CREATE TABLE and are built as the rows load. Upgrade the server to defer them.");
            }
        }

        using CamusCommand cmd = CreateCommand("SHOW CREATE TABLE " + quoted);

        return await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the table DDL. When the dump writes it, each statement is checked and prepared here, so a
    /// definition that is refused stops the dump before its first line is written. With
    /// <c>--no-create-table</c> the text is read only for its foreign keys, and is returned as the
    /// server sent it.
    ///
    /// <para>With <c>--defer-indexes</c> the DDL is requested as
    /// <c>SHOW CREATE TABLE … WITHOUT INDEXES</c>. Without that, the DDL declares every secondary
    /// index inline as a <c>KEY</c> clause, the index is built before the first row and maintained
    /// row by row, and relocating the <c>CREATE INDEX</c> statements after the data achieves nothing
    /// — which is exactly what the option did before. The primary key is still rendered: it is part
    /// of the table definition and cannot be created by a later <c>CREATE INDEX</c>.</para>
    /// </summary>
    private async Task<List<string>> ReadTableDefinitionsAsync(string table, CancellationToken cancellationToken)
    {
        using CamusDataReader reader = await ReadTableDefinitionAsync(table, cancellationToken).ConfigureAwait(false);

        List<string> definitions = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string ddl = reader.GetString(reader.GetOrdinal("Create Table")).TrimEnd();

            definitions.Add(opts.NoCreateTable ? ddl : PrepareTableDefinition(ddl, table));
        }

        return definitions;
    }

    /// <summary>
    /// Checks one statement from <c>SHOW CREATE TABLE</c> and puts it in the form the dump writes.
    /// </summary>
    private string PrepareTableDefinition(string ddl, string table)
    {
        ValidateTableDefinition(ddl, table);

        // A DEFAULT or a COMMENT in the definition is a string literal the server wrote in the plain
        // form, so one ending in a backslash would break the loader that reads the dump. See SqlText.
        if (!SqlText.TryMakeLiteralsUnambiguous(ddl, out ddl, out string? problem))
            throw new DumpException(Refuse(table, problem!));

        if (!ddl.EndsWith(';'))
            ddl += ";";

        if (opts.IfNotExists && ddl.StartsWith("CREATE TABLE ", StringComparison.Ordinal))
            ddl = "CREATE TABLE IF NOT EXISTS " + ddl["CREATE TABLE ".Length..];

        return ddl;
    }

    private void WriteTableDefinitions(List<string> definitions)
    {
        foreach (string ddl in definitions)
            output.WriteLine("{0}\n", ddl);
    }

    /// <summary>
    /// The secondary indexes on a table, as <c>CREATE INDEX IF NOT EXISTS</c> statements.
    ///
    /// <para>They are emitted even though <c>SHOW CREATE TABLE</c> also renders them inline as
    /// <c>KEY</c>/<c>UNIQUE KEY</c> clauses: <c>IF NOT EXISTS</c> makes them a no-op when the table DDL
    /// already created them, and they are what a dump taken with <c>--no-create-table</c> — or replayed
    /// with <c>--defer-indexes</c>, so the rows load before the indexes are built — has to rely on. The
    /// primary key is skipped; it belongs to the table definition and cannot be created separately.</para>
    /// </summary>
    private async Task<List<string>> DumpIndexesAsync(string table, CancellationToken cancellationToken)
    {
        using CamusCommand cmd = CreateCommand("SHOW INDEXES FROM " + SqlLiteral.Identifier(table, "table"));
        using CamusDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        List<string> statements = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string name = reader.GetString(reader.GetOrdinal("Key_name"));

            // The primary index is internal ("~pk") and is not a legal identifier to re-create.
            if (name.StartsWith('~'))
                continue;

            bool unique = reader.GetString(reader.GetOrdinal("Non_unique")) == "0";
            string columns = IndexKeyColumnList(reader);
            string include = ColumnList(reader, "Include");

            if (columns.Length == 0)
                continue;

            StringBuilder sb = new();

            sb.Append("CREATE ");

            if (unique)
                sb.Append("UNIQUE ");

            sb.Append("INDEX IF NOT EXISTS ")
              .Append(SqlLiteral.Identifier(name, "index"))
              .Append(" ON ")
              .Append(SqlLiteral.Identifier(table, "table"))
              .Append(" (")
              .Append(columns)
              .Append(')');

            if (include.Length > 0)
                sb.Append(" INCLUDE (").Append(include).Append(')');

            statements.Add(sb.Append(';').ToString());

            // CREATE INDEX has no inline COMMENT clause, so a commented index needs a second
            // statement. It is emitted next to its index rather than collected separately, so the
            // two move together when --defer-indexes relocates the index build after the rows.
            string comment = OptionalString(reader, "Comment");

            if (comment.Length > 0)
            {
                statements.Add(
                    "COMMENT ON INDEX "
                    + SqlLiteral.Identifier(table, "table") + "." + SqlLiteral.Identifier(name, "index")
                    + " IS " + SqlLiteral.Quote(comment) + ";");
            }
        }

        return statements;
    }

    /// <summary>
    /// The index's key columns, each quoted and carrying its sort direction.
    ///
    /// <para><c>SHOW INDEXES</c> reports the directions in a <c>Directions</c> field that is a
    /// parallel list to <c>Columns</c> — the direction is deliberately not folded into <c>Columns</c>,
    /// because every element of that field is an identifier this method quotes. <c>ASC</c> is left
    /// implicit so an all-ascending index renders exactly as it did before directions were carried.</para>
    ///
    /// <para>A server older than the release that added <c>Directions</c> does not send the field.
    /// The index is then rendered all-ascending, which is what this tool emitted for every index
    /// before — a descending index dumped from such a server still restores ascending, and no
    /// rewriting here can recover a direction the server never sent.</para>
    /// </summary>
    private static string IndexKeyColumnList(CamusDataReader reader)
    {
        int columnsOrdinal = reader.GetOrdinal("Columns");

        if (reader.IsDBNull(columnsOrdinal))
            return "";

        string[] columns = Split(reader.GetString(columnsOrdinal));
        string[] directions = Split(OptionalString(reader, "Directions"));

        StringBuilder sb = new();

        for (int i = 0; i < columns.Length; i++)
        {
            if (i > 0)
                sb.Append(", ");

            sb.Append(SqlLiteral.Identifier(columns[i], "index column"));

            // Shorter or absent directions mean "ascending", which is also the pre-directions default.
            if (i < directions.Length && string.Equals(directions[i], "DESC", StringComparison.OrdinalIgnoreCase))
                sb.Append(" DESC");
        }

        return sb.ToString();
    }

    private static string[] Split(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Reads a field that a server older than this tool may not send at all. <c>GetOrdinal</c> throws
    /// for an unknown name, so the lookup is done over the reader's own field names instead: a dump
    /// against an older server must degrade, not fail.
    /// </summary>
    private static string OptionalString(CamusDataReader reader, string field)
    {
        for (int i = 0; i < reader.FieldCount; i++)
        {
            if (!string.Equals(reader.GetName(i), field, StringComparison.OrdinalIgnoreCase))
                continue;

            return reader.IsDBNull(i) ? "" : reader.GetString(i);
        }

        return "";
    }

    private void WriteIndexes(List<string> statements)
    {
        if (statements.Count == 0)
            return;

        foreach (string statement in statements)
            output.WriteLine(statement);

        output.WriteLine();
    }

    /// <summary>
    /// Reads a comma-separated column list out of a <c>SHOW INDEXES</c> row and re-quotes each name.
    /// The <c>Include</c> column is empty for a plain (non-covering) index.
    /// </summary>
    private static string ColumnList(CamusDataReader reader, string field)
    {
        int ordinal = reader.GetOrdinal(field);

        if (reader.IsDBNull(ordinal))
            return "";

        return string.Join(
            ", ",
            reader.GetString(ordinal)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(column => SqlLiteral.Identifier(column, "index column")));
    }

    /// <summary>
    /// Writes the rows of a table as batched <c>INSERT</c> statements.
    ///
    /// <para>A table with a foreign key on itself is the one case where the order of its rows matters:
    /// a row has to come after the row it references, or in the same statement. Such a table is read
    /// whole and its rows are put in that order before the first one is written (see
    /// <see cref="ForeignKeyOrder.OrderRows"/>), so it is held in memory as a whole. Every other table
    /// is streamed.</para>
    /// </summary>
    private async Task DumpTableDataAsync(string table, List<ForeignKeyReference> selfReferences, CancellationToken cancellationToken)
    {
        string sql = "SELECT * FROM " + SqlLiteral.Identifier(table, "table");

        // The time-travel clause belongs immediately after the table, before any WHERE.
        if (pointInTime is not null)
            sql += " " + pointInTime.Clause;

        if (!string.IsNullOrWhiteSpace(opts.Where))
            sql += " WHERE " + opts.Where;

        using CamusCommand cmd = CreateCommand(sql);

        // Rows are streamed rather than buffered: a dump reads whole tables, and the buffered path would
        // hold every row of the largest one in memory at once.
        using CamusDataReader reader = await cmd.ExecuteStreamReaderAsync(cancellationToken).ConfigureAwait(false);

        int batchSize = Math.Max(1, opts.Batch);
        string? insertPrefix = null;
        List<string> batchRows = new(batchSize);

        void FlushBatch()
        {
            if (batchRows.Count == 0)
                return;

            StringBuilder sb = new();
            sb.Append(insertPrefix);
            sb.AppendJoin(",\n  ", batchRows.Select(r => "(" + r + ")"));
            sb.Append(';');
            output.WriteLine(sb.ToString());
            progress.RowsWritten(batchRows.Count);
            batchRows.Clear();
        }

        if (selfReferences.Count == 0)
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                insertPrefix ??= BuildInsertPrefix(reader, table);

                batchRows.Add(string.Join(", ", RenderRow(reader, table)));

                if (batchRows.Count >= batchSize)
                    FlushBatch();
            }
        }
        else
        {
            List<string[]> rows = [];
            List<(int[] Child, int[] Parent)>? keys = null;

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                insertPrefix ??= BuildInsertPrefix(reader, table);
                keys ??= ResolveSelfReferenceKeys(reader, table, selfReferences);

                rows.Add(RenderRow(reader, table));
            }

            List<int> order = ForeignKeyOrder.OrderRows(rows, keys ?? [], out int held);
            int free = order.Count - held;

            for (int k = 0; k < order.Count; k++)
            {
                // The held rows form one statement whatever --batch says, so they start a batch of their own.
                if (k == free)
                    FlushBatch();

                batchRows.Add(string.Join(", ", rows[order[k]]));

                if (k < free && batchRows.Count >= batchSize)
                    FlushBatch();
            }

            if (held > 0)
            {
                warnings.Note(
                    $"foreign-key-row-cycle|{table}",
                    $"{held} rows of table '{table}' reference each other in a cycle through a foreign key on "
                    + "the table itself. No order of separate statements satisfies them, so they are written as "
                    + "one INSERT statement, which ignores --batch: the server checks the constraint when the "
                    + "statement ends.");
            }
        }

        FlushBatch();

        output.WriteLine();
    }

    private string[] RenderRow(CamusDataReader reader, string table)
    {
        string[] row = new string[reader.FieldCount];

        for (int i = 0; i < reader.FieldCount; i++)
            row[i] = SqlLiteral.Render(reader.GetColumnValue(i), table, reader.GetName(i), warnings);

        return row;
    }

    /// <summary>
    /// The ordinals in the result of the referencing and the referenced columns of each foreign key a
    /// table has on itself. A key whose columns cannot all be found is left out and reported. The rows
    /// are then written without regard to it, and the load can fail on them.
    /// </summary>
    private List<(int[] Child, int[] Parent)> ResolveSelfReferenceKeys(CamusDataReader reader, string table, List<ForeignKeyReference> selfReferences)
    {
        Dictionary<string, int> ordinals = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < reader.FieldCount; i++)
            ordinals.TryAdd(reader.GetName(i), i);

        List<(int[] Child, int[] Parent)> keys = [];

        foreach (ForeignKeyReference reference in selfReferences)
        {
            int[]? child = Ordinals(reference.ChildColumns);
            int[]? parent = Ordinals(reference.ParentColumns);

            if (child is null || parent is null || child.Length == 0 || child.Length != parent.Length)
            {
                warnings.Note(
                    $"foreign-key-row-order|{table}",
                    $"the columns of a foreign key of table '{table}' on itself were not found in its rows, so "
                    + "the rows were not put in the order of that key. The load can fail on a row that comes "
                    + "before the row it references.");
                continue;
            }

            keys.Add((child, parent));
        }

        return keys;

        int[]? Ordinals(string[] columns)
        {
            int[] result = new int[columns.Length];

            for (int i = 0; i < columns.Length; i++)
            {
                if (!ordinals.TryGetValue(columns[i], out result[i]))
                    return null;
            }

            return result;
        }
    }

    private static string BuildInsertPrefix(CamusDataReader reader, string table)
    {
        string[] fields = new string[reader.FieldCount];

        for (int i = 0; i < reader.FieldCount; i++)
            fields[i] = SqlLiteral.Identifier(reader.GetName(i), "column");

        return $"INSERT INTO {SqlLiteral.Identifier(table, "table")} ({string.Join(", ", fields)}) VALUES\n  ";
    }

    /// <summary>
    /// Matches the head of a <c>CREATE TABLE</c> statement and captures the table it declares. The name
    /// may be backtick-quoted, and it may carry a database qualifier, which is dropped.
    /// </summary>
    [GeneratedRegex(
        @"^\s*CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:(?:`[^`]*`|[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*)?(?:`(?<name>[^`]*)`|(?<name>[A-Za-z_][A-Za-z0-9_]*))",
        RegexOptions.IgnoreCase)]
    private static partial Regex CreateTableHead();

    /// <summary>
    /// Checks the DDL from <c>SHOW CREATE TABLE</c> before it is copied into the dump.
    ///
    /// <para>This is the one place where server text reaches the dump without being quoted or escaped —
    /// it is SQL by definition, so it cannot be. A dump runs as SQL when it is restored, so the text is
    /// held to what it claims to be: one statement, a <c>CREATE TABLE</c>, for the table that was asked
    /// for. Anything else is refused rather than passed on to the restore.</para>
    /// </summary>
    /// <exception cref="DumpException">The definition is not a single CREATE TABLE for this table.</exception>
    private static void ValidateTableDefinition(string ddl, string table)
    {
        int separator = SqlText.FindStatementSeparator(ddl, out bool unterminated);

        if (unterminated)
            throw new DumpException(Refuse(table, "it leaves a quoted string or a comment open"));

        // A single trailing ';' closes the statement; anything after one ends a second statement.
        if (separator >= 0 && !ddl.AsSpan(separator + 1).IsWhiteSpace())
            throw new DumpException(Refuse(table, "it holds more than one statement"));

        Match head = CreateTableHead().Match(ddl);

        if (!head.Success)
            throw new DumpException(Refuse(table, "it does not begin with a CREATE TABLE statement"));

        string declared = head.Groups["name"].Value;

        if (!string.Equals(declared, table, StringComparison.OrdinalIgnoreCase))
            throw new DumpException(Refuse(table, $"it declares the table '{declared}' instead"));
    }

    private static string Refuse(string table, string reason)
        => $"the server's definition of table '{table}' was refused: {reason}. A dump runs as SQL when it " +
           "is restored, so only a single CREATE TABLE for this table is written out. " +
           "Skip the table with --exclude-table.";

    private CamusCommand CreateCommand(string sql)
    {
        CamusCommand command = connection.CreateSelectCommand(sql);

        if (transaction is not null)
            command.Transaction = transaction;

        return command;
    }
}
