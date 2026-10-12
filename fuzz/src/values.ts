import { camus, ColumnType, ticksToDate } from 'camusdb';
import type { ParameterValue } from 'camusdb';

import type { Random } from './random.js';

/**
 * The column types the fuzzer declares. Each one maps to one CamusDB storage type; the aliases the
 * server also accepts are only a spelling, chosen in {@link typeSql}.
 */
export type ScalarKind =
  | 'int64'
  | 'float64'
  | 'float32'
  | 'numeric'
  | 'bool'
  | 'string'
  | 'bytes'
  | 'date'
  | 'datetime'
  | 'uuid'
  | 'oid';

/** Element types an array column may hold. NUMERIC has no array form on the server. */
export const ARRAY_ELEMENT_KINDS: readonly ScalarKind[] = ['int64', 'float64', 'bool', 'string', 'bytes', 'date', 'uuid'];

export type ColType =
  | { readonly kind: ScalarKind; readonly length?: number | undefined }
  | { readonly kind: 'array'; readonly element: ScalarKind };

/**
 * A value as the fuzzer means it, before any encoding. Each kind has exactly one form, so two values
 * of a kind compare without any conversion:
 *
 * - int64: bigint; float64 and float32: number (a float32 already rounded with Math.fround)
 * - numeric: decimal text; string, uuid, oid: string (uuid and oid in lowercase)
 * - bytes: Uint8Array; date: 'yyyy-MM-dd'; datetime: .NET ticks as bigint
 * - array: a list of element values or null
 */
export type Value = bigint | number | string | boolean | Uint8Array | null | readonly Value[];

const TICKS_PER_MS = 10_000n;
const TICKS_PER_DAY = 864_000_000_000n;
const MAX_TICKS = 3_155_378_975_999_999_999n; // 9999-12-31T23:59:59.9999999
const UNIX_EPOCH_TICKS = 621_355_968_000_000_000n;
const MAX_DAYS = Number(MAX_TICKS / TICKS_PER_DAY);

// ---------------------------------------------------------------------------------------------
// SQL type names
// ---------------------------------------------------------------------------------------------

const ALIASES: Partial<Record<ScalarKind, readonly string[]>> = {
  int64: ['INT64', 'int64', 'INT', 'int', 'integer'],
  float64: ['FLOAT64', 'float64'],
  float32: ['FLOAT32', 'real'],
  numeric: ['NUMERIC', 'decimal'],
  bool: ['BOOL', 'boolean'],
  bytes: ['BYTES', 'blob'],
  datetime: ['DATETIME', 'timestamp'],
  oid: ['OID', 'object_id'],
};

/** The type as written in a column definition. An alias is chosen at random to test the spellings. */
export function typeSql(type: ColType, rng: Random): string {
  if (type.kind === 'array') return `ARRAY(${type.element.toUpperCase()})`;
  if (type.kind === 'string') return type.length === undefined ? 'STRING' : `STRING(${type.length})`;

  const aliases = ALIASES[type.kind];
  return aliases ? rng.pick(aliases) : type.kind.toUpperCase();
}

/** The canonical spelling of a type, as SHOW CREATE TABLE writes it. */
export function canonicalTypeSql(type: ColType): string {
  if (type.kind === 'array') return `ARRAY(${type.element.toUpperCase()})`;
  if (type.kind === 'string' && type.length !== undefined) return `STRING(${type.length})`;
  return type.kind.toUpperCase();
}

/** Arrays cannot be indexed; every scalar can. */
export function indexable(type: ColType): boolean {
  return type.kind !== 'array';
}

/** The server refuses a descending key on the types that share the string key encoding. */
export function descendable(type: ColType): boolean {
  return type.kind !== 'array' && type.kind !== 'string' && type.kind !== 'oid' && type.kind !== 'bytes';
}

// ---------------------------------------------------------------------------------------------
// Generation
// ---------------------------------------------------------------------------------------------

/** Optional bounds a CHECK constraint puts on a column, so most generated values pass it. */
export interface ValueBounds {
  readonly min?: bigint | number | undefined;
  readonly max?: bigint | number | undefined;
  readonly maxLength?: number | undefined;
}

const STRING_PIECES: readonly string[] = [
  '',
  "'",
  "''",
  '"',
  '\\',
  "\\'",
  'a\\',
  '\n',
  '\r\n',
  '\t',
  '\0',
  '\u0001',
  '\u001f',
  '\u007f',
  '\u0085',
  ';',
  '--',
  '/*',
  '*/',
  '`',
  "E'",
  "X'00'",
  '$$',
  'é',
  'ñandú',
  '日本語',
  '😀',
  '👩‍💻',
  '​',
  '﻿',
  ' ',
  'NULL',
  '\\x41',
  '\\n',
  '%',
  '_',
  '?',
  '@p0',
  ':name',
  'DROP TABLE x;',
];

const ASCII = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 ';

export function generateValue(type: ColType, rng: Random, bounds: ValueBounds = {}): Value {
  if (type.kind === 'array') {
    const length = rng.pick([0, 0, 1, 2, 3, 5, 8]);
    const items: Value[] = [];
    for (let i = 0; i < length; i++)
      items.push(rng.chance(0.15) ? null : generateValue({ kind: type.element, length: 40 }, rng));
    return items;
  }

  switch (type.kind) {
    case 'int64':
      return generateInt64(rng, bounds);
    case 'float64':
      return generateFloat64(rng, bounds);
    case 'float32':
      return Math.fround(generateFloat64(rng, bounds, true));
    case 'numeric':
      return generateNumeric(rng, bounds);
    case 'bool':
      return rng.chance(0.5);
    case 'string':
      return generateString(rng, Math.min(type.length ?? 4000, bounds.maxLength ?? Number.MAX_SAFE_INTEGER));
    case 'bytes':
      return rng.bytes(rng.chance(0.03) ? rng.int(2049, 4096) : rng.pick([0, 1, 2, 4, 16, rng.int(0, 64)]));
    case 'date':
      return generateDate(rng);
    case 'datetime':
      return generateTicks(rng);
    case 'uuid':
      return formatUuid(rng.bytes(16));
    case 'oid':
      return hex(rng.bytes(12));
  }
}

function generateInt64(rng: Random, bounds: ValueBounds): bigint {
  const min = typeof bounds.min === 'bigint' ? bounds.min : undefined;
  const max = typeof bounds.max === 'bigint' ? bounds.max : undefined;

  if (min !== undefined && max !== undefined && rng.chance(0.9)) return rng.bigintIn(min, max);
  if (min !== undefined && rng.chance(0.9)) return min + rng.bigintIn(0n, 1_000_000n);

  return rng.pick<() => bigint>([
    () => 0n,
    () => 1n,
    () => -1n,
    () => 2n ** 53n,
    () => 2n ** 53n + 1n,
    () => -(2n ** 63n),
    () => 2n ** 63n - 1n,
    () => -(2n ** 63n) + 1n,
    () => rng.int64(),
    () => rng.int64(),
    () => BigInt(rng.int(-1000, 1000)),
    () => BigInt(rng.int(-1000, 1000)),
  ])();
}

function generateFloat64(rng: Random, bounds: ValueBounds, single = false): number {
  const min = typeof bounds.min === 'number' ? bounds.min : undefined;

  if (min !== undefined && rng.chance(0.9)) return min + rng.float() * 1e6 + 1;

  const edges = single
    ? [0, -0, 1, -1, 1.1, 0.1, 3.4028234663852886e38, -3.4028234663852886e38, 1.401298464324817e-45, 1.1754943508222875e-38, 16777217]
    : [
        0,
        -0,
        1,
        -1,
        0.1,
        1 / 3,
        5e-324,
        2.2250738585072014e-308,
        1.7976931348623157e308,
        -1.7976931348623157e308,
        1e21,
        1e-7,
        123456789.123,
        9007199254740993,
        0.30000000000000004,
      ];

  if (rng.chance(0.3)) return rng.pick(edges);

  const magnitude = single ? rng.int(-38, 38) : rng.int(-300, 300);
  const value = (rng.float() - 0.5) * 2 * 10 ** magnitude;
  return rng.chance(0.2) ? Math.round(value) : value;
}

function generateNumeric(rng: Random, bounds: ValueBounds): string {
  if (rng.chance(0.2)) {
    return rng.pick([
      '0',
      '1',
      '-1',
      '0.000000001',
      '-0.000000001',
      '99999999999999999999999999999.999999999',
      '-99999999999999999999999999999.999999999',
      '12345678901234567890.123456789',
      '1.5',
      '1200',
    ]);
  }

  const intDigits = rng.int(1, typeof bounds.max === 'bigint' ? 6 : 29);
  const fracDigits = rng.int(0, 9);
  let intPart = '';
  for (let i = 0; i < intDigits; i++) intPart += String(rng.int(0, 9));
  let frac = '';
  for (let i = 0; i < fracDigits; i++) frac += String(rng.int(0, 9));

  const sign = rng.chance(0.4) && bounds.min === undefined ? '-' : '';
  return canonicalNumeric(sign + intPart + (frac ? '.' + frac : ''));
}

function generateString(rng: Random, maxLength: number): string {
  let text = '';
  const roll = rng.float();

  if (roll < 0.03 && maxLength > 2100) {
    // Long enough to cross the server's large-value threshold (2048 bytes) and be stored out of line.
    const length = rng.int(2100, Math.min(maxLength, 6000));
    while (text.length < length) text += rng.chance(0.05) ? rng.pick(STRING_PIECES) : ASCII[rng.int(0, ASCII.length - 1)];
  } else if (roll < 0.1) {
    text = '';
  } else {
    const pieces = rng.int(1, 6);
    for (let i = 0; i < pieces; i++) {
      if (rng.chance(0.5)) text += rng.pick(STRING_PIECES);
      else for (let j = rng.int(1, 8); j > 0; j--) text += ASCII[rng.int(0, ASCII.length - 1)];
    }
  }

  return truncateUtf16(text, maxLength);
}

/** Cuts a string to a UTF-16 length without leaving half of a surrogate pair at the end. */
function truncateUtf16(text: string, maxLength: number): string {
  if (text.length <= maxLength) return text;

  let cut = text.slice(0, maxLength);
  const last = cut.charCodeAt(cut.length - 1);
  if (last >= 0xd800 && last <= 0xdbff) cut = cut.slice(0, -1);
  return cut;
}

function generateDate(rng: Random): string {
  if (rng.chance(0.2))
    return rng.pick(['0001-01-01', '9999-12-31', '1970-01-01', '1900-02-28', '2000-02-29', '1969-12-31']);

  return ticksToIsoDate(BigInt(rng.int(0, MAX_DAYS)) * TICKS_PER_DAY);
}

function generateTicks(rng: Random): bigint {
  if (rng.chance(0.2)) {
    return rng.pick([0n, MAX_TICKS, UNIX_EPOCH_TICKS, UNIX_EPOCH_TICKS - 1n, UNIX_EPOCH_TICKS + 1n, 638_000_000_000_000_001n]);
  }

  const ticks = rng.bigintIn(0n, MAX_TICKS);
  // Half the values are whole milliseconds, which a JavaScript Date carries; the other half are not.
  return rng.chance(0.5) ? (ticks / TICKS_PER_MS) * TICKS_PER_MS : ticks;
}

// ---------------------------------------------------------------------------------------------
// SQL literals
// ---------------------------------------------------------------------------------------------

/**
 * Writes a value as a SQL literal. This is the fuzzer's own writer and does not share code with the
 * dump tool, so a bug in one does not hide the same bug in the other.
 */
export function literal(type: ColType, value: Value): string {
  if (value === null) return 'NULL';

  if (type.kind === 'array') {
    const items = value as readonly Value[];
    return `ARRAY[${items.map((item) => literal({ kind: type.element }, item)).join(', ')}]`;
  }

  switch (type.kind) {
    case 'int64':
      return String(value);
    case 'float64':
    case 'float32':
      return floatLiteral(value as number);
    case 'numeric':
      return `NUMERIC '${value as string}'`;
    case 'bool':
      return value ? 'true' : 'false';
    case 'string':
      return stringLiteral(value as string);
    case 'bytes':
      return `X'${hex(value as Uint8Array)}'`;
    case 'date':
      return `'${value as string}'`;
    case 'datetime':
      return `'${ticksToIso7(value as bigint)}'`;
    case 'uuid':
    case 'oid':
      return `'${value as string}'`;
  }
}

function floatLiteral(value: number): string {
  if (Object.is(value, -0)) return '-0.0';
  if (Number.isInteger(value) && Math.abs(value) < 1e21) return `${value}.0`;
  return String(value).replace('e+', 'e');
}

/** The plain form when it can carry the value, and the escape form otherwise. */
export function stringLiteral(value: string): string {
  if (!/[\\\u0000-\u001f\u007f-\u009f]/.test(value)) return `'${value.replaceAll("'", "''")}'`;

  let body = '';
  for (const c of value) {
    const code = c.codePointAt(0)!;
    if (c === '\\') body += '\\\\';
    else if (c === "'") body += "''";
    else if (code < 0x20 || (code >= 0x7f && code <= 0x9f)) body += `\\u${code.toString(16).padStart(4, '0')}`;
    else body += c;
  }
  return `E'${body}'`;
}

// ---------------------------------------------------------------------------------------------
// Driver parameters
// ---------------------------------------------------------------------------------------------

/**
 * Turns a value into what the camusdb driver binds. The choice between an inferred JavaScript value
 * and an explicit `camus.*` helper is random, so both encoder paths run.
 */
export function parameter(type: ColType, value: Value, rng: Random): ParameterValue {
  if (value === null) return rng.chance(0.5) ? null : camus.null();

  if (type.kind === 'array') {
    const items = (value as readonly Value[]).map((item) => (item === null ? null : arrayItem(type.element, item)));
    return camus.array(items, columnType(type.element));
  }

  switch (type.kind) {
    case 'int64':
      return rng.chance(0.5) ? (value as bigint) : camus.int64(value as bigint);
    case 'float64':
      return camus.float64(value as number);
    case 'float32':
      return camus.float32(value as number);
    case 'numeric':
      return camus.numeric(value as string);
    case 'bool':
      return rng.chance(0.5) ? (value as boolean) : camus.bool(value as boolean);
    case 'string':
      return rng.chance(0.5) ? (value as string) : camus.string(value as string);
    case 'bytes':
      return rng.chance(0.5) ? (value as Uint8Array) : camus.bytes(value as Uint8Array);
    case 'date':
      return camus.date(new Date(`${value as string}T00:00:00.000Z`));
    case 'datetime': {
      const ticks = value as bigint;
      // A Date carries whole milliseconds only. Finer values go through the raw-cell escape hatch.
      if (ticks % TICKS_PER_MS === 0n && rng.chance(0.7)) return camus.dateTime(ticksToDate(ticks));
      return camus.raw({ type: ColumnType.DateTime, longValue: ticks });
    }
    case 'uuid':
      return camus.uuid(value as string);
    case 'oid':
      return camus.id(value as string);
  }
}

function arrayItem(kind: ScalarKind, value: Value): unknown {
  switch (kind) {
    case 'date':
      return new Date(`${value as string}T00:00:00.000Z`);
    default:
      return value;
  }
}

export function columnType(kind: ScalarKind): ColumnType {
  switch (kind) {
    case 'int64':
      return ColumnType.Integer64;
    case 'float64':
      return ColumnType.Float64;
    case 'float32':
      return ColumnType.Float32;
    case 'numeric':
      return ColumnType.Numeric;
    case 'bool':
      return ColumnType.Bool;
    case 'string':
      return ColumnType.String;
    case 'bytes':
      return ColumnType.Bytes;
    case 'date':
      return ColumnType.Date;
    case 'datetime':
      return ColumnType.DateTime;
    case 'uuid':
      return ColumnType.Uuid;
    case 'oid':
      return ColumnType.Id;
  }
}

// ---------------------------------------------------------------------------------------------
// Text forms and comparison
// ---------------------------------------------------------------------------------------------

/** NUMERIC as the server writes it: no exponent, no trailing zeros, no point for a whole number. */
export function canonicalNumeric(text: string): string {
  let sign = '';
  let body = text.trim();
  if (body.startsWith('-')) {
    sign = '-';
    body = body.slice(1);
  } else if (body.startsWith('+')) {
    body = body.slice(1);
  }

  let [intPart = '0', frac = ''] = body.split('.');
  intPart = intPart.replace(/^0+/, '') || '0';
  frac = frac.replace(/0+$/, '');

  const result = frac ? `${intPart}.${frac}` : intPart;
  return result === '0' ? '0' : sign + result;
}

export function hex(bytes: Uint8Array): string {
  return Buffer.from(bytes).toString('hex');
}

function formatUuid(bytes: Uint8Array): string {
  const h = hex(bytes);
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}

export function ticksToIsoDate(ticks: bigint): string {
  return ticksToIso7(ticks).slice(0, 10);
}

/** The server's text form of a DATETIME: ISO-8601 in UTC with seven fractional digits. */
export function ticksToIso7(ticks: bigint): string {
  const ms = (ticks - UNIX_EPOCH_TICKS) / TICKS_PER_MS - (ticks < UNIX_EPOCH_TICKS && ticks % TICKS_PER_MS !== 0n ? 1n : 0n);
  const date = new Date(Number(ms));
  const sub = ((ticks % TICKS_PER_MS) + TICKS_PER_MS) % TICKS_PER_MS;
  const iso = isoYear(date);
  return `${iso.slice(0, 19)}.${String(date.getUTCMilliseconds()).padStart(3, '0')}${String(sub).padStart(4, '0')}Z`;
}

/** toISOString, but always with a four-digit year, which is all the server's range needs. */
function isoYear(date: Date): string {
  const iso = date.toISOString();
  return iso.startsWith('+') || iso.startsWith('-') ? iso.slice(3) : iso;
}

/** A stable text for any value the driver returns, used to compare rows from two servers. */
export function canonical(value: unknown): string {
  if (value === null || value === undefined) return 'null';
  if (typeof value === 'bigint') return `i:${value}`;
  if (typeof value === 'number') return `n:${Object.is(value, -0) ? '-0' : String(value)}`;
  if (typeof value === 'boolean') return `b:${value}`;
  if (typeof value === 'string') return `s:${JSON.stringify(value)}`;
  if (value instanceof Uint8Array) return `x:${hex(value)}`;
  if (value instanceof Date) return `d:${Number.isNaN(value.getTime()) ? 'invalid' : isoYear(value)}`;
  if (Array.isArray(value)) return `[${value.map(canonical).join(',')}]`;
  return `o:${JSON.stringify(value, (_, v: unknown) => (typeof v === 'bigint' ? v.toString() : v))}`;
}

/** Parses a SHOW COLUMNS type such as `STRING(40)` or `ARRAY(INT64)`. */
export function parseTypeName(text: string): ColType | undefined {
  const upper = text.trim().toUpperCase();
  const array = /^ARRAY\((\w+)\)$/.exec(upper);
  if (array) {
    const element = kindFromName(array[1]!);
    return element ? { kind: 'array', element } : undefined;
  }

  const sized = /^(\w+)(?:\((\d+)\))?$/.exec(upper);
  if (!sized) return undefined;
  const kind = kindFromName(sized[1]!);
  return kind ? { kind, length: sized[2] ? Number(sized[2]) : undefined } : undefined;
}

function kindFromName(name: string): ScalarKind | undefined {
  const map: Record<string, ScalarKind> = {
    INT64: 'int64',
    FLOAT64: 'float64',
    FLOAT32: 'float32',
    NUMERIC: 'numeric',
    BOOL: 'bool',
    STRING: 'string',
    BYTES: 'bytes',
    DATE: 'date',
    DATETIME: 'datetime',
    UUID: 'uuid',
    OID: 'oid',
  };
  return map[name];
}

/**
 * Checks a value the driver decoded against the server's own text form of the same cell
 * (`CAST(col AS string)`). Returns a description of the disagreement, or undefined when they agree.
 */
export function checkAgainstText(kind: ScalarKind, decoded: unknown, text: unknown): string | undefined {
  if (decoded === null || decoded === undefined) {
    return text === null || text === undefined ? undefined : `driver gave null, server text is ${JSON.stringify(text)}`;
  }
  if (typeof text !== 'string') return `driver gave ${canonical(decoded)}, server text is ${JSON.stringify(text)}`;

  const fail = () => `driver gave ${canonical(decoded)}, server text is ${JSON.stringify(text)}`;

  switch (kind) {
    case 'int64':
      return typeof decoded === 'bigint' && decoded === BigInt(text) ? undefined : fail();
    case 'float64':
      return typeof decoded === 'number' && Object.is(decoded, Number(text)) ? undefined : fail();
    case 'float32':
      return typeof decoded === 'number' && Object.is(decoded, Math.fround(Number(text))) ? undefined : fail();
    case 'numeric':
      return decoded === text ? undefined : fail();
    case 'bool':
      return String(decoded) === text.toLowerCase() ? undefined : fail();
    case 'string':
    case 'oid':
      return decoded === text ? undefined : fail();
    case 'uuid':
      return decoded === text.toLowerCase() ? undefined : fail();
    case 'bytes':
      return decoded instanceof Uint8Array && `\\x${hex(decoded)}` === text ? undefined : fail();
    case 'date':
      return decoded instanceof Date && isoYear(decoded).slice(0, 10) === text ? undefined : fail();
    case 'datetime':
      // The driver truncates to whole milliseconds, which is documented; the text keeps all seven digits.
      return decoded instanceof Date && isoYear(decoded).slice(0, 23) === text.slice(0, 23) ? undefined : fail();
  }
}

/**
 * Checks a value read back through the driver against the value the fuzzer wrote. `text` is the
 * server's text form, which is the only exact form of a DATETIME on this side.
 */
export function matchesWritten(type: ColType, written: Value, decoded: unknown, text: unknown): boolean {
  if (written === null) return decoded === null || decoded === undefined;

  if (type.kind === 'array') {
    if (!Array.isArray(decoded)) return false;
    const items = written as readonly Value[];
    return (
      items.length === decoded.length &&
      items.every((item, i) => matchesWritten({ kind: type.element }, item, decoded[i], undefined))
    );
  }

  switch (type.kind) {
    case 'int64':
      return decoded === written;
    case 'float64':
      return Object.is(decoded, written);
    case 'float32':
      return Object.is(decoded, Math.fround(written as number));
    case 'numeric':
      return decoded === canonicalNumeric(written as string);
    case 'bool':
    case 'string':
    case 'oid':
      return decoded === written;
    case 'uuid':
      return typeof decoded === 'string' && decoded.toLowerCase() === written;
    case 'bytes':
      return decoded instanceof Uint8Array && hex(decoded) === hex(written as Uint8Array);
    case 'date':
      return decoded instanceof Date && isoYear(decoded).slice(0, 10) === written;
    case 'datetime':
      return text === undefined
        ? decoded instanceof Date && isoYear(decoded).slice(0, 23) === ticksToIso7(written as bigint).slice(0, 23)
        : text === ticksToIso7(written as bigint);
  }
}

/** A short description of a written value for a report. */
export function describe(type: ColType, value: Value): string {
  return literal(type, value).slice(0, 200);
}
