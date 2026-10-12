import { CamusClient, CamusError } from 'camusdb';
import type { Parameters } from 'camusdb';

import { q } from './schema.js';
import type { ColType } from './values.js';
import { canonical, checkAgainstText, parseTypeName } from './values.js';

export interface Endpoints {
  readonly rest: string;
  readonly grpc: string;
}

export type Protocol = 'rest' | 'grpc';

/** Something a check found. `kind` groups findings in the report. */
export interface Finding {
  readonly kind: string;
  readonly database?: string | undefined;
  readonly table?: string | undefined;
  readonly detail: string;
  /** How many findings share this one's kind, place and column; set by {@link groupFindings}. */
  readonly count?: number | undefined;
}

/**
 * Folds findings that share a kind, a table and the head of their detail (the column, as a rule)
 * into one, with a count. One defect on a 300-row table is one line, not 300.
 */
export function groupFindings(findings: readonly Finding[]): Finding[] {
  const groups = new Map<string, Finding>();
  for (const f of findings) {
    const head = f.detail.split(/[:\n]/, 1)[0] ?? '';
    const key = [f.kind, f.database, f.table, head].join('\u0001');
    const seen = groups.get(key);
    groups.set(key, seen ? { ...seen, count: (seen.count ?? 1) + (f.count ?? 1) } : f);
  }
  return [...groups.values()];
}

export function clientFor(endpoints: Endpoints, database: string, protocol: Protocol): CamusClient {
  return new CamusClient({
    endpoint: protocol === 'rest' ? endpoints.rest : endpoints.grpc,
    database,
    protocol,
    int64: 'bigint',
    timeoutSeconds: 120,
  });
}

/** One row of a SHOW statement, with every value as text so it serializes and compares plainly. */
export type ShowRow = Record<string, string | null>;

export async function show(client: CamusClient, sql: string): Promise<ShowRow[]> {
  const result = await client.query(sql);
  return result.rows.map((row) => {
    const out: ShowRow = {};
    for (const [key, value] of Object.entries(row)) out[key] = value === null || value === undefined ? null : String(value);
    return out;
  });
}

export async function listDatabases(client: CamusClient): Promise<string[]> {
  return (await show(client, 'SHOW DATABASES')).map((row) => row['Database'] ?? '').filter((name) => name.length > 0);
}

export async function listTables(client: CamusClient): Promise<string[]> {
  return (await show(client, 'SHOW TABLES')).map((row) => row['tables'] ?? '').filter((name) => name.length > 0);
}

export interface ColumnInfo {
  readonly name: string;
  readonly typeText: string;
  readonly type: ColType | undefined;
}

export async function showColumns(client: CamusClient, table: string): Promise<{ rows: ShowRow[]; columns: ColumnInfo[] }> {
  const rows = await show(client, `SHOW COLUMNS FROM ${q(table)}`);
  const columns = rows.map((row) => {
    const typeText = row['Type'] ?? '';
    return { name: row['Field'] ?? '', typeText, type: parseTypeName(typeText) };
  });
  return { rows, columns };
}

/** One cell as the driver decoded it, next to the server's text form of it (absent for an array). */
export interface Cell {
  readonly decoded: unknown;
  readonly text: unknown;
}

/**
 * Reads rows with every column twice: once as the driver decodes it, and once as the server's own
 * text form, `CAST(col AS string)`. The text form is exact where the driver's JavaScript value is not
 * (a DATETIME below a millisecond), and it is an independent check of the driver's decoder: a
 * disagreement between the two is reported as a finding.
 */
export async function readRows(
  client: CamusClient,
  table: string,
  columns: readonly ColumnInfo[],
  findings: Finding[],
  context: { database: string },
  where?: { sql: string; parameters: Parameters },
): Promise<Cell[][]> {
  const projection: string[] = [];
  columns.forEach((column, i) => {
    projection.push(`${q(column.name)} AS ${q(`__v${i}`)}`);
    if (column.type && column.type.kind !== 'array') projection.push(`CAST(${q(column.name)} AS string) AS ${q(`__t${i}`)}`);
  });

  const sql = `SELECT ${projection.join(', ')} FROM ${q(table)}${where ? ` WHERE ${where.sql}` : ''}`;
  const result = await client.query<Record<string, unknown>>(sql, where?.parameters);

  return result.rows.map((row) =>
    columns.map((column, i) => {
      const decoded = row[`__v${i}`];
      const text = column.type && column.type.kind !== 'array' ? row[`__t${i}`] : undefined;

      if (column.type && column.type.kind !== 'array') {
        const problem = checkAgainstText(column.type.kind, decoded, text);
        if (problem) {
          findings.push({
            kind: 'driver-decode',
            database: context.database,
            table,
            detail: `column ${column.name} (${column.typeText}): ${problem}`,
          });
        }
      }

      return { decoded, text };
    }),
  );
}

/** A cell as one stable string: the decoded value and the server text, so both must survive. */
export function cellText(cell: Cell): string {
  return cell.text === undefined ? canonical(cell.decoded) : `${canonical(cell.decoded)} | ${canonical(cell.text)}`;
}

export function errorCode(error: unknown): string {
  return CamusError.is(error) ? error.code : `client:${error instanceof Error ? error.name : typeof error}`;
}

export function errorText(error: unknown): string {
  if (CamusError.is(error)) return `${error.code}: ${error.message}`;
  if (error instanceof Error) return `${error.name}: ${error.message}`;
  return String(error);
}

export async function closeQuietly(client: CamusClient): Promise<void> {
  try {
    await client.close();
  } catch {
    // A close that fails leaves nothing to clean up that matters to a test run.
  }
}
