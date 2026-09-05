
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

    /// <summary>The instant the rows are read at, or null when the dump reads the latest data.</summary>
    private readonly PointInTime? pointInTime;

    public Dumper(CamusConnection connection, CamusTransaction? transaction, Options opts, string database, PointInTime? pointInTime, TextWriter output, DumpWarnings warnings)
    {
        this.connection = connection;
        this.transaction = transaction;
        this.opts = opts;
        this.database = database;
        this.pointInTime = pointInTime;
        this.output = output;
        this.warnings = warnings;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        (string endpoint, _, string protocol) = ConnectionFactory.Describe(opts, database);

        List<string> tables = await ResolveTablesAsync(cancellationToken).ConfigureAwait(false);

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

        foreach (string table in tables)
        {
            List<string> indexStatements = opts.NoIndexes
                ? []
                : await DumpIndexesAsync(table, cancellationToken).ConfigureAwait(false);

            if (!opts.NoCreateTable)
                await DumpTableDefinitionAsync(table, cancellationToken).ConfigureAwait(false);

            if (!opts.DeferIndexes)
                WriteIndexes(indexStatements);

            if (!opts.NoData)
                await DumpTableDataAsync(table, cancellationToken).ConfigureAwait(false);

            if (opts.DeferIndexes)
                WriteIndexes(indexStatements);
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

    private async Task DumpTableDefinitionAsync(string table, CancellationToken cancellationToken)
    {
        using CamusCommand cmd = CreateCommand("SHOW CREATE TABLE " + SqlLiteral.Identifier(table, "table"));
        using CamusDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string ddl = reader.GetString(reader.GetOrdinal("Create Table")).TrimEnd();

            ValidateTableDefinition(ddl, table);

            if (!ddl.EndsWith(';'))
                ddl += ";";

            if (opts.IfNotExists && ddl.StartsWith("CREATE TABLE ", StringComparison.Ordinal))
                ddl = "CREATE TABLE IF NOT EXISTS " + ddl["CREATE TABLE ".Length..];

            if (opts.AddDropTable)
                output.WriteLine($"DROP TABLE IF EXISTS {SqlLiteral.Identifier(table, "table")};");

            output.WriteLine("{0}\n", ddl);
        }
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
            string columns = ColumnList(reader, "Columns");
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
        }

        return statements;
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

    private async Task DumpTableDataAsync(string table, CancellationToken cancellationToken)
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
            batchRows.Clear();
        }

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            insertPrefix ??= BuildInsertPrefix(reader, table);

            string[] row = new string[reader.FieldCount];

            for (int i = 0; i < reader.FieldCount; i++)
                row[i] = SqlLiteral.Render(reader.GetColumnValue(i), table, reader.GetName(i), warnings);

            batchRows.Add(string.Join(", ", row));

            if (batchRows.Count >= batchSize)
                FlushBatch();
        }

        FlushBatch();

        output.WriteLine();
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
        int separator = FindStatementSeparator(ddl, out bool unterminated);

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

    /// <summary>
    /// The index of the first <c>;</c> in <paramref name="sql"/> that stands outside every string
    /// literal, quoted identifier and comment, or -1 when there is none. <paramref name="unterminated"/>
    /// reports a literal or a block comment that is never closed, which is itself a reason to refuse the
    /// text: the rest of it cannot be scanned.
    /// </summary>
    private static int FindStatementSeparator(string sql, out bool unterminated)
    {
        unterminated = false;

        int i = 0;

        while (i < sql.Length)
        {
            char c = sql[i];

            if (c == ';')
                return i;

            // A line comment runs to the newline, and an unterminated one ends the text harmlessly.
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                int newline = sql.IndexOf('\n', i);
                i = newline < 0 ? sql.Length : newline + 1;
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);

                if (close < 0)
                {
                    unterminated = true;
                    return -1;
                }

                i = close + 2;
                continue;
            }

            if (c is '`' or '"' or '\'')
            {
                // Only the E'…' form reads a backslash as an escape; a plain literal takes it verbatim.
                bool escapes = c == '\'' && i > 0 && (sql[i - 1] is 'E' or 'e')
                    && (i == 1 || !(char.IsAsciiLetterOrDigit(sql[i - 2]) || sql[i - 2] == '_'));

                i = SkipDelimited(sql, i, c, escapes);

                if (i < 0)
                {
                    unterminated = true;
                    return -1;
                }

                continue;
            }

            i++;
        }

        return -1;
    }

    /// <summary>
    /// The index just past the delimiter that closes the run starting at <paramref name="start"/>, or -1
    /// when it is never closed. A doubled delimiter stands for one character and does not close the run.
    /// </summary>
    private static int SkipDelimited(string sql, int start, char delimiter, bool backslashEscapes)
    {
        int i = start + 1;

        while (i < sql.Length)
        {
            char c = sql[i];

            if (backslashEscapes && c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == delimiter)
            {
                if (i + 1 < sql.Length && sql[i + 1] == delimiter)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return -1;
    }

    private CamusCommand CreateCommand(string sql)
    {
        CamusCommand command = connection.CreateSelectCommand(sql);

        if (transaction is not null)
            command.Transaction = transaction;

        return command;
    }
}
