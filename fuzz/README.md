# camusdb-dump fuzz test

A nightly test that checks that `camus-dump` loses nothing. It also gives the
[camusdb-ts](https://github.com/camusdb/camusdb-ts) driver more testing, because the driver writes
and reads all the data.

The workflow is `.github/workflows/nightly-dump-fuzz.yml`. It runs every night, and you can start it
by hand with a seed to replay a run.

## What one run does

1. **Build.** `generate` makes 4 random databases on an empty server, through the camusdb-ts driver.
   Each database gets random tables with:
   - random column types, including arrays, type aliases, and mixed-case and keyword names
   - random primary keys: single, composite, and `OID DEFAULT(gen_id())`
   - constant and function defaults, `serial` and identity columns, and `DEFAULT(nextval(...))`
   - unique, multi-column, descending and covering indexes, declared inline or with `CREATE INDEX`
   - foreign keys to other tables and to the same table, including rows that reference each other
     in a cycle
   - `CHECK` constraints, comments on tables, columns, indexes and sequences
   - sequences with random options, values drawn with `nextval`, and a `setval`
   - edge values: the limits of every number type, `-0`, the smallest and largest dates, values
     below a millisecond, strings with quotes, backslashes, control characters, `NUL` and emoji,
     and values large enough to be stored out of line

   The rows go in through every write path of the driver: SQL literals, bound parameters, the typed
   row insert, transactions and `RETURNING`. REST and gRPC are both used. Rows that break a
   constraint are refused by the server and left out. Then some rows are updated and deleted.
2. **Dump.** `scripts/dump.sh` dumps every database twice: one file per database, and one file with
   `--all-databases`. `dump-plan` picks the options from the seed: the transport, the batch size,
   `--single-transaction` or `--no-as-of`, `--defer-indexes`, `--add-drop-table`, `--if-not-exists`
   and gzip.
3. **Record.** `snapshot` records the source: `SHOW CREATE TABLE`, `SHOW COLUMNS`, `SHOW INDEXES`,
   `SHOW SEQUENCES` and every row. Then a **probe** inserts two rows into each table with only the
   primary key. The other columns get their defaults, identities and sequence values. This finds a
   lost default that `SHOW CREATE TABLE` does not show.
4. **Verify.** For each of the two dumps, a separate job loads it into an empty server with
   `camus-cli`, records the restore the same way, and runs `compare`. Each database, table, column,
   index, sequence and row of the source must be in the restore with the same value, and the probe
   must give the same result.

## Driver checks

The run also checks the driver, independently of the dump:

| Finding | Meaning |
| --- | --- |
| `readback-value` | A value read back through the other transport is not the value that was written. |
| `readback-missing-row`, `readback-extra-row` | The rows in the table are not the rows that the driver reported as written. |
| `driver-decode` | The driver's JavaScript value disagrees with the server's own text form of the cell, `CAST(col AS string)`. |
| `driver-exception` | The driver threw something other than a `CamusError` for valid input. |
| `driver-route` | `execute()` sent a statement to a route that refused it, and `executeDdl()` ran it. |
| `typed-insert-default` | `client.insert()` stored `NULL` in a column that an `INSERT` would fill with its default. |
| `ddl-refused` | The server refused a generated DDL statement. This is reported, but it does not fail the run. |

A run fails when the dump loses or changes something, or when a driver check has a finding.

## Running it on your machine

You need Node.js 22, the .NET 10 SDK, `camus-cli`, two empty CamusDB servers, and a checkout of
camusdb-ts next to this repository (`../camusdb-ts`), built with `npm ci && npm run build`.

```shell
cd fuzz
npm ci
npm run build

# Server A (source) on 5095/5096
node dist/cli.js generate --seed 42 --out run
CAMUS_DUMP="dotnet ../CamusDB.Dump/bin/Release/net10.0/CamusDB.Dump.dll" \
  scripts/dump.sh run/generate-report.json 42 run/dumps
node dist/cli.js snapshot --label source --probe --out run/source.json

# Server B (empty) on 6095/6096
RESTORE_ENDPOINT=http://localhost:6096 scripts/restore.sh run/dumps/per-database/*
node dist/cli.js snapshot --rest http://localhost:6095 --grpc http://localhost:6096 \
  --label restore --probe --out run/restore.json

node dist/cli.js compare --expected run/source.json --actual run/restore.json
node dist/cli.js findings --allow ddl-refused --file run/generate-report.json --file run/source.json
```

`run/generated.sql` holds every statement that `generate` sent, with its parameters, so you can
replay a failure by hand.
