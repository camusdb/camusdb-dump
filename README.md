# CamusDB Dump

`camus-dump` makes logical backups of a [CamusDB](https://github.com/camusdb/camusdb) database. It writes a SQL script that recreates the installed C# extensions, schemas, indexes, sequences and rows, and that any CamusDB SQL client can replay.

- **Consistent by default.** Every table is read at the same instant, so the dump never mixes data from different moments.
- **Restores in one pass.** Tables and rows are ordered so foreign keys hold, and sequences resume where they left off.
- **Exact values.** Every CamusDB type round-trips, including full-precision floats, `NUMERIC`, `BYTES` and sub-millisecond `DATETIME`.
- **Whole servers.** Dump every database, plus user accounts and grants, in one run.

## Installation

`camus-dump` is a .NET tool and needs the [.NET 10 SDK](https://dotnet.microsoft.com/download):

```shell
dotnet tool install --global CamusDB.Dump
```

## Quick start

```shell
# Dump a database to a file
camus-dump --endpoint http://localhost:5096 --database mydb -o mydb.sql

# Restore it
camus-cli mydb -f mydb.sql
```

`camus-cli` is the CamusDB SQL shell. Get it from [camusdb/camussqlsh](https://github.com/camusdb/camussqlsh).

More examples:

```shell
# One table, to standard output
camus-dump -e http://localhost:5096 -d mydb -t orders

# Compressed (gzip is picked from the .gz extension)
camus-dump -e http://localhost:5096 -d mydb -o mydb.sql.gz
gunzip -c mydb.sql.gz | camus-cli mydb -f -

# The database as it was five minutes ago, replayable onto an existing schema
camus-dump -e http://localhost:5096 -d mydb --as-of -5m --if-not-exists -o backup.sql

# Every database, with accounts and grants, one file each under backup/
camus-dump -e https://db1.internal:5096 -u admin -A --all-users --output-directory backup/

# Straight to object storage
set -o pipefail
camus-dump -e https://db1.internal:5096 -d mydb -z gzip | aws s3 cp - s3://backups/mydb.sql.gz
```

## Options

### Connecting

| Option | Description |
| --- | --- |
| `-e`, `--endpoint` | Server endpoint, or a comma-separated pool (default `http://localhost:5096`, the gRPC port). |
| `-d`, `--database` | Database to dump (default `test`). |
| `-c`, `--connection-source` | Full connection string. The other options fill in only the keys it does not set. |
| `--protocol` | `grpc` (default) or `rest`. The endpoint port must match the protocol. |
| `--timeout` | Per-statement timeout in seconds (default `10`). |

### Authentication

Only needed when the server runs with `CAMUSDB_AUTH_ENABLED=true`. The dump only reads, so `SELECT` and `SHOW` privileges are enough.

| Option | Description |
| --- | --- |
| `-u`, `--user` | User to authenticate as. |
| `-W`, `--ask-password` | Prompt for the password. |
| `-p`, `--password` | Password. Prefer `CAMUSDB_PASSWORD` or `-W`: a command-line password is visible in the process list. |
| `--access-token` | Bearer token to use instead of a login. Prefer `CAMUSDB_ACCESS_TOKEN`. |
| `--token-lifetime` | Seconds to reuse a token when the server reports no expiry (default `600`). |

```shell
CAMUSDB_PASSWORD=app-secret camus-dump -e https://camus.internal:5096 -d mydb -u app
```

Credentials are refused over plain `http://` to anything other than localhost. Use an `https://` endpoint, or pass `-c "AllowInsecureCredentials=true"` when a VPN or SSH tunnel already protects the link.

### Choosing what to dump

| Option | Description |
| --- | --- |
| `-A`, `--all-databases` | Dump every database on the server. |
| `-X`, `--exclude-database` | With `-A`, skip these databases. |
| `-t`, `--table` | Dump only these tables (comma-separated, or repeat the option). |
| `-x`, `--exclude-table` | Skip these tables. |
| `-w`, `--where` | Dump only rows that match this SQL condition. It is passed as written, so never build it from untrusted input. |
| `--all-users` | Also export every account and grant. Needs a superuser. |
| `--users` | Also export these accounts and their grants. Works with older servers. |
| `--no-create-table` | Leave out `CREATE EXTENSION`, `CREATE TABLE` and `CREATE SEQUENCE`. |
| `--no-data` | Leave out the rows. |
| `--no-indexes` | Leave out the separate `CREATE INDEX` statements. |

### Point in time

| Option | Description |
| --- | --- |
| `--as-of` | Read every table as of this instant: a negative offset (`-5m`, `-1h`), a UTC timestamp, or Unix epoch milliseconds. |
| `--no-as-of` | Read the latest committed data, with no consistency across tables. |
| `--single-transaction` | Read every table from one serializable transaction instead. Cannot be combined with `--as-of`. |

By default, the dump reads every table as of one second before it starts, and records that instant in its header. Only rows travel back in time. Table definitions and indexes are always the current ones.

### Output

| Option | Description |
| --- | --- |
| `-o`, `--output` | Write to this file instead of standard output. |
| `--output-directory` | Write one `<database>.sql` per database into this directory. |
| `-z`, `--compress` | `gzip` or `none`. Defaults to `gzip` when the `-o` file name ends in `.gz`. |
| `-b`, `--batch` | Rows per `INSERT` (default `100`). Higher values gain little and can exceed the server's 4 MiB message limit on wide tables. |
| `--add-drop-table` | Drop every dumped table before it is created. |
| `--if-not-exists` | Use `CREATE TABLE IF NOT EXISTS`, to replay onto an existing schema. |
| `--create-database` | Add `CREATE DATABASE IF NOT EXISTS` and `USE`. Implied by `-A`. |
| `--defer-indexes` | Create each table's indexes after its rows instead of before. |
| `--strict` | Fail on a value that has no SQL literal (`NaN`, `±Infinity`) instead of dumping it as `NULL` with a warning. |
| `--no-header` | Leave out the comment header and the `-- Dump completed` footer. |
| `--no-progress` | Hide the progress bars shown when writing to a file. |

Output files are created readable by their owner only.

## Restoring

Feed the dump to a CamusDB SQL client such as [`camus-cli`](https://github.com/camusdb/camussqlsh).

- **Multiple databases in one file** need a client that understands `USE`, like `camus-cli`. Otherwise, dump with `--output-directory` and restore each file with `-d`.
- **Accounts and grants** go in two blocks: accounts before the data and grants after it. With `--output-directory` they are `users.sql` and `grants.sql`:

  ```shell
  camus-cli -c "Endpoint=http://db1.internal:5095;Database=test" -f backup/users.sql
  camus-cli -c "Endpoint=http://db1.internal:5095;Database=test" -f backup/shop.sql
  camus-cli -c "Endpoint=http://db1.internal:5095;Database=test" -f backup/grants.sql
  ```

  Passwords and superuser status are not exported. A restored account cannot log in until you run `ALTER USER name IDENTIFIED BY '…';`.
- **A complete dump** ends with one `-- Dump completed` comment per database. Check for it when a dump was streamed somewhere that keeps partial uploads.

## Limitations

- Sequence positions and table definitions are read when the dump starts, not at `--as-of`.
- A `--where` condition or a `--table` filter can leave out rows or tables that the dumped rows reference. The restore then needs them to already exist. `camus-dump` warns when a filtered-out table is referenced.
- Each restored sequence position takes about five seconds on the server.

For the full behavior of each feature, see [How camus-dump works](https://github.com/camusdb/camusdb-dump/blob/main/docs/how-it-works.md).

## Contributing

Contributions are welcome: bug fixes, new features or better documentation. Open an issue or a pull request on [GitHub](https://github.com/camusdb/camusdb-dump).

## License

`camus-dump` is released under the [MIT License](LICENSE).
