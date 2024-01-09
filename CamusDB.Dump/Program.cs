
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

var connection = await GetConnection(opts);

List<string> tables = await FetchTables(connection);

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
        Dictionary<string, ColumnValue> current = reader.GetCurrent();

        Console.WriteLine("{0}\n", current["Create Table"].StrValue!);
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

        Dictionary<string, ColumnValue> current = reader.GetCurrent();

        int i = 0;
        string[] row = new string[current.Count];

        if (fields is null)
        {
            int j = 0;
            string[] fieldsList = new string[current.Count];

            foreach (KeyValuePair<string, ColumnValue> item in current)
                fieldsList[j++] = "`" + item.Key + "`";

            fields = string.Join(", ", fieldsList);
        }

        sb.Append("INSERT INTO `" + table + "` (" + fields + ") VALUES (");

        foreach (KeyValuePair<string, ColumnValue> item in current)
        {
            if (item.Value.Type == ColumnType.Id)
                row[i++] = !string.IsNullOrEmpty(item.Value.StrValue) ? "STR_ID(\"" + item.Value.StrValue!.ToString() + "\")" : "STR_ID(\"\")";
            else if (item.Value.Type == ColumnType.String)
                row[i++] = !string.IsNullOrEmpty(item.Value.StrValue) ? "\"" + item.Value.StrValue!.ToString() + "\"" : "\"\"";
            else if (item.Value.Type == ColumnType.Integer64)
                row[i++] = item.Value.LongValue.ToString();
            else if (item.Value.Type == ColumnType.Float)
                row[i++] = item.Value.LongValue.ToString();
            else if (item.Value.Type == ColumnType.Bool)
                row[i++] = item.Value.BoolValue.ToString();
            else
                row[i++] = "null";
        }

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
        Dictionary<string, ColumnValue> current = reader.GetCurrent();

        tables.Add(current["tables"].StrValue!);
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
}