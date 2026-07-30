
/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using CommandLine;

namespace CamusDB.Dump;

public sealed class Options
{
    [Option('c', "connection-source", Required = false, HelpText = "Full connection string. Individual options below fill in any key it does not already set.")]
    public string? ConnectionSource { get; set; }

    [Option('e', "endpoint", Required = false, HelpText = "Server endpoint, or a comma-separated pool (default: http://localhost:5096, the gRPC port).")]
    public string? Endpoint { get; set; }

    [Option('d', "database", Required = false, HelpText = "Database to dump (default: test).")]
    public string? Database { get; set; }

    [Option("protocol", Required = false, HelpText = "Wire protocol: grpc (default) or rest. Each addresses its own port on the server — pass an endpoint that matches.")]
    public string? Protocol { get; set; }

    [Option('u', "user", Required = false, HelpText = "User to authenticate as.")]
    public string? User { get; set; }

    [Option('p', "password", Required = false, HelpText = "That user's password. Prefer CAMUSDB_PASSWORD or --ask-password: a password on the command line is visible to other processes.")]
    public string? Password { get; set; }

    [Option('W', "ask-password", Required = false, HelpText = "Prompt for the password on the terminal.")]
    public bool AskPassword { get; set; }

    [Option("access-token", Required = false, HelpText = "Bearer token obtained elsewhere, used instead of logging in.")]
    public string? AccessToken { get; set; }

    [Option("token-lifetime", Required = false, HelpText = "Seconds to reuse a minted token when the server reports no expiry (default: 600).")]
    public int? TokenLifetime { get; set; }

    [Option("timeout", Required = false, HelpText = "Per-statement timeout in seconds (default: 10).")]
    public int? Timeout { get; set; }

    [Option('t', "table", Required = false, Separator = ',', HelpText = "Dump only these tables (comma-separated, or repeat the option).")]
    public IEnumerable<string> Tables { get; set; } = [];

    [Option('x', "exclude-table", Required = false, Separator = ',', HelpText = "Skip these tables (comma-separated, or repeat the option).")]
    public IEnumerable<string> ExcludeTables { get; set; } = [];

    [Option('w', "where", Required = false, HelpText = "Dump only rows matching this WHERE condition.")]
    public string? Where { get; set; }

    [Option("as-of", Required = false, HelpText = "Read every table as of this point in time: a negative offset (-10s, -5m, -1h, -1d), an absolute UTC timestamp ('2026-07-19 20:00:00+00:00'), or Unix epoch milliseconds. Defaults to the instant the dump starts.")]
    public string? AsOf { get; set; }

    [Option("no-as-of", Required = false, HelpText = "Read the latest committed data instead of a fixed point in time. Tables are then no longer read from a common instant.")]
    public bool NoAsOf { get; set; }

    [Option('b', "batch", Required = false, Default = 1, HelpText = "Number of rows per INSERT statement.")]
    public int Batch { get; set; }

    [Option('o', "output", Required = false, HelpText = "Write the dump to this file instead of standard output.")]
    public string? Output { get; set; }

    [Option("no-create-table", Required = false, HelpText = "Do not emit CREATE TABLE statements.")]
    public bool NoCreateTable { get; set; }

    [Option("no-data", Required = false, HelpText = "Do not emit INSERT statements.")]
    public bool NoData { get; set; }

    [Option("no-indexes", Required = false, HelpText = "Do not emit the CREATE INDEX statements that follow each table.")]
    public bool NoIndexes { get; set; }

    [Option("defer-indexes", Required = false, HelpText = "Emit the CREATE INDEX statements after a table's data rather than before it.")]
    public bool DeferIndexes { get; set; }

    [Option("add-drop-table", Required = false, HelpText = "Emit DROP TABLE IF EXISTS before each CREATE TABLE.")]
    public bool AddDropTable { get; set; }

    [Option("if-not-exists", Required = false, HelpText = "Emit CREATE TABLE IF NOT EXISTS so the dump can be replayed onto an existing schema.")]
    public bool IfNotExists { get; set; }

    [Option("create-database", Required = false, HelpText = "Emit CREATE DATABASE IF NOT EXISTS for the dumped database.")]
    public bool CreateDatabase { get; set; }

    [Option("single-transaction", Required = false, HelpText = "Dump every table from one lock-free serializable snapshot, so the dump is consistent across tables.")]
    public bool SingleTransaction { get; set; }

    [Option("strict", Required = false, HelpText = "Fail instead of emitting NULL when a value has no CamusDB SQL literal (ARRAY columns, strings holding control characters).")]
    public bool Strict { get; set; }

    [Option("no-header", Required = false, HelpText = "Omit the leading comment header.")]
    public bool NoHeader { get; set; }
}
