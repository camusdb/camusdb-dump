import type { ShowRow } from './catalog.js';
import type { ProbeResult, Snapshot, TableSnapshot } from './snapshot.js';

export interface Difference {
  readonly where: string;
  readonly what: string;
}

const SAMPLE = 3;

/**
 * Compares the snapshot of the source with the snapshot of a restore. Every object and every row of
 * the source must be in the restore, unchanged, and the restore must hold nothing more.
 */
export function compare(expected: Snapshot, actual: Snapshot): Difference[] {
  const out: Difference[] = [];

  const { missing, extra, both } = keys(expected.databases, actual.databases);
  for (const name of missing) out.push({ where: name, what: 'the database is missing from the restore' });
  for (const name of extra) out.push({ where: name, what: 'the restore has a database the source did not have' });

  for (const name of both) {
    const e = expected.databases[name]!;
    const a = actual.databases[name]!;

    compareShowRows(out, `${name} sequences`, e.sequences, a.sequences, 'sequence');

    const tables = keys(e.tables, a.tables);
    for (const t of tables.missing) out.push({ where: `${name}.${t}`, what: 'the table is missing from the restore' });
    for (const t of tables.extra) out.push({ where: `${name}.${t}`, what: 'the restore has a table the source did not have' });
    for (const t of tables.both) compareTable(out, `${name}.${t}`, e.tables[t]!, a.tables[t]!);
  }

  return out;
}

function compareTable(out: Difference[], where: string, e: TableSnapshot, a: TableSnapshot): void {
  if (JSON.stringify(e.createTable) !== JSON.stringify(a.createTable)) {
    const source = e.createTable.join('\n');
    const restore = a.createTable.join('\n');
    let at = 0;
    while (at < source.length && source[at] === restore[at]) at++;
    const around = (text: string) => `${at > 80 ? '…' : ''}${text.slice(Math.max(0, at - 80), at + 120)}${at + 120 < text.length ? '…' : ''}`;
    out.push({ where, what: `SHOW CREATE TABLE differs at character ${at}\n  source:  ${around(source)}\n  restore: ${around(restore)}` });
  }

  compareShowRows(out, `${where} columns`, e.columns, a.columns, 'Field');
  // A `~fk_` index is the server's own: it backs a foreign key only when no user index covered the
  // key's columns at the moment the key was created. A dump creates the objects in another order
  // than the source did, so the server can make one where the source has none, or the reverse.
  const userIndexes = (rows: ShowRow[]) => rows.filter((row) => !(row['Key_name'] ?? '').startsWith('~fk_'));
  compareShowRows(out, `${where} indexes`, userIndexes(e.indexes), userIndexes(a.indexes), 'Key_name');

  if (e.rowCount !== a.rowCount) out.push({ where, what: `row count: source ${e.rowCount}, restore ${a.rowCount}` });

  compareRows(out, where, e, a);

  if (e.probe && a.probe) compareProbe(out, where, e.probe, a.probe);
  else if (e.probe || a.probe) out.push({ where, what: 'only one side has probe results' });
}

/**
 * Pairs the rows that differ by primary key, and names the columns that changed. A change that
 * repeats across rows, such as a type that loses precision, is one line with a count.
 */
function compareRows(out: Difference[], where: string, e: TableSnapshot, a: TableSnapshot): void {
  const lost = multisetMinus(e.rows, a.rows).map((r) => JSON.parse(r) as string[]);
  const gained = multisetMinus(a.rows, e.rows).map((r) => JSON.parse(r) as string[]);
  if (lost.length === 0 && gained.length === 0) return;

  const keyOf = (row: string[]) => JSON.stringify(e.keyColumns.map((i) => row[i]));
  const restored = new Map<string, string[]>();
  if (e.keyColumns.length > 0) for (const row of gained) restored.set(keyOf(row), row);

  const changes = new Map<string, { count: number; example: string }>();
  const missing: string[][] = [];

  for (const row of lost) {
    const other = e.keyColumns.length > 0 ? restored.get(keyOf(row)) : undefined;
    if (!other) {
      missing.push(row);
      continue;
    }
    restored.delete(keyOf(row));

    row.forEach((cell, i) => {
      if (cell === other[i]) return;
      const column = e.columnNames[i] ?? `#${i}`;
      const seen = changes.get(column);
      if (seen) seen.count++;
      else changes.set(column, { count: 1, example: `key ${clip(keyOf(row))}: source ${clip(cell)}, restore ${clip(other[i] ?? '')}` });
    });
  }

  for (const [column, change] of changes)
    out.push({ where, what: `column ${column} changed in ${change.count} rows, for example ${change.example}` });
  if (missing.length > 0)
    out.push({ where, what: `${missing.length} source rows are not in the restore, for example:\n  ${missing.slice(0, SAMPLE).map((r) => clip(JSON.stringify(r))).join('\n  ')}` });
  if (restored.size > 0 || (e.keyColumns.length === 0 && gained.length > 0)) {
    const extra = e.keyColumns.length > 0 ? [...restored.values()] : gained;
    out.push({ where, what: `${extra.length} restored rows are not in the source, for example:\n  ${extra.slice(0, SAMPLE).map((r) => clip(JSON.stringify(r))).join('\n  ')}` });
  }
}

/**
 * Compares the two probe rows of each side. A column whose value differs between the two rows of a
 * side is volatile there (a sequence, gen_id(), now()), and only its NULL-ness is compared.
 */
function compareProbe(out: Difference[], where: string, e: ProbeResult, a: ProbeResult): void {
  if (JSON.stringify(e.outcomes) !== JSON.stringify(a.outcomes)) {
    out.push({ where, what: `probe insert outcomes differ: source ${e.outcomes.join(', ')}, restore ${a.outcomes.join(', ')}` });
    return;
  }

  const volatile = (p: ProbeResult, i: number) => p.rows.length < 2 || p.rows[0]![i] !== p.rows[1]![i];

  for (let r = 0; r < Math.min(e.rows.length, a.rows.length); r++) {
    e.columns.forEach((column, i) => {
      const ev = e.rows[r]![i];
      const av = a.rows[r]![i];
      if (ev === av) return;

      if (volatile(e, i) || volatile(a, i)) {
        if (isNull(ev) !== isNull(av))
          out.push({ where, what: `probe row ${r + 1}, column ${column}: the source filled in ${clip(ev ?? '')}, the restore ${clip(av ?? '')}` });
        return;
      }

      out.push({ where, what: `probe row ${r + 1}, column ${column}: the source filled in ${clip(ev ?? '')}, the restore ${clip(av ?? '')}` });
    });
  }
}

function isNull(cell: string | undefined): boolean {
  return cell === undefined || cell === 'null' || cell.startsWith('null |');
}

function compareShowRows(out: Difference[], where: string, e: ShowRow[], a: ShowRow[], keyField: string): void {
  const index = (rows: ShowRow[]) => new Map(rows.map((row) => [row[keyField] ?? JSON.stringify(row), row]));
  const ei = index(e);
  const ai = index(a);

  for (const [key, row] of ei) {
    const other = ai.get(key);
    if (!other) {
      out.push({ where, what: `${key} is missing from the restore: ${JSON.stringify(row)}` });
      continue;
    }

    const changed = Object.keys({ ...row, ...other }).filter((field) => row[field] !== other[field]);
    if (changed.length > 0)
      out.push({ where, what: `${key} differs in ${changed.map((f) => `${f} (source ${JSON.stringify(row[f])}, restore ${JSON.stringify(other[f])})`).join(', ')}` });
  }

  for (const [key, row] of ai) if (!ei.has(key)) out.push({ where, what: `${key} exists only in the restore: ${JSON.stringify(row)}` });
}

function keys<T>(e: Record<string, T>, a: Record<string, T>) {
  const ek = Object.keys(e);
  const ak = new Set(Object.keys(a));
  return {
    missing: ek.filter((k) => !ak.has(k)),
    extra: [...ak].filter((k) => !(k in e)),
    both: ek.filter((k) => ak.has(k)),
  };
}

function multisetMinus(left: readonly string[], right: readonly string[]): string[] {
  const counts = new Map<string, number>();
  for (const item of right) counts.set(item, (counts.get(item) ?? 0) + 1);

  const out: string[] = [];
  for (const item of left) {
    const n = counts.get(item) ?? 0;
    if (n > 0) counts.set(item, n - 1);
    else out.push(item);
  }
  return out;
}

function clip(text: string): string {
  return text.length > 600 ? `${text.slice(0, 600)}… (${text.length} chars)` : text;
}

export function formatDifferences(title: string, differences: readonly Difference[]): string {
  if (differences.length === 0) return `${title}: no differences. Every database object and every row survived.\n`;

  const lines = [`${title}: ${differences.length} differences.`, ''];
  for (const d of differences) lines.push(`- ${d.where}: ${d.what.replaceAll('\n', '\n  ')}`);
  return lines.join('\n') + '\n';
}
