
/**
 * This file is part of CamusDB  
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using CamusDB.Client;
using CommandLine;
using System.Text;

ParserResult<Options> optsResult = Parser.Default.ParseArguments<Options>(args);

Options? opts = optsResult.Value;
if (opts is null)
    return;

//Console.WriteLine("CamusDB Dump 0.0.1\n");

CamusConnection connection = await GetConnection(opts);

List<string> tables;
if (!string.IsNullOrEmpty(opts.Table))
    tables = new() { opts.Table };
else
    tables = await FetchTables(connection);

foreach (string table in tables)
{
    await DumpTableDefinition(connection, table);
    await DumpTable(connection, table);
}

static async Task DumpTableDefinition(CamusConnection connection, string table)
{
    using CamusCommand cmd = connection.CreateSelectCommand("SHOW CREATE TABLE `" + table + "`");

    using CamusDataReader reader = await cmd.ExecuteReaderAsync();

    while (await reader.ReadAsync())
    {
        int ordinal = reader.GetOrdinal("Create Table");
        Console.WriteLine("{0}\n", reader.GetString(ordinal));
    }
}

static async Task DumpTable(CamusConnection connection, string table)
{
    using CamusCommand cmd = connection.CreateSelectCommand("SELECT * FROM `" + table + "`");

    using CamusDataReader reader = await cmd.ExecuteReaderAsync();

    StringBuilder sb = new();
    string? fields = null;

    while (await reader.ReadAsync())
    {
        sb.Clear();

        if (fields is null)
        {
            string[] fieldsList = new string[reader.FieldCount];
            for (int j = 0; j < reader.FieldCount; j++)
                fieldsList[j] = "`" + reader.GetName(j) + "`";
            fields = string.Join(", ", fieldsList);
        }

        string[] row = new string[reader.FieldCount];
        for (int i = 0; i < reader.FieldCount; i++)
        {
            row[i] = reader.GetDataTypeName(i) switch
            {
                "Id" => "STR_ID(\"" + (reader.IsDBNull(i) ? "" : reader.GetString(i)) + "\")",
                "String" => "\"" + (reader.IsDBNull(i) ? "" : reader.GetString(i).Replace("\"", "\\\"")) + "\"",
                "Integer64" => reader.GetInt64(i).ToString(),
                "Float64" => reader.GetDouble(i).ToString(),
                "Bool" => reader.GetBoolean(i).ToString(),
                _ => "null"
            };
        }

        sb.Append("INSERT INTO `");
        sb.Append(table);
        sb.Append("` (");
        sb.Append(fields);
        sb.Append(") VALUES ");

        sb.Append('(');
        sb.Append(string.Join(", ", row));

        Console.WriteLine(sb.ToString() + ");");
    }

    Console.WriteLine();
}

static async Task<List<string>> FetchTables(CamusConnection connection)
{
    using CamusCommand cmd = connection.CreateSelectCommand("SHOW TABLES");

    List<string> tables = new();
    CamusDataReader reader = await cmd.ExecuteReaderAsync();

    while (await reader.ReadAsync())
    {
        int ordinal = reader.GetOrdinal("tables");
        tables.Add(reader.GetString(ordinal));
    }

    return tables;
}


static async Task<CamusConnection> GetConnection(Options opts)
{
    CamusConnection cmConnection;

    SessionPoolOptions options = new()
    {
        MinimumPooledSessions = 1,
        MaximumActiveSessions = 20,
    };

    string? connectionString = opts.ConnectionSource;

    if (string.IsNullOrEmpty(connectionString))
        connectionString = $"Endpoint=https://localhost:7141;Database=test";

    SessionPoolManager manager = SessionPoolManager.Create(options);

    CamusConnectionStringBuilder builder = new(connectionString)
    {
        SessionPoolManager = manager
    };

    cmConnection = new(builder);

    await cmConnection.OpenAsync();

    CamusPingCommand pingCommand = cmConnection.CreatePingCommand();

    await pingCommand.ExecuteNonQueryAsync();

    return cmConnection;
}

public sealed class Options
{
    [Option('c', "connection-source", Required = false, HelpText = "Set the connection string")]
    public string? ConnectionSource { get; set; }

    [Option('t', "table", Required = false, HelpText = "Dump only the specified table")]
    public string? Table { get; set; }
}