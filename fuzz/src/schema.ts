import type { Random } from './random.js';
import type { ColType, ScalarKind, Value, ValueBounds } from './values.js';
import { ARRAY_ELEMENT_KINDS, canonicalTypeSql, descendable, generateValue, indexable, literal, stringLiteral, typeSql } from './values.js';

export type ColumnDefault =
  | { readonly kind: 'const'; readonly value: Value }
  | { readonly kind: 'fn'; readonly fn: 'gen_id' | 'gen_uuid_v7' | 'now' }
  | { readonly kind: 'nextval'; readonly sequence: string };

export type Identity = 'serial' | 'always' | 'by-default';

export interface ColumnDef {
  readonly name: string;
  readonly type: ColType;
  readonly typeSql: string;
  readonly notNull: boolean;
  readonly default?: ColumnDefault | undefined;
  readonly identity?: Identity | undefined;
  /** Written inline in CREATE TABLE, or later with COMMENT ON COLUMN. */
  readonly comment?: { readonly text: string; readonly inline: boolean } | undefined;
  readonly bounds?: ValueBounds | undefined;
  /** Set on a column that exists only to reference another table. */
  readonly references?: { readonly table: string; readonly column: string } | undefined;
  /** Added with ALTER TABLE ADD COLUMN after some rows exist. */
  readonly addedLater?: boolean | undefined;
}

export interface IndexDef {
  readonly name: string;
  readonly unique: boolean;
  readonly columns: readonly { readonly name: string; readonly desc: boolean }[];
  readonly include: readonly string[];
  readonly comment?: string | undefined;
  /** Declared inside CREATE TABLE rather than with a separate CREATE INDEX. */
  readonly inline: boolean;
}

export interface ForeignKeyDef {
  readonly name?: string | undefined;
  readonly columns: readonly string[];
  readonly parentTable: string;
  readonly parentColumns: readonly string[];
}

export interface CheckDef {
  readonly name?: string | undefined;
  readonly sql: string;
}

export interface TableDef {
  readonly name: string;
  readonly columns: ColumnDef[];
  readonly primaryKey: readonly string[];
  readonly indexes: IndexDef[];
  readonly foreignKeys: ForeignKeyDef[];
  readonly checks: CheckDef[];
  readonly comment?: { readonly text: string; readonly inline: boolean } | undefined;
  readonly rowTarget: number;
}

export interface SequenceDef {
  readonly name: string;
  readonly start?: bigint | undefined;
  readonly increment?: bigint | undefined;
  readonly min?: bigint | undefined;
  readonly max?: bigint | undefined;
  readonly cache?: number | undefined;
  readonly comment?: string | undefined;
  /** How many values to draw with nextval after the rows are in. */
  readonly draws: number;
}

export interface DatabaseDef {
  readonly name: string;
  readonly protocol: 'rest' | 'grpc';
  readonly sequences: SequenceDef[];
  readonly tables: TableDef[];
}

/** Quotes an identifier. Every name the fuzzer makes is a plain identifier, so this cannot fail. */
export function q(name: string): string {
  return '`' + name.replaceAll('`', '``') + '`';
}

const WORDS = [
  'alpha', 'beta', 'gamma', 'delta', 'orders', 'items', 'users', 'events', 'ledger', 'notes', 'tags',
  'price', 'total', 'name', 'status', 'data', 'value', 'created', 'payload', 'amount', 'flag', 'score',
];

/** Table names that are SQL keywords. The server takes them quoted; a dump must keep them quoted. */
const KEYWORD_TABLES = ['order', 'key', 'value', 'table', 'select'];

const SCALARS: readonly ScalarKind[] = [
  'int64', 'int64', 'float64', 'float32', 'numeric', 'bool', 'string', 'string', 'bytes', 'date', 'datetime', 'uuid', 'oid',
];

export interface SchemaOptions {
  readonly databases: number;
}

export function generateSchema(rng: Random, options: SchemaOptions): DatabaseDef[] {
  const databases: DatabaseDef[] = [];

  for (let i = 1; i <= options.databases; i++) {
    const r = rng.fork(`db${i}`);
    const name = r.chance(0.25) ? `FuzzMixed_${i}` : `fuzz_${i}`;

    // One database in a while has no tables at all: the dump still has to recreate it.
    const empty = r.chance(0.1);
    databases.push(generateDatabase(r, name, empty));
  }

  return databases;
}

function generateDatabase(rng: Random, name: string, empty: boolean): DatabaseDef {
  const protocol = rng.chance(0.5) ? 'rest' : 'grpc';
  const sequences: SequenceDef[] = [];
  const tables: TableDef[] = [];

  if (empty) return { name, protocol, sequences, tables };

  for (let i = 0, n = rng.int(0, 3); i < n; i++) sequences.push(generateSequence(rng, `seq_${i}_${rng.pick(WORDS)}`));

  const used = new Set<string>();
  for (let i = 0, n = rng.int(1, 6); i < n; i++) {
    let tableName: string;
    if (rng.chance(0.12)) tableName = rng.pick(KEYWORD_TABLES);
    else if (rng.chance(0.15)) tableName = `Tbl${i}_${capitalize(rng.pick(WORDS))}`;
    else tableName = `t${i}_${rng.pick(WORDS)}`;

    if (used.has(tableName.toLowerCase())) tableName = `t${i}_${rng.pick(WORDS)}_${i}`;
    used.add(tableName.toLowerCase());

    tables.push(generateTable(rng.fork(tableName), tableName, tables, sequences));
  }

  return { name, protocol, sequences, tables };
}

function generateSequence(rng: Random, name: string): SequenceDef {
  const increment = rng.chance(0.6) ? undefined : BigInt(rng.pick([1, 2, 3, 7, 100]));
  const min = rng.chance(0.7) ? undefined : BigInt(rng.pick([1, 10, 1000]));
  const start = rng.chance(0.5) ? undefined : (min ?? 1n) + BigInt(rng.int(0, 5000));
  const max = rng.chance(0.7) ? undefined : (start ?? min ?? 1n) + BigInt(rng.int(100000, 10000000));
  const cache = rng.chance(0.7) ? undefined : rng.pick([1, 5, 50]);

  return {
    name,
    start,
    increment,
    min,
    max,
    cache,
    comment: rng.chance(0.3) ? randomComment(rng) : undefined,
    draws: rng.pick([0, 0, 1, 3]),
  };
}

export function createSequenceSql(sequence: SequenceDef): string {
  let sql = `CREATE SEQUENCE ${q(sequence.name)}`;
  if (sequence.start !== undefined) sql += ` START WITH ${sequence.start}`;
  if (sequence.increment !== undefined) sql += ` INCREMENT BY ${sequence.increment}`;
  if (sequence.min !== undefined) sql += ` MINVALUE ${sequence.min}`;
  if (sequence.max !== undefined) sql += ` MAXVALUE ${sequence.max}`;
  if (sequence.cache !== undefined) sql += ` CACHE ${sequence.cache}`;
  return sql;
}

function generateTable(rng: Random, name: string, earlier: readonly TableDef[], sequences: readonly SequenceDef[]): TableDef {
  const columns: ColumnDef[] = [];
  const names = new Set<string>();
  const columnName = (base: string) => {
    let n = rng.chance(0.15) ? `Col${capitalize(base)}` : base;
    while (names.has(n.toLowerCase())) n = `${n}_${names.size}`;
    names.add(n.toLowerCase());
    return n;
  };

  // The primary key.
  const pkShape = rng.pick(['int64', 'int64', 'string', 'uuid', 'oid', 'oid-default', 'composite'] as const);
  const primaryKey: string[] = [];

  const pkColumn = (kind: ScalarKind, extra: Partial<ColumnDef> = {}) => {
    const type: ColType = kind === 'string' ? { kind, length: rng.pick([16, 40, 255]) } : { kind };
    const column: ColumnDef = { name: columnName('id'), type, typeSql: typeSql(type, rng), notNull: true, ...extra };
    columns.push(column);
    primaryKey.push(column.name);
  };

  switch (pkShape) {
    case 'composite':
      pkColumn('int64');
      pkColumn('string');
      break;
    case 'oid-default':
      pkColumn('oid', { default: { kind: 'fn', fn: 'gen_id' } });
      break;
    default:
      pkColumn(pkShape);
  }

  // Ordinary columns.
  for (let i = 0, n = rng.int(1, 9); i < n; i++) columns.push(generateColumn(rng, columnName(`c${i}_${rng.pick(WORDS)}`), sequences));

  // Foreign keys: to an earlier table, and now and then to this table itself.
  const foreignKeys: ForeignKeyDef[] = [];
  for (const parent of rng.subset(earlier, 0.35)) {
    const childColumns = parent.primaryKey.map((pk) => {
      const parentColumn = parent.columns.find((c) => c.name === pk)!;
      const column: ColumnDef = {
        name: columnName(`ref_${parent.name.toLowerCase()}_${pk.toLowerCase()}`),
        type: parentColumn.type,
        typeSql: typeSql(parentColumn.type, rng),
        notNull: false,
        references: { table: parent.name, column: pk },
      };
      columns.push(column);
      return column.name;
    });
    foreignKeys.push({
      name: rng.chance(0.5) ? `fk_${name.toLowerCase()}_${foreignKeys.length}` : undefined,
      columns: childColumns,
      parentTable: parent.name,
      parentColumns: parent.primaryKey,
    });
  }

  if (primaryKey.length === 1 && rng.chance(0.25)) {
    const pk = columns.find((c) => c.name === primaryKey[0])!;
    const column: ColumnDef = {
      name: columnName('parent_id'),
      type: pk.type,
      typeSql: typeSql(pk.type, rng),
      notNull: false,
      references: { table: name, column: pk.name },
    };
    columns.push(column);
    foreignKeys.push({
      name: rng.chance(0.5) ? `fk_${name.toLowerCase()}_self` : undefined,
      columns: [column.name],
      parentTable: name,
      parentColumns: [pk.name],
    });
  }

  // Columns added with ALTER TABLE after the first rows.
  for (let i = 0, n = rng.chance(0.2) ? rng.int(1, 2) : 0; i < n; i++) {
    const column = generateColumn(rng, columnName(`late${i}_${rng.pick(WORDS)}`), []);
    if (column.identity || column.notNull) continue;
    columns.push({ ...column, addedLater: true });
  }

  const checks = generateChecks(rng, columns, primaryKey);
  const indexes = generateIndexes(rng, name, columns, primaryKey);

  return {
    name,
    columns,
    primaryKey,
    indexes,
    foreignKeys,
    checks,
    comment: rng.chance(0.3) ? { text: randomComment(rng), inline: rng.chance(0.5) } : undefined,
    rowTarget: rng.pick([0, 1, 5, 20, 60, 150, 300]),
  };
}

function generateColumn(rng: Random, name: string, sequences: readonly SequenceDef[]): ColumnDef {
  const isArray = rng.chance(0.1);
  const kind = rng.pick(SCALARS);
  const type: ColType = isArray
    ? { kind: 'array', element: rng.pick(ARRAY_ELEMENT_KINDS) }
    : kind === 'string'
      ? { kind, length: rng.chance(0.5) ? undefined : rng.pick([1, 8, 40, 255, 3000]) }
      : { kind };

  const comment = rng.chance(0.15) ? { text: randomComment(rng), inline: rng.chance(0.5) } : undefined;

  // An identity, or a default drawn from a free-standing sequence.
  if (type.kind === 'int64' && rng.chance(0.25)) {
    const roll = rng.float();
    if (roll < 0.15)
      return { name, type, typeSql: 'serial', notNull: true, identity: 'serial', comment };
    if (roll < 0.3)
      return { name, type, typeSql: typeSql(type, rng), notNull: true, identity: rng.pick(['always', 'by-default'] as const), comment };
    if (sequences.length > 0)
      return {
        name,
        type,
        typeSql: typeSql(type, rng),
        notNull: false,
        default: { kind: 'nextval', sequence: rng.pick(sequences).name },
        comment,
      };
  }

  let def: ColumnDefault | undefined;
  if (rng.chance(0.25)) {
    if (type.kind === 'oid' && rng.chance(0.5)) def = { kind: 'fn', fn: 'gen_id' };
    else if (type.kind === 'uuid' && rng.chance(0.5)) def = { kind: 'fn', fn: 'gen_uuid_v7' };
    else if (type.kind === 'datetime' && rng.chance(0.5)) def = { kind: 'fn', fn: 'now' };
    else if (type.kind !== 'array') def = { kind: 'const', value: generateValue(type, rng) };
  }

  // A NOT NULL column always gets a default, so a row that leaves it out is still valid.
  const notNull = def !== undefined && def.kind === 'const' && def.value !== null && rng.chance(0.4);

  let bounds: ValueBounds | undefined;
  if (type.kind === 'int64' && rng.chance(0.2)) bounds = { min: BigInt(rng.int(-1000, 0)), max: BigInt(rng.int(1, 1_000_000)) };
  else if (type.kind === 'float64' && rng.chance(0.2)) bounds = { min: -rng.int(0, 1000) - 0.5 };
  else if (type.kind === 'string' && rng.chance(0.2)) bounds = { maxLength: rng.int(1, 30) };

  return { name, type, typeSql: typeSql(type, rng), notNull, default: def, comment, bounds };
}

function generateChecks(rng: Random, columns: readonly ColumnDef[], primaryKey: readonly string[]): CheckDef[] {
  const checks: CheckDef[] = [];
  const named = () => (rng.chance(0.5) ? `ck_${checks.length}_${rng.int(0, 999)}` : undefined);

  for (const column of columns) {
    if (!column.bounds || column.addedLater) continue;
    const c = q(column.name);
    const b = column.bounds;

    if (column.type.kind === 'int64' && b.min !== undefined && b.max !== undefined)
      checks.push({ name: named(), sql: rng.chance(0.5) ? `${c} BETWEEN ${b.min} AND ${b.max}` : `${c} >= ${b.min} AND ${c} <= ${b.max}` });
    else if (column.type.kind === 'float64' && b.min !== undefined) checks.push({ name: named(), sql: `${c} > ${b.min}` });
    else if (column.type.kind === 'string' && b.maxLength !== undefined)
      checks.push({ name: named(), sql: `${c} IS NULL OR length(${c}) <= ${b.maxLength}` });
  }

  // A check across two columns.
  const ints = columns.filter((c) => c.type.kind === 'int64' && !c.identity && !c.addedLater && !primaryKey.includes(c.name));
  if (ints.length >= 2 && rng.chance(0.3)) {
    const [a, b] = rng.shuffle(ints) as [ColumnDef, ColumnDef];
    checks.push({ name: named(), sql: `${q(a.name)} IS NULL OR ${q(b.name)} IS NULL OR ${q(a.name)} <> ${q(b.name)}` });
  }

  // A check with a string literal in it, which the dump has to copy without breaking the quoting.
  const strings = columns.filter((c) => c.type.kind === 'string' && !c.addedLater && !primaryKey.includes(c.name));
  if (strings.length > 0 && rng.chance(0.2)) {
    const s = rng.pick(strings);
    checks.push({ name: named(), sql: `${q(s.name)} <> ${stringLiteral(rng.pick(["it's", 'a\\b', 'tab\there', 'x;y']))}` });
  }

  return checks;
}

function generateIndexes(rng: Random, table: string, columns: readonly ColumnDef[], primaryKey: readonly string[]): IndexDef[] {
  const candidates = columns.filter((c) => indexable(c.type) && !primaryKey.includes(c.name) && !c.addedLater);
  const indexes: IndexDef[] = [];

  for (let i = 0, n = rng.int(0, 3); i < n && candidates.length > 0; i++) {
    const keys = rng.shuffle(candidates).slice(0, rng.int(1, Math.min(3, candidates.length)));
    const rest = candidates.filter((c) => !keys.includes(c));

    indexes.push({
      name: `ix_${table.toLowerCase()}_${i}`,
      // A unique index on a low-cardinality column rejects most rows, so it stays on wide keys.
      unique: rng.chance(0.3) && keys.some((k) => ['int64', 'string', 'uuid', 'oid', 'float64'].includes(k.type.kind)),
      columns: keys.map((k) => ({ name: k.name, desc: descendable(k.type) && rng.chance(0.3) })),
      include: rng.chance(0.25) ? rng.subset(rest, 0.5).slice(0, 3).map((c) => c.name) : [],
      comment: rng.chance(0.2) ? randomComment(rng) : undefined,
      inline: rng.chance(0.4),
    });
  }

  return indexes;
}

function randomComment(rng: Random): string {
  return rng.pick([
    'plain comment',
    "it's quoted",
    'back\\slash',
    'ends with a backslash \\',
    'line one\nline two',
    'semi; colon -- dash',
    'ünïcödé 😀',
    'tab\tseparated',
    '',
  ]);
}

function capitalize(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1);
}

// ---------------------------------------------------------------------------------------------
// DDL text
// ---------------------------------------------------------------------------------------------

export function columnSql(column: ColumnDef, canonicalType = false): string {
  const type = canonicalType && column.typeSql !== 'serial' ? canonicalTypeSql(column.type) : column.typeSql;
  let sql = `${q(column.name)} ${type}`;

  if (column.identity === 'always') sql += ' GENERATED ALWAYS AS IDENTITY';
  else if (column.identity === 'by-default') sql += ' GENERATED BY DEFAULT AS IDENTITY';

  if (column.notNull && !column.identity) sql += ' NOT NULL';

  const def = column.default;
  if (def?.kind === 'const') sql += ` DEFAULT(${literal(column.type, def.value)})`;
  else if (def?.kind === 'fn') sql += ` DEFAULT(${def.fn}())`;
  else if (def?.kind === 'nextval') sql += ` DEFAULT(nextval(${stringLiteral(def.sequence)}))`;

  if (column.comment?.inline) sql += ` COMMENT ${stringLiteral(column.comment.text)}`;

  return sql;
}

export function indexKeySql(index: IndexDef): string {
  return index.columns.map((c) => q(c.name) + (c.desc ? ' DESC' : '')).join(', ');
}

/**
 * The CREATE TABLE statement. `simple` leaves out the inline indexes and checks and spells every type
 * the canonical way, for a retry after the server refused the full form.
 */
export function createTableSql(table: TableDef, simple = false): string {
  const parts = table.columns.filter((c) => !c.addedLater).map((c) => columnSql(c, simple));

  parts.push(`PRIMARY KEY (${table.primaryKey.map(q).join(', ')})`);

  if (!simple) {
    for (const index of table.indexes.filter((i) => i.inline)) {
      let sql = `${index.unique ? 'UNIQUE KEY' : 'KEY'} ${q(index.name)} (${indexKeySql(index)})`;
      if (index.include.length > 0) sql += ` INCLUDE (${index.include.map(q).join(', ')})`;
      parts.push(sql);
    }

    for (const check of table.checks)
      parts.push(`${check.name ? `CONSTRAINT ${q(check.name)} ` : ''}CHECK (${check.sql})`);
  }

  for (const fk of table.foreignKeys) {
    parts.push(
      `${fk.name ? `CONSTRAINT ${q(fk.name)} ` : ''}FOREIGN KEY (${fk.columns.map(q).join(', ')}) ` +
        `REFERENCES ${q(fk.parentTable)} (${fk.parentColumns.map(q).join(', ')})`,
    );
  }

  let sql = `CREATE TABLE ${q(table.name)} (\n  ${parts.join(',\n  ')}\n)`;
  if (table.comment?.inline) sql += ` COMMENT ${stringLiteral(table.comment.text)}`;
  return sql;
}

export function createIndexSql(table: TableDef, index: IndexDef): string {
  let sql = `CREATE ${index.unique ? 'UNIQUE ' : ''}INDEX ${q(index.name)} ON ${q(table.name)} (${indexKeySql(index)})`;
  if (index.include.length > 0) sql += ` INCLUDE (${index.include.map(q).join(', ')})`;
  return sql;
}
