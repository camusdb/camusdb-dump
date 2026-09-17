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

The password is exchanged once for a short-lived bearer token, which the driver renews on its own; the password itself never travels with a statement. The dump only reads, so `SELECT` and `SHOW` privileges on the dumped tables are enough. Credentials are refused over plaintext outside loopback — use an `https://` endpoint. The driver refuses them itself, with `CADB0519`, before anything is sent; the server refuses them as well. Where the link is protected some other way, such as a VPN or an SSH tunnel, pass `-c "AllowInsecureCredentials=true"` to accept that. The rows themselves carry no such rule, so camus-dump warns when it sends a dump unencrypted to another host.

Authentication works the same over gRPC (`--protocol grpc`): the exchange rides the server's `CamusAuth` service on the channel that carries the statements, so no HTTP port has to be exposed just to obtain a token.

### Choosing what to dump

| Option | Description |
| --- | --- |
| `-t`, `--table` | Dump only these tables (comma-separated, or repeat the option). |
| `-x`, `--exclude-table` | Skip these tables. |
| `-w`, `--where` | Dump only rows matching this condition. The text is unparsed SQL and reaches the query as written, so never build it from untrusted input. |
| `--users` | Also export these database accounts and their grants (see below). |
| `--all-users` | Also export every database account and every grant (see below). |
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

### Users and grants

A dump carries databases, tables, indexes and rows. It does not carry the accounts that reach them,
and a storage revision upgrade empties the user catalog, so after a reimport the server holds the
bootstrap superuser and nothing else. Two options export the accounts, so that the list of grants is
not something you have to keep by hand:

```shell
# Every database, every account and every grant, with the accounts in their own files under backup/
camus-dump -e https://db1.internal:5096 -u admin -A --all-users --output-directory backup/

# One database, plus two named accounts and everything granted to them
camus-dump -e https://db1.internal:5096 -u admin -d shop --users app,reporting -o shop.sql
```

`--all-users` reads the catalog with `SHOW USERS` and `SHOW GRANTS FOR *`. The server answers each of
those from its own snapshot, so camus-dump checks one against the other: every account's grant count
from `SHOW USERS` has to match the grants listed for it. When an account or a grant changes between the
two reads, both are read again, up to three times, and then the dump fails. The check compares counts,
so a revoke and a grant on one account between the two reads can still pass it; take an exact dump while
nobody changes accounts. The export also names the accounts that were superusers and the accounts that
had no password on the source, because `SHOW USERS` reports both.

`--users` reads one account at a time with `SHOW GRANTS FOR name`. Use it against a server older than
`SHOW USERS` — which is often the server a migration dump is taken from — or to export only some
accounts. The two options cannot be combined.

The export is two blocks of SQL, and their order matters. `CREATE USER IF NOT EXISTS` comes first,
because a grant needs its account. The `GRANT` statements come last, because a grant resolves its
object to an immutable id and fails while the database or table is still missing. In a single file
the blocks sit at the top and at the bottom; under `--output-directory` they are written as
`users.sql` and `grants.sql`, to run around the per-database files:

```shell
camus-cli -c "Endpoint=http://db1.internal:5095;Database=test" -f backup/users.sql
camus-cli -c "Endpoint=http://db1.internal:5095;Database=test" -f backup/shop.sql
camus-cli -c "Endpoint=http://db1.internal:5095;Database=test" -f backup/grants.sql
```

Three limits come with it, and the export states each one in a comment at the top of the block:

- **No password is exported.** The server stores a salted verifier, never the password, and
  `CREATE USER` accepts cleartext only, so no statement can restore a login unchanged. Each account
  is created without a password. The server refuses every login to an account with no password, so a
  restored account stays closed until you set one with
  `ALTER USER name IDENTIFIED BY '…';`. It also means the dump file is no more sensitive than it
  already was.
- **Superuser status is not exported.** It is granted only by the bootstrap user at server start, and
  `SHOW GRANTS` does not report it, so an account that was a superuser comes back as an ordinary
  account.
- **An older server cannot list the accounts.** `--all-users` needs `SHOW USERS` and
  `SHOW GRANTS FOR *`, and camus-dump says so when the server rejects them. Against such a server,
  name the accounts with `--users`. A name that no account carries fails the dump rather than being
  passed over, because an account silently missing from the dump is the problem these options exist
  to prevent.

`--all-users` needs a superuser. `--users` needs one too, except for reading your own account.

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
| `-b`, `--batch` | Rows per `INSERT` statement (default `100`). `-b 1` is 12 to 41 times slower (the gap is largest on narrow rows), because the cost is one round trip per statement rather than the data. Raising it above 100 gains little; on a wide table it risks the server's 4 MiB gRPC message limit. |
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

Strings use CamusDB's two literal forms. The plain `'…'` form does no escape processing: a backslash is an ordinary character, and the only special sequence is a doubled quote. The `E'…'` escape form reads a backslash as an escape. A value goes into the escape form when it holds a control character, which the plain form cannot carry, **or a backslash**, and inside that form a backslash is always doubled and a quote is always written `''`, never `\'`.

That last rule is what makes the file reloadable, and it is worth stating why. A client reads the dump before the server does, and it cuts the file into statements at the `;` characters that stand outside a string. Such a splitter has to decide what closes a string: `camus-cli` reads `\'` as an escaped quote, the server reads it as a backslash followed by the closing quote. So a value that ends with a backslash has no plain spelling the two agree on — `'…\'` closes the string for the server and leaves it open for the splitter, which then swallows the statement terminator and merges the next statement into this one. The escape form with every backslash doubled ends at the same character under either reading, and the server decodes it to the original value.

The same rewrite is applied to the `CREATE TABLE` text the server reports, so a `DEFAULT` or a `COMMENT` ending in a backslash is written in the escape form as well.

Any string round-trips, including one holding a backslash, a trailing backslash, both quote characters, a newline, or a NUL.

Indexes — unique, multi-column, and covering indexes with `INCLUDE` columns — are dumped both inline in `CREATE TABLE` and as separate `CREATE INDEX IF NOT EXISTS` statements, so a dump taken with `--no-create-table` still carries them, and `--defer-indexes` can build them after the rows have loaded. The `IF NOT EXISTS` makes the separate statements a no-op when the table definition already created the index.

One thing has no CamusDB SQL literal, and `camus-dump` reports it rather than emitting something that would restore incorrectly:

- **Non-finite floats** — `NaN`, `+Infinity`, `-Infinity`. CamusDB's float literal has no form for them. The value is dumped as `NULL`.

It is counted and printed to standard error at the end of the run, and repeated as `-- WARNING` lines in the dump itself; `--strict` turns it into a failure instead. `DATETIME` values are truncated to milliseconds, the finest precision a CamusDB literal carries, and a dump that truncates one says so the same way.

## Contribution

`camus-dump` is an open-source project, and contributions are heartily welcomed! Whether you are looking to fix bugs, add new features, or improve documentation, your efforts and contributions will be appreciated. Check out the CONTRIBUTING.md file for guidelines on how to get started with contributing to `camus-dump`.

## License

`camus-dump` is released under the MIT License.
