
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using System.Text;
using CamusDB.Client;

namespace CamusDB.Dump;

/// <summary>
/// Writes the SQL that reproduces a database: its table definitions, the indexes on each table, and the
/// rows themselves.
/// </summary>
internal sealed class Dumper
{
    private readonly CamusConnection connection;

    private readonly CamusTransaction? transaction;

    private readonly Options opts;

    private readonly TextWriter output;

    private readonly DumpWarnings warnings;

    /// <summary>The instant the rows are read at, or null when the dump reads the latest data.</summary>
    private readonly PointInTime? pointInTime;

    public Dumper(CamusConnection connection, CamusTransaction? transaction, Options opts, PointInTime? pointInTime, TextWriter output, DumpWarnings warnings)
    {
        this.connection = connection;
        this.transaction = transaction;
        this.opts = opts;
        this.pointInTime = pointInTime;
        this.output = output;
        this.warnings = warnings;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        (string endpoint, string database, string protocol) = ConnectionFactory.Describe(opts);

        List<string> tables = await ResolveTablesAsync(cancellationToken).ConfigureAwait(false);

        if (!opts.NoHeader)
            WriteHeader(endpoint, database, protocol, tables);

        if (opts.CreateDatabase)
            output.WriteLine($"CREATE DATABASE IF NOT EXISTS {SqlLiteral.Identifier(database)};\n");

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

    private void WriteHeader(string endpoint, string database, string protocol, List<string> tables)
    {
        string version = typeof(Dumper).Assembly.GetName().Version?.ToString(3) ?? "";

        output.WriteLine($"-- camus-dump {version}");
        output.WriteLine($"-- Host: {endpoint}    Database: {database}    Protocol: {protocol}");
        output.WriteLine($"-- Tables: {(tables.Count == 0 ? "(none)" : string.Join(", ", tables))}");

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
        using CamusCommand cmd = CreateCommand("SHOW CREATE TABLE " + SqlLiteral.Identifier(table));
        using CamusDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string ddl = reader.GetString(reader.GetOrdinal("Create Table")).TrimEnd();

            if (!ddl.EndsWith(';'))
                ddl += ";";

            if (opts.IfNotExists && ddl.StartsWith("CREATE TABLE ", StringComparison.Ordinal))
                ddl = "CREATE TABLE IF NOT EXISTS " + ddl["CREATE TABLE ".Length..];

            if (opts.AddDropTable)
                output.WriteLine($"DROP TABLE IF EXISTS {SqlLiteral.Identifier(table)};");

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
        using CamusCommand cmd = CreateCommand("SHOW INDEXES FROM " + SqlLiteral.Identifier(table));
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
              .Append(SqlLiteral.Identifier(name))
              .Append(" ON ")
              .Append(SqlLiteral.Identifier(table))
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
                .Select(SqlLiteral.Identifier));
    }

    private async Task DumpTableDataAsync(string table, CancellationToken cancellationToken)
    {
        string sql = "SELECT * FROM " + SqlLiteral.Identifier(table);

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
            fields[i] = SqlLiteral.Identifier(reader.GetName(i));

        return $"INSERT INTO {SqlLiteral.Identifier(table)} ({string.Join(", ", fields)}) VALUES\n  ";
    }

    private CamusCommand CreateCommand(string sql)
    {
        CamusCommand command = connection.CreateSelectCommand(sql);

        if (transaction is not null)
            command.Transaction = transaction;

        return command;
    }
}
