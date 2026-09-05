# CamusDB Dump

`camus-dump` performs logical backups, producing a set of SQL statements that can be executed to reproduce the original [CamusDB](https://github.com/camusdb/camusdb) database definitions, indexes and table data

## Installation

Install the `camus-dump` package from NuGet. Add it to your project in the normal way (for example by right-clicking on the project in Visual Studio and choosing "Manage NuGet Packages...").

#### Using .NET CLI

```shell
dotnet tool install --global CamusDB.Dump
```

## Usage

```shell
# Everything in a database, to standard output
camus-dump --endpoint http://localhost:5096 --database mydb

# One table, 100 rows per INSERT, to a file
camus-dump -e http://localhost:5096 -d mydb -t orders -b 100 -o orders.sql

# The whole database as it was five minutes ago, replayable onto an existing schema
camus-dump -e http://localhost:5096 -d mydb --as-of -5m --if-not-exists -o backup.sql

# Every database on the server, one file each
camus-dump -e http://localhost:5096 --all-databases --output-directory backup/
```

Restore by feeding the file back to any CamusDB SQL client, such as [`camussqlsh`](https://github.com/camusdb/camussqlsh).

### Connecting

| Option | Description |
| --- | --- |
| `-c`, `--connection-source` | Full connection string. Every option below fills in a key it does not already set. |
| `-e`, `--endpoint` | Server endpoint, or a comma-separated pool (default `http://localhost:5096`, the gRPC port). |
| `-d`, `--database` | Database to dump (default `test`). |
| `-A`, `--all-databases` | Dump every database on the server (see below). |
| `-X`, `--exclude-database` | With `--all-databases`, skip these databases. |
| `--protocol` | `grpc` (default) or `rest`. The server exposes each on its own port, so an endpoint given with `-e` has to match the protocol — REST against the gRPC port fails with *an HTTP/1.x request was sent to an HTTP/2 only endpoint*. |
| `--timeout` | Per-statement timeout in seconds (default `10`). |

### Authentication

CamusDB authentication is off by default. Against a server started with `CAMUSDB_AUTH_ENABLED=true`, pass credentials:

```shell
# Password from the environment — it never appears in the process list
CAMUSDB_PASSWORD=app-secret camus-dump -e https://camus.internal:5096 -d mydb -u app

# Prompt for it instead
camus-dump -e https://camus.internal:5096 -d mydb -u app --ask-password

# Or use a token minted elsewhere — from the environment for the same reason
CAMUSDB_ACCESS_TOKEN=camus_... camus-dump -e https://camus.internal:5096 -d mydb
```

| Option | Description |
| --- | --- |
| `-u`, `--user` | User to authenticate as. |
| `-p`, `--password` | That user's password. Prefer `CAMUSDB_PASSWORD` or `--ask-password`. |
| `-W`, `--ask-password` | Prompt for the password on the terminal. |
| `--access-token` | Bearer token obtained elsewhere, used instead of logging in. Prefer `CAMUSDB_ACCESS_TOKEN`. |
| `--token-lifetime` | Seconds to reuse a minted token when the server reports no expiry (default `600`). |

A password or a token given on the command line is visible to every local user through the process list, for as long as the dump runs. `CAMUSDB_PASSWORD`, `CAMUSDB_ACCESS_TOKEN` and `--ask-password` avoid that.

The password is exchanged once for a short-lived bearer token, which the driver renews on its own; the password itself never travels with a statement. The dump only reads, so `SELECT` and `SHOW` privileges on the dumped tables are enough. With authentication enabled the server refuses credentials over plaintext outside loopback — use an `https://` endpoint. The rows themselves carry no such rule, so camus-dump warns when it sends a dump unencrypted to another host.

Authentication works the same over gRPC (`--protocol grpc`): the exchange rides the server's `CamusAuth` service on the channel that carries the statements, so no HTTP port has to be exposed just to obtain a token.

### Choosing what to dump

| Option | Description |
| --- | --- |
| `-t`, `--table` | Dump only these tables (comma-separated, or repeat the option). |
| `-x`, `--exclude-table` | Skip these tables. |
| `-w`, `--where` | Dump only rows matching this condition. The text is unparsed SQL and reaches the query as written, so never build it from untrusted input. |
| `--as-of` | Read every table as of this point in time (see below). |
| `--no-as-of` | Read the latest committed data instead. |
| `--no-create-table` | Do not emit `CREATE TABLE`. |
| `--no-data` | Do not emit `INSERT`. |
| `--no-indexes` | Do not emit the `CREATE INDEX` statements that follow each table. |

### Dumping every database

`--all-databases` asks the server for its databases with `SHOW DATABASES` and dumps each one in turn, skipping anything `--exclude-database` names:

```shell
# Every database, as one stream of sections
camus-dump -A -o server.sql

# Every database except two, one file per database under backup/
camus-dump -A -X scratch,tempdb --output-directory backup/
```

Every database is read as of the same instant, since the point in time is fixed once before the first statement goes out. `--single-transaction` is the exception: a transaction belongs to a connection and each database gets its own, so it makes each database internally consistent but does not tie them to a common snapshot.

Each section opens with `CREATE DATABASE IF NOT EXISTS` and a `USE`, whether or not `--create-database` was passed, so one file restores every database in turn. `USE` is not server-side SQL — CamusDB's parser rejects it — but a client reads it and points the statements that follow at that database, which is how [`camus-cli`](https://github.com/camusdb/camus-cli) takes the whole file. Against a client that does not, dump with `--output-directory`: it writes `<database>.sql` per database, so each file goes back on its own with `-d` pointing at the matching database.

The other options apply per database. `-t`/`-x` match table names in every one of them, and `-w` filters rows in every table it names — a condition that references a column only some tables have will fail on the others.

### Point in time

By default the dump does **not** read the latest data. It fixes an instant when it starts — a second behind the wall clock, to stay clear of the server's own clock — and reads every table as of that instant with CamusDB's [`AS OF SYSTEM TIME`](https://github.com/camusdb/camusdb/blob/main/docs/time-travel-reads.md) clause. Without that, a row written between the first table's scan and the last one's lands in the dump without whatever it referenced in a table already written, and the dump restores into a state the database was never in.

The instant is recorded at the top of the dump, so a dump can be reproduced exactly:

```
-- Point in time: 2026-07-29 19:15:35.277+00:00
-- Rows read with AS OF SYSTEM TIME '2026-07-29 19:15:35.277+00:00'; table definitions and indexes are current.
```

`--as-of` picks a different instant, in any of the forms the server accepts:

```shell
# Five minutes ago
camus-dump -d mydb --as-of -5m

# An absolute UTC instant — for example, the one a previous dump recorded
camus-dump -d mydb --as-of "2026-07-29 19:15:35.277+00:00"

# Unix epoch milliseconds
camus-dump -d mydb --as-of 1721420000000
```

Offsets take `ms`, `s`, `m`, `h`, `d` and must be negative. A relative offset is resolved to an absolute instant once, before the first statement goes out, rather than passed through — otherwise the server would evaluate it afresh for each table and the tables would not share a snapshot.

Notes:

- **Rows only.** `SHOW CREATE TABLE` and `SHOW INDEXES` have no time-travel form, so the schema in the dump is the current one. A table created after the chosen instant appears with its definition and no rows.
- **Retention bounds how far back you can look.** An instant older than the history the storage layer still keeps reads as empty rather than as an error.
- **`--single-transaction` replaces it.** The server rejects `AS OF SYSTEM TIME` inside an explicit transaction, which is already pinned to one snapshot; passing `--single-transaction` turns the default off, and passing both it and `--as-of` is an error.
- **`--no-as-of`** reads the latest committed data, with no consistency guarantee across tables.

### Shaping the output

| Option | Description |
| --- | --- |
| `-b`, `--batch` | Rows per `INSERT` statement (default `1`). |
| `-o`, `--output` | Write to this file instead of standard output. |
| `--output-directory` | Write one `<database>.sql` file per database into this directory, creating it if missing. Cannot be combined with `-o`. |
| `--defer-indexes` | Emit each table's `CREATE INDEX` statements after its data rather than before. |
| `--add-drop-table` | Emit `DROP TABLE IF EXISTS` before each `CREATE TABLE`. |
| `--if-not-exists` | Emit `CREATE TABLE IF NOT EXISTS`, so the dump replays onto an existing schema. |
| `--create-database` | Emit `CREATE DATABASE IF NOT EXISTS` for the dumped database, followed by `USE`. Implied by `--all-databases`. |
| `--single-transaction` | Read every table from one lock-free serializable snapshot instead of a fixed past instant. |
| `--strict` | Fail instead of emitting `NULL` for a value that has no SQL literal (see below). |
| `--no-header` | Omit the leading comment header. |

A dump holds every row of the database, so `-o` and `--output-directory` create files that only their owner can read or write, and `--output-directory` creates the directory the same way. An existing directory keeps the permissions it has; camus-dump warns when other users can write to it. A path that is already a symbolic link is refused rather than followed, because the dump would truncate whatever is at the far end.

### Data types and indexes

Every type CamusDB stores is dumped as a literal that parses back to the same value: `OID`, `STRING`, `INT64`, `FLOAT64`, `FLOAT32`, `BOOL`, `BYTES` (as `X'…'`), `DATE`, `DATETIME`, `UUID`, `ARRAY` (as `ARRAY[…]`) and `NULL`.

Strings use CamusDB's two literal forms: the plain `'…'` form, which does no escape processing, for everything except values containing a control character, which use the `E'…'` escape form. Any string round-trips, including one holding a backslash, a trailing backslash, both quote characters, or a newline.

Indexes — unique, multi-column, and covering indexes with `INCLUDE` columns — are dumped both inline in `CREATE TABLE` and as separate `CREATE INDEX IF NOT EXISTS` statements, so a dump taken with `--no-create-table` still carries them, and `--defer-indexes` can build them after the rows have loaded. The `IF NOT EXISTS` makes the separate statements a no-op when the table definition already created the index.

One thing has no CamusDB SQL literal, and `camus-dump` reports it rather than emitting something that would restore incorrectly:

- **Non-finite floats** — `NaN`, `+Infinity`, `-Infinity`. CamusDB's float literal has no form for them. The value is dumped as `NULL`.

It is counted and printed to standard error at the end of the run, and repeated as `-- WARNING` lines in the dump itself; `--strict` turns it into a failure instead. `DATETIME` values are truncated to milliseconds, the finest precision a CamusDB literal carries, and a dump that truncates one says so the same way.

## Contribution

`camus-dump` is an open-source project, and contributions are heartily welcomed! Whether you are looking to fix bugs, add new features, or improve documentation, your efforts and contributions will be appreciated. Check out the CONTRIBUTING.md file for guidelines on how to get started with contributing to `camus-dump`.

## License

`camus-dump` is released under the MIT License.
