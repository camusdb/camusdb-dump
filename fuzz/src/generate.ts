import { appendFileSync } from 'node:fs';

import { CamusClient, CamusError, createSequenceStatement } from 'camusdb';
import type { ParameterValue, Parameters } from 'camusdb';

type MutableParameters = Record<string, ParameterValue>;

import type { Endpoints, Finding, Protocol } from './catalog.js';
import { closeQuietly, errorCode, errorText, readRows, showColumns } from './catalog.js';
import { Random } from './random.js';
import type { ColumnDef, DatabaseDef, SequenceDef, TableDef } from './schema.js';
import { columnSql, createIndexSql, createSequenceSql, createTableSql, generateSchema, q } from './schema.js';
import type { ColType, Value } from './values.js';
import { describe, generateValue, literal, matchesWritten, parameter, stringLiteral } from './values.js';

export interface GenerateOptions {
  readonly seed: bigint;
  readonly endpoints: Endpoints;
  readonly databases: number;
  /** Every statement sent is appended here, so a failure can be replayed by hand. */
  readonly logFile?: string | undefined;
}

export interface GenerateReport {
  readonly seed: string;
  readonly databases: { name: string; protocol: Protocol; tables: number; sequences: number; rows: number }[];
  /** Statements the server refused, by error code. Expected: constraint violations and the like. */
  readonly rejected: Record<string, number>;
  /** Problems with the driver or the server that a dump cannot be blamed for. */
  readonly findings: Finding[];
}

/** A row as the fuzzer knows it. `undefined` marks a value the server chose (a default or a sequence). */
type RowModel = Map<string, Value | undefined>;

class TableState {
  readonly rows = new Map<string, RowModel>();
  readonly added = new Set<string>();

  constructor(readonly def: TableDef) {}

  liveColumns(): ColumnDef[] {
    return this.def.columns.filter((c) => !c.addedLater || this.added.has(c.name));
  }

  column(name: string): ColumnDef {
    return this.def.columns.find((c) => c.name === name)!;
  }
}

function pkText(type: ColType, value: unknown): string {
  if (typeof value === 'string') return type.kind === 'uuid' ? value.toLowerCase() : value;
  return String(value);
}

function rowKey(table: TableDef, row: RowModel): string | undefined {
  const parts: string[] = [];
  for (const pk of table.primaryKey) {
    const value = row.get(pk);
    if (value === undefined || value === null) return undefined;
    parts.push(pkText(table.columns.find((c) => c.name === pk)!.type, value));
  }
  return parts.join('\u0001');
}

export async function generate(options: GenerateOptions): Promise<GenerateReport> {
  const rng = new Random(options.seed);
  const schema = generateSchema(rng.fork('schema'), { databases: options.databases });
  const run = new Run(options, rng.fork('data'));

  for (const database of schema) await run.database(database);

  return {
    seed: options.seed.toString(),
    databases: run.summaries,
    rejected: Object.fromEntries([...run.rejected.entries()].sort()),
    findings: run.findings,
  };
}

class Run {
  readonly findings: Finding[] = [];
  /** Per row, the columns the INSERT left out and the fuzzer expects to hold their constant default. */
  private readonly defaulted = new WeakMap<RowModel, Set<string>>();
  /** Rows written with the typed row insert, client.insert(). */
  private readonly typedInserts = new WeakSet<RowModel>();
  readonly rejected = new Map<string, number>();
  readonly summaries: GenerateReport['databases'] = [];
  private setvalDone = false;

  constructor(
    private readonly options: GenerateOptions,
    private readonly rng: Random,
  ) {}

  private log(database: string, sql: string, parameters?: Parameters): void {
    if (!this.options.logFile) return;
    let line = `-- [${database}]\n${sql};\n`;
    if (parameters) {
      line += `-- parameters: ${JSON.stringify(parameters, (_, v: unknown) =>
        typeof v === 'bigint' ? `${v}n` : v instanceof Uint8Array ? Buffer.from(v).toString('hex') : v,
      )}\n`;
    }
    appendFileSync(this.options.logFile, line);
  }

  private reject(error: unknown): void {
    const code = errorCode(error);
    this.rejected.set(code, (this.rejected.get(code) ?? 0) + 1);
  }

  /**
   * Runs a statement whose refusal is a legitimate outcome, such as an insert that breaks a CHECK.
   * A CamusError is counted and swallowed. Anything else is the driver failing on valid input, and
   * becomes a finding.
   */
  private async attempt<T>(database: string, table: string | undefined, what: string, work: () => Promise<T>): Promise<T | undefined> {
    try {
      return await work();
    } catch (error) {
      if (CamusError.is(error)) {
        this.reject(error);
        return undefined;
      }
      this.findings.push({ kind: 'driver-exception', database, table, detail: `${what}: ${errorText(error)}` });
      return undefined;
    }
  }

  /**
   * Runs a statement that must succeed, through `execute`, which picks the route itself. When the
   * server refuses it there and `executeDdl` then runs it, the driver chose the wrong route: that is
   * a finding, and the run goes on. A refusal on both routes stops the run, because the generator
   * has a bug.
   */
  private async must(client: CamusClient, database: string, sql: string): Promise<void> {
    this.log(database, sql);
    try {
      await client.execute(sql);
    } catch (error) {
      if (!CamusError.is(error)) throw error;
      await client.executeDdl(sql);
      this.findings.push({
        kind: 'driver-route',
        database,
        detail: `execute() refused a statement that executeDdl() runs: ${errorText(error)}\n${sql}`,
      });
    }
  }

  async database(def: DatabaseDef): Promise<void> {
    const rng = this.rng.fork(def.name);
    const other: Protocol = def.protocol === 'rest' ? 'grpc' : 'rest';
    const client = new CamusClient({
      endpoint: def.protocol === 'rest' ? this.options.endpoints.rest : this.options.endpoints.grpc,
      database: def.name,
      protocol: def.protocol,
      int64: 'bigint',
      timeoutSeconds: 120,
    });

    try {
      // Both ways to create a database: the driver call and the SQL statement.
      this.log(def.name, `CREATE DATABASE ${q(def.name)}`);
      if (rng.chance(0.5)) await client.createDatabase(def.name);
      else await client.execute(`CREATE DATABASE ${q(def.name)}`);

      for (const sequence of def.sequences) await this.createSequence(client, def.name, sequence, rng);

      const tables: TableState[] = [];
      for (const table of def.tables) {
        await this.createTable(client, def.name, table);
        tables.push(new TableState(table));
      }

      const byName = new Map(tables.map((t) => [t.def.name, t]));

      for (const table of tables) await this.fill(client, def.name, table, byName, rng);
      for (const table of tables) await this.mutate(client, def.name, table, rng);
      for (const sequence of def.sequences) await this.drawSequence(client, def.name, sequence, rng);

      // The read-back goes through the other transport, so a value written over REST is read over
      // gRPC and the other way round.
      const reader = new CamusClient({
        endpoint: other === 'rest' ? this.options.endpoints.rest : this.options.endpoints.grpc,
        database: def.name,
        protocol: other,
        int64: 'bigint',
        timeoutSeconds: 120,
      });
      try {
        for (const table of tables) await this.verify(reader, def.name, table);
      } finally {
        await closeQuietly(reader);
      }

      this.summaries.push({
        name: def.name,
        protocol: def.protocol,
        tables: tables.length,
        sequences: def.sequences.length,
        rows: tables.reduce((sum, t) => sum + t.rows.size, 0),
      });
    } finally {
      await closeQuietly(client);
    }
  }

  private async createSequence(client: CamusClient, database: string, sequence: SequenceDef, rng: Random): Promise<void> {
    // The driver's statement builder has no CACHE, so a sequence with one is written by hand.
    const sql =
      sequence.cache === undefined && rng.chance(0.5)
        ? createSequenceStatement(sequence.name, {
            startWith: sequence.start,
            incrementBy: sequence.increment,
            minValue: sequence.min,
            maxValue: sequence.max,
          })
        : createSequenceSql(sequence);

    await this.must(client, database, sql);

    if (sequence.comment !== undefined)
      await this.must(client, database, `COMMENT ON SEQUENCE ${q(sequence.name)} IS ${stringLiteral(sequence.comment)}`);
  }

  private async createTable(client: CamusClient, database: string, table: TableDef): Promise<void> {
    const full = createTableSql(table);
    this.log(database, full);

    try {
      await client.execute(full);
    } catch (error) {
      if (!CamusError.is(error)) throw error;

      // The server refused something optional, such as an index shape. That is recorded, and the
      // table is created without its inline indexes and checks. The model forgets them too.
      this.findings.push({ kind: 'ddl-refused', database, table: table.name, detail: `${errorText(error)}\n${full}` });
      table.checks.splice(0);
      for (let i = table.indexes.length - 1; i >= 0; i--) if (table.indexes[i]!.inline) table.indexes.splice(i, 1);
      await this.must(client, database, createTableSql(table, true));
    }

    for (const index of table.indexes.filter((i) => !i.inline)) {
      const sql = createIndexSql(table, index);
      this.log(database, sql);
      try {
        await client.execute(sql);
      } catch (error) {
        if (!CamusError.is(error)) throw error;
        this.findings.push({ kind: 'ddl-refused', database, table: table.name, detail: `${errorText(error)}\n${sql}` });
        table.indexes.splice(table.indexes.indexOf(index), 1);
      }
    }

    for (const index of table.indexes.filter((i) => i.comment !== undefined))
      await this.must(client, database, `COMMENT ON INDEX ${q(table.name)}.${q(index.name)} IS ${stringLiteral(index.comment!)}`);

    if (table.comment && !table.comment.inline)
      await this.must(client, database, `COMMENT ON TABLE ${q(table.name)} IS ${stringLiteral(table.comment.text)}`);

    for (const column of table.columns.filter((c) => c.comment && !c.comment.inline && !c.addedLater))
      await this.must(client, database, `COMMENT ON COLUMN ${q(table.name)}.${q(column.name)} IS ${stringLiteral(column.comment!.text)}`);
  }

  // -------------------------------------------------------------------------------------------
  // Rows
  // -------------------------------------------------------------------------------------------

  private async fill(client: CamusClient, database: string, table: TableState, tables: Map<string, TableState>, rng: Random): Promise<void> {
    let remaining = table.def.rowTarget;
    const alterAt = Math.floor(remaining / 2);
    let altered = false;

    while (remaining > 0 || !altered) {
      if (!altered && remaining <= alterAt) {
        await this.addLateColumns(client, database, table);
        altered = true;
        if (remaining === 0) break;
      }

      const size = Math.min(remaining, rng.pick([1, 3, 10, 25, 50]));
      remaining -= size;

      const rows = this.planRows(table, tables, rng, size);
      const method = rng.pick(['literal', 'param', 'param', 'insert-api', 'transaction'] as const);
      await this.insertBatch(client, database, table, rows, method, rng);
    }
  }

  private async addLateColumns(client: CamusClient, database: string, table: TableState): Promise<void> {
    for (const column of table.def.columns.filter((c) => c.addedLater)) {
      const sql = `ALTER TABLE ${q(table.def.name)} ADD COLUMN ${columnSql(column)}`;
      this.log(database, sql);
      const done = await this.attempt(database, table.def.name, sql, () => client.execute(sql));
      if (done === undefined) continue;

      table.added.add(column.name);
      // Whether the server fills the rows it already holds with the default is not what this run tests.
      for (const row of table.rows.values()) row.set(column.name, undefined);

      if (column.comment && !column.comment.inline)
        await this.must(client, database, `COMMENT ON COLUMN ${q(table.def.name)}.${q(column.name)} IS ${stringLiteral(column.comment.text)}`);
    }
  }

  /**
   * Plans rows that share one column list, so they fit one INSERT. A column left out is filled by
   * the server: with its default, from its sequence, or with NULL.
   */
  private planRows(table: TableState, tables: Map<string, TableState>, rng: Random, count: number): { columns: ColumnDef[]; rows: RowModel[] } {
    const live = table.liveColumns();
    const fkColumns = new Set(table.def.foreignKeys.flatMap((fk) => fk.columns));

    const provided = live.filter((column) => {
      if (column.identity === 'always') return false;
      if (table.def.primaryKey.includes(column.name)) return column.default === undefined || rng.chance(0.3);
      if (column.identity) return rng.chance(0.15);
      if (column.default?.kind === 'nextval') return rng.chance(0.2);
      if (column.default) return rng.chance(0.5);
      return rng.chance(0.85);
    });

    const rows: RowModel[] = [];
    for (let i = 0; i < count; i++) {
      const row: RowModel = new Map();

      for (const column of live) {
        if (provided.includes(column)) continue;
        // What the server will store for a column the INSERT leaves out.
        if (column.default?.kind === 'const') {
          row.set(column.name, column.default.value);
          if (!this.defaulted.has(row)) this.defaulted.set(row, new Set());
          this.defaulted.get(row)!.add(column.name);
        }
        else if (column.default || column.identity) row.set(column.name, undefined);
        else row.set(column.name, null);
      }

      for (const column of provided) {
        if (fkColumns.has(column.name)) continue;
        if (column.identity) row.set(column.name, BigInt(rng.int(1, 1_000_000)));
        else row.set(column.name, generateValue(column.type, rng, column.bounds ?? {}));
      }

      for (const fk of table.def.foreignKeys) {
        const parent = tables.get(fk.parentTable)!;
        const candidates = [...parent.rows.values()].filter((r) => fk.parentColumns.every((c) => r.get(c) !== undefined));
        const target = candidates.length > 0 && rng.chance(0.75) ? rng.pick(candidates) : undefined;
        fk.columns.forEach((c, j) => {
          if (provided.some((p) => p.name === c)) row.set(c, target ? target.get(fk.parentColumns[j]!)! : null);
        });
      }

      rows.push(row);
    }

    return { columns: provided, rows };
  }

  private async insertBatch(
    client: CamusClient,
    database: string,
    table: TableState,
    batch: { columns: ColumnDef[]; rows: RowModel[] },
    method: 'literal' | 'param' | 'insert-api' | 'transaction',
    rng: Random,
  ): Promise<void> {
    const { columns, rows } = batch;
    const pkUnknown = rows.some((r) => rowKey(table.def, r) === undefined);

    // The typed row insert takes no RETURNING, so it is used only when the key is known up front.
    if (method === 'insert-api' && (pkUnknown || columns.length === 0)) method = 'param';

    // Every column would be filled by the server, and an INSERT needs at least one column.
    if (columns.length === 0) return;

    const returning = pkUnknown || rng.chance(0.2) ? ` RETURNING ${table.def.primaryKey.map(q).join(', ')}` : '';
    const head = `INSERT INTO ${q(table.def.name)} (${columns.map((c) => q(c.name)).join(', ')}) VALUES `;

    const paramStatement = (subset: RowModel[]) => {
      const parameters: MutableParameters = {};
      let n = 0;
      const tuples = subset.map(
        (row) =>
          `(${columns
            .map((c) => {
              const name = `p${n++}`;
              parameters[name] = parameter(c.type, row.get(c.name) as Value, rng);
              return `@${name}`;
            })
            .join(', ')})`,
      );
      return { sql: head + tuples.join(', ') + returning, parameters };
    };

    const literalStatement = (subset: RowModel[]) =>
      head + subset.map((row) => `(${columns.map((c) => literal(c.type, row.get(c.name) as Value)).join(', ')})`).join(', ') + returning;

    const record = (subset: RowModel[], returned?: Record<string, unknown>[]) => {
      subset.forEach((row, i) => {
        if (returned) {
          const back = returned[i];
          if (!back) return;
          for (const pk of table.def.primaryKey) if (row.get(pk) === undefined) row.set(pk, back[pk] as Value);
        }
        const key = rowKey(table.def, row);
        if (key !== undefined) table.rows.set(key, row);
      });
    };

    const run = async (subset: RowModel[], how: typeof method): Promise<boolean> => {
      switch (how) {
        case 'literal': {
          const sql = literalStatement(subset);
          this.log(database, sql);
          const result = returning ? await client.query(sql) : await client.execute(sql);
          record(subset, 'rows' in result ? result.rows : undefined);
          return true;
        }
        case 'param': {
          const { sql, parameters } = paramStatement(subset);
          this.log(database, sql, parameters);
          const result = returning ? await client.query(sql, parameters) : await client.execute(sql, parameters);
          record(subset, 'rows' in result ? result.rows : undefined);
          return true;
        }
        case 'insert-api': {
          // Each call is its own statement, so each row is recorded as soon as it is in: a later
          // row that fails does not take the earlier ones back out.
          for (const row of subset) {
            const values: MutableParameters = {};
            for (const c of columns) values[c.name] = parameter(c.type, row.get(c.name) as Value, rng);
            this.log(database, `-- client.insert(${table.def.name})`, values);
            await client.insert(table.def.name, values);
            this.typedInserts.add(row);
            record([row]);
          }
          return true;
        }
        case 'transaction': {
          const returnedRows: Record<string, unknown>[] = [];
          await client.transaction(async (txn) => {
            returnedRows.length = 0;
            for (const row of subset) {
              const { sql, parameters } = paramStatement([row]);
              this.log(database, `-- in a transaction\n${sql}`, parameters);
              if (returning) returnedRows.push(...(await client.query(sql, parameters, { transaction: txn })).rows);
              else await client.execute(sql, parameters, { transaction: txn });
            }
          });
          record(subset, returning ? returnedRows : undefined);
          return true;
        }
      }
    };

    const whole = await this.attempt(database, table.def.name, `${method} insert of ${rows.length} rows`, () => run(rows, method));
    if (whole || rows.length === 1) return;

    // One bad row fails the whole statement, so the rows go again one at a time and only the bad
    // ones are lost.
    for (const row of rows) {
      const key = rowKey(table.def, row);
      if (key !== undefined && table.rows.get(key) === row) continue;
      await this.attempt(database, table.def.name, 'single-row insert', () => run([row], 'param'));
    }
  }

  private keyWhere(table: TableState, row: RowModel, rng: Random, start = 0): { sql: string; parameters: Parameters } {
    const parameters: MutableParameters = {};
    const sql = table.def.primaryKey
      .map((pk, i) => {
        const name = `k${start + i}`;
        parameters[name] = parameter(table.column(pk).type, row.get(pk) as Value, rng);
        return `${q(pk)} = @${name}`;
      })
      .join(' AND ');
    return { sql, parameters };
  }

  /** Updates and deletes, including updates that close a cycle of self-references. */
  private async mutate(client: CamusClient, database: string, table: TableState, rng: Random): Promise<void> {
    const name = table.def.name;
    const updatable = table
      .liveColumns()
      .filter((c) => !table.def.primaryKey.includes(c.name) && c.identity !== 'always' && !c.references);

    for (let i = 0, n = Math.min(table.rows.size, rng.int(0, 8)); i < n && updatable.length > 0; i++) {
      const row = rng.pick([...table.rows.values()]);
      const column = rng.pick(updatable);
      const value = rng.chance(0.1) && !column.notNull ? null : generateValue(column.type, rng, column.bounds ?? {});
      const where = this.keyWhere(table, row, rng);
      const sql = `UPDATE ${q(name)} SET ${q(column.name)} = @v WHERE ${where.sql}`;
      const parameters = { ...where.parameters, v: parameter(column.type, value, rng) };
      this.log(database, sql, parameters);
      const done = await this.attempt(database, name, 'update', () => client.execute(sql, parameters));
      if (done !== undefined) row.set(column.name, value);
    }

    // Two rows that each reference the other: no order of separate INSERTs restores them.
    const self = table.def.foreignKeys.find((fk) => fk.parentTable === name);
    if (self && table.rows.size >= 2 && rng.chance(0.6)) {
      const [a, b] = rng.shuffle([...table.rows.values()]).slice(0, 2) as [RowModel, RowModel];
      for (const [child, parent] of [
        [a, b],
        [b, a],
      ] as const) {
        const where = this.keyWhere(table, child, rng);
        const column = table.column(self.columns[0]!);
        const target = parent.get(self.parentColumns[0]!) as Value;
        const sql = `UPDATE ${q(name)} SET ${q(column.name)} = @v WHERE ${where.sql}`;
        const parameters = { ...where.parameters, v: parameter(column.type, target, rng) };
        this.log(database, sql, parameters);
        const done = await this.attempt(database, name, 'cycle update', () => client.execute(sql, parameters));
        if (done !== undefined) child.set(column.name, target);
      }
    }

    for (let i = 0, n = Math.min(table.rows.size, rng.int(0, 3)); i < n; i++) {
      const [key, row] = rng.pick([...table.rows.entries()]);
      const where = this.keyWhere(table, row, rng);
      const sql = `DELETE FROM ${q(name)} WHERE ${where.sql}`;
      this.log(database, sql, where.parameters);
      // A parent row that a child references is refused; that is fine.
      const done = await this.attempt(database, name, 'delete', () => client.execute(sql, where.parameters));
      if (done !== undefined && done.affectedRows > 0) table.rows.delete(key);
    }
  }

  private async drawSequence(client: CamusClient, database: string, sequence: SequenceDef, rng: Random): Promise<void> {
    for (let i = 0; i < sequence.draws; i++) {
      this.log(database, `SELECT nextval(${stringLiteral(sequence.name)})`);
      await this.attempt(database, undefined, 'nextval', () => client.nextSequenceValue(sequence.name));
    }

    // A setval moves the counter past values that a restore must not hand out again. It costs about
    // five seconds on the server, so a run does it at most once.
    if (!this.setvalDone && rng.chance(0.5)) {
      this.setvalDone = true;
      const low = sequence.start ?? sequence.min ?? 1n;
      let target = low + BigInt(rng.int(10, 5000));
      if (sequence.max !== undefined && target > sequence.max) target = sequence.max;
      const sql = `SELECT setval(${stringLiteral(sequence.name)}, ${target})`;
      this.log(database, sql);
      await this.attempt(database, undefined, 'setval', () => client.query(sql));
    }
  }

  // -------------------------------------------------------------------------------------------
  // Read-back
  // -------------------------------------------------------------------------------------------

  /** Reads the table back and checks every value the fuzzer chose against what the driver returns. */
  private async verify(client: CamusClient, database: string, table: TableState): Promise<void> {
    const name = table.def.name;
    const { columns } = await showColumns(client, name);
    const cells = await readRows(client, name, columns, this.findings, { database });

    const index = new Map(columns.map((c, i) => [c.name.toLowerCase(), i]));
    const pkIndexes = table.def.primaryKey.map((pk) => index.get(pk.toLowerCase())!);
    const seen = new Set<string>();

    for (const row of cells) {
      const key = pkIndexes
        .map((i, j) => pkText(table.column(table.def.primaryKey[j]!).type, row[i]!.decoded))
        .join('\u0001');
      seen.add(key);

      const model = table.rows.get(key);
      if (!model) {
        this.findings.push({ kind: 'readback-extra-row', database, table: name, detail: `a row the fuzzer did not write: key ${JSON.stringify(key)}` });
        continue;
      }

      for (const [columnName, written] of model) {
        if (written === undefined) continue;
        const i = index.get(columnName.toLowerCase());
        const column = table.column(columnName);
        if (i === undefined) {
          this.findings.push({ kind: 'readback-missing-column', database, table: name, detail: columnName });
          continue;
        }
        const cell = row[i]!;
        if (!matchesWritten(column.type, written, cell.decoded, cell.text)) {
          const typedDefault = this.typedInserts.has(model) && this.defaulted.get(model)?.has(columnName) === true;
          this.findings.push({
            kind: typedDefault ? 'typed-insert-default' : 'readback-value',
            database,
            table: name,
            detail: `column ${columnName} (${column.typeSql}): key ${JSON.stringify(key)}, wrote ${describe(column.type, written)}, ` +
              `read ${String(cell.decoded instanceof Uint8Array ? Buffer.from(cell.decoded).toString('hex') : cell.decoded)} ` +
              `(server text ${JSON.stringify(cell.text)})`,
          });
        }
      }
    }

    for (const key of table.rows.keys()) {
      if (!seen.has(key))
        this.findings.push({ kind: 'readback-missing-row', database, table: name, detail: `the row with key ${JSON.stringify(key)} was written but not read back` });
    }
  }
}
