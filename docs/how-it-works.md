# How camus-dump works

This page describes in detail how `camus-dump` behaves, and why. For installation and the list of options, see the [README](../README.md).

## Authentication

A password or a token given on the command line is visible to every local user through the process list, for as long as the dump runs. `CAMUSDB_PASSWORD`, `CAMUSDB_ACCESS_TOKEN` and `--ask-password` avoid that.

The password is exchanged once for a short-lived bearer token, which the driver renews on its own; the password itself never travels with a statement. The dump only reads, so `SELECT` and `SHOW` privileges on the dumped tables are enough. Credentials are refused over plaintext outside loopback — use an `https://` endpoint. The driver refuses them itself, with `CADB0519`, before anything is sent; the server refuses them as well. Where the link is protected some other way, such as a VPN or an SSH tunnel, pass `-c "AllowInsecureCredentials=true"` to accept that. The rows themselves carry no such rule, so camus-dump warns when it sends a dump unencrypted to another host.

Authentication works the same over gRPC (`--protocol grpc`): the exchange rides the server's `CamusAuth` service on the channel that carries the statements, so no HTTP port has to be exposed just to obtain a token.

## Dumping every database

`--all-databases` asks the server for its databases with `SHOW DATABASES` and dumps each one in turn, skipping anything `--exclude-database` names:

```shell
# Every database, as one stream of sections
camus-dump -A -o server.sql

# Every database except two, one file per database under backup/
camus-dump -A -X scratch,tempdb --output-directory backup/
```

Every database is read as of the same instant, since the point in time is fixed once before the first statement goes out. `--single-transaction` is the exception: a transaction belongs to a connection and each database gets its own, so it makes each database internally consistent but does not tie them to a common snapshot.

Each section opens with `CREATE DATABASE IF NOT EXISTS` and a `USE`, whether or not `--create-database` was passed, so one file restores every database in turn. `USE` is not server-side SQL — CamusDB's parser rejects it — but a client reads it and points the statements that follow at that database, which is how [`camus-cli`](https://github.com/camusdb/camussqlsh) takes the whole file. Against a client that does not, dump with `--output-directory`: it writes `<database>.sql` per database, so each file goes back on its own with `-d` pointing at the matching database.

The other options apply per database. `-t`/`-x` match table names in every one of them, and `-w` filters rows in every table it names — a condition that references a column only some tables have will fail on the others.

## Users and grants

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

## Point in time

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

## Compression and output files

A compressed dump is a plain gzip file. Restore it by decompressing it into the client:

```shell
camus-dump -e http://localhost:5096 -d mydb -o mydb.sql.gz
gunzip -c mydb.sql.gz | camus-cli mydb -f -
```

camus-dump refuses to write compressed bytes to a terminal; redirect standard output, or pass `-o`. The same stream goes straight to object storage, which the cloud CLIs read from standard input:

```shell
set -o pipefail
camus-dump -e https://db1.internal:5096 -d mydb -z gzip | gcloud storage cp - gs://backups/mydb.sql.gz
camus-dump -e https://db1.internal:5096 -d mydb -z gzip | aws s3 cp - s3://backups/mydb.sql.gz
```

Both CLIs keep whatever they received when the dump fails partway, so the object can be a truncated dump that still decompresses cleanly. `pipefail` reports the failure in the exit status. Afterwards, the `-- Dump completed` comment tells a finished database from a cut one: it closes each database's section, so a dump of N databases holds N of them. `--no-header` leaves it out. Above about 50 GB, `aws s3 cp -` also needs `--expected-size`.

A dump holds every row of the database, so `-o` and `--output-directory` create files that only their owner can read or write, and `--output-directory` creates the directory the same way. An existing directory keeps the permissions it has; camus-dump warns when other users can write to it. A path that is already a symbolic link is refused rather than followed, because the dump would truncate whatever is at the far end.

## Data types and indexes

Every type CamusDB stores is dumped as a literal that parses back to the same value: `OID`, `STRING`, `INT64`, `FLOAT64`, `FLOAT32`, `NUMERIC` (as the exact typed literal `NUMERIC '…'`), `BOOL`, `BYTES` (as `X'…'`), `DATE`, `DATETIME`, `UUID`, `ARRAY` (as `ARRAY[…]`) and `NULL`.

Strings use CamusDB's two literal forms. The plain `'…'` form does no escape processing: a backslash is an ordinary character, and the only special sequence is a doubled quote. The `E'…'` escape form reads a backslash as an escape. A value goes into the escape form when it holds a control character, which the plain form cannot carry, **or a backslash**, and inside that form a backslash is always doubled and a quote is always written `''`, never `\'`.

That last rule is what makes the file reloadable, and it is worth stating why. A client reads the dump before the server does, and it cuts the file into statements at the `;` characters that stand outside a string. Such a splitter has to decide what closes a string: `camus-cli` reads `\'` as an escaped quote, the server reads it as a backslash followed by the closing quote. So a value that ends with a backslash has no plain spelling the two agree on — `'…\'` closes the string for the server and leaves it open for the splitter, which then swallows the statement terminator and merges the next statement into this one. The escape form with every backslash doubled ends at the same character under either reading, and the server decodes it to the original value.

The same rewrite is applied to the `CREATE TABLE` text the server reports, so a `DEFAULT` or a `COMMENT` ending in a backslash is written in the escape form as well.

Any string round-trips, including one holding a backslash, a trailing backslash, both quote characters, a newline, or a NUL.

Indexes — unique, multi-column, and covering indexes with `INCLUDE` columns — are dumped both inline in `CREATE TABLE` and as separate `CREATE INDEX IF NOT EXISTS` statements, so a dump taken with `--no-create-table` still carries them, and `--defer-indexes` can build them after the rows have loaded. The `IF NOT EXISTS` makes the separate statements a no-op when the table definition already created the index.

One thing has no CamusDB SQL literal, and `camus-dump` reports it rather than emitting something that would restore incorrectly:

- **Non-finite floats** — `NaN`, `+Infinity`, `-Infinity`. CamusDB's float literal has no form for them. The value is dumped as `NULL`.

It is counted and printed to standard error at the end of the run, and repeated as `-- WARNING` lines in the dump itself; `--strict` turns it into a failure instead. `DATETIME` values keep their full precision of 100 nanoseconds: a value below a whole millisecond is written with seven fractional digits.

## Sequences

The dump restores every sequence, and the position it has reached:

- **A free-standing sequence** (`CREATE SEQUENCE`) is created before the first table, with every option spelled out, because a column default can draw from it. Its comment follows.
- **A sequence owned by an identity or `serial` column** is created by that column's `CREATE TABLE`. After the table, the dump renames it when the source used another name, restores options that differ from an identity's, and restores its comment.
- **The position** is restored with `SELECT setval('name', n)` after the rows, where `n` is the `reserved_upto` that `SHOW SEQUENCES` reports. A row restored with an explicit value does not advance a sequence, so without this a restored sequence would hand out values that restored rows already hold. A sequence nothing has been drawn from has no position to restore.

Notes:

- **Positions are current.** `SHOW SEQUENCES` has no time-travel form, so the position is read when the dump starts, not as of `--as-of`. It is never below the position at that instant, so a restored sequence never reissues a value a dumped row holds.
- **A `setval` takes about five seconds on the server.** A dump with many drawn sequences takes that much longer to restore.
- **With `--table` or `--exclude-table`**, the dump carries the sequences the dumped tables own and the free-standing sequences their column defaults draw from.
- `--no-create-table` leaves out the `CREATE SEQUENCE` statements, `--no-data` the positions, `--if-not-exists` adds `IF NOT EXISTS`, and `--add-drop-table` drops the free-standing sequences after the tables.
- **`GENERATED ALWAYS AS IDENTITY`** refuses a supplied value, and the dump supplies every value, so the `INSERT` statements of such a table say `OVERRIDING SYSTEM VALUE`, as pg_dump's do. The restored rows keep their identity values.
- A column default that draws from a sequence, and an identity column, reach the dump only through `SHOW CREATE TABLE`. A server older than the release that renders them there loses both, and the restored column has no default.

## Foreign keys

The server checks a foreign key when the statement that writes a row ends. A table that references another table can only be created after that table exists, and its rows can only be inserted after the rows they reference. So camus-dump writes the dump in that order:

- **Tables.** Each table comes after every table it references. Tables that have no foreign key between them keep the order of `SHOW TABLES`. The server refuses a cycle of references between tables, so this order always exists.
- **Rows of a table that references itself.** Each row comes after the row it references, or in the same `INSERT`. A `NULL` in any referencing column means no reference. To put the rows in this order, camus-dump reads the whole table into memory before it writes the first row. Every other table is streamed.
- **Rows that reference each other in a cycle**, such as two employees who are each the other's manager. No order of separate statements is valid for them. They go into one `INSERT` at the end of the table, which ignores `--batch`, and camus-dump prints a warning.
- **Drops.** With `--add-drop-table`, all the `DROP TABLE IF EXISTS` statements come first, child tables before parent tables. The server refuses to drop a table that another table references.

camus-dump reads the foreign keys from the `SHOW CREATE TABLE` text, the only place where the server reports them. Against a server that does not render them there, the dump keeps the order of `SHOW TABLES`.

Some limits remain:

- When `--table` or `--exclude-table` leaves out a table that a dumped table references, camus-dump prints a warning. The load then needs that table to exist already, with the referenced rows.
- A `--where` condition can leave out a parent row while it keeps a child row. The load then fails on the child row.
- With `--add-drop-table`, a drop fails when the target database has a table that is not in the dump and references a dumped table.
