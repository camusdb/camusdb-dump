import { camus } from 'camusdb';
import type { CamusClient, ParameterValue } from 'camusdb';

import type { ColumnInfo, Endpoints, Finding, Protocol, ShowRow } from './catalog.js';
import { cellText, clientFor, closeQuietly, errorCode, listDatabases, listTables, readRows, show, showColumns } from './catalog.js';
import { q } from './schema.js';

export interface TableSnapshot {
  readonly createTable: string[];
  readonly columns: ShowRow[];
  readonly indexes: ShowRow[];
  readonly rowCount: number;
  readonly columnNames: string[];
  /** Positions of the primary-key columns in each row, so rows of two snapshots can be paired. */
  readonly keyColumns: number[];
  /** Every row as a JSON array of cell texts, sorted, so two snapshots compare as multisets. */
  readonly rows: string[];
  readonly probe?: ProbeResult | undefined;
}

export interface DatabaseSnapshot {
  readonly tables: Record<string, TableSnapshot>;
  readonly sequences: ShowRow[];
}

export interface Snapshot {
  readonly label: string;
  readonly databases: Record<string, DatabaseSnapshot>;
  /** Driver problems seen while reading: these are not dump problems. */
  readonly findings: Finding[];
}

/**
 * The result of inserting two rows that give only their primary key. Everything else in them comes
 * from the table's defaults, identities and sequences, which SHOW CREATE TABLE does not always show.
 */
export interface ProbeResult {
  /** 'ok' or the error code, per probe row. */
  readonly outcomes: string[];
  readonly columns: string[];
  /** Per probe row that went in, one cell text per column. */
  readonly rows: string[][];
}

export interface SnapshotOptions {
  readonly endpoints: Endpoints;
  readonly protocol: Protocol;
  readonly prefix: string;
  readonly label: string;
  /** Also run the probe. It writes rows, so it runs after the snapshot has read everything. */
  readonly probe: boolean;
}

export async function snapshot(options: SnapshotOptions): Promise<Snapshot> {
  const findings: Finding[] = [];
  const catalog = clientFor(options.endpoints, 'test', options.protocol);
  let names: string[];
  try {
    names = (await listDatabases(catalog)).filter((name) => name.toLowerCase().startsWith(options.prefix.toLowerCase())).sort();
  } finally {
    await closeQuietly(catalog);
  }

  const databases: Record<string, DatabaseSnapshot> = {};
  const probes: { database: string; table: string; columns: ColumnInfo[]; key: ShowRow | undefined }[] = [];

  for (const database of names) {
    const client = clientFor(options.endpoints, database, options.protocol);
    try {
      const tables: Record<string, TableSnapshot> = {};

      for (const table of (await listTables(client)).sort()) {
        const createTable = (await show(client, `SHOW CREATE TABLE ${q(table)}`)).map((row) => row['Create Table'] ?? '');
        const { rows: columnRows, columns } = await showColumns(client, table);
        const indexes = (await show(client, `SHOW INDEXES FROM ${q(table)}`)).sort((a, b) =>
          (a['Key_name'] ?? '').localeCompare(b['Key_name'] ?? ''),
        );
        const rows = (await readRows(client, table, columns, findings, { database })).map((row) =>
          JSON.stringify(row.map(cellText)),
        );
        rows.sort();

        const key = indexes.find((i) => i['Key_name'] === '~pk');
        const keyColumns = (key?.['Columns'] ?? '')
          .split(',')
          .map((name) => columns.findIndex((c) => c.name.toLowerCase() === name.trim().toLowerCase()))
          .filter((i) => i >= 0);

        tables[table] = {
          createTable,
          columns: columnRows,
          indexes,
          rowCount: rows.length,
          columnNames: columns.map((c) => c.name),
          keyColumns,
          rows,
        };
        probes.push({ database, table, columns, key });
      }

      const sequences = (await show(client, 'SHOW SEQUENCES')).sort((a, b) => (a['sequence'] ?? '').localeCompare(b['sequence'] ?? ''));
      databases[database] = { tables, sequences };
    } finally {
      await closeQuietly(client);
    }
  }

  if (options.probe) {
    for (const p of probes) {
      const client = clientFor(options.endpoints, p.database, options.protocol);
      try {
        const result = await probe(client, p.table, p.columns, p.key, findings, p.database);
        const tables = databases[p.database]!.tables as Record<string, TableSnapshot>;
        tables[p.table] = { ...tables[p.table]!, probe: result };
      } finally {
        await closeQuietly(client);
      }
    }
  }

  return { label: options.label, databases, findings };
}

/** A key value no generated row holds: the generator never makes these. */
function probeKey(kind: string, n: number): ParameterValue | undefined {
  switch (kind) {
    case 'int64':
      return camus.int64(9_100_000_000_000_000_000n + BigInt(n));
    case 'string':
      return `~probe-${n}`;
    case 'uuid':
      return camus.uuid(`ffffffff-ffff-4fff-bfff-00000000000${n}`);
    case 'oid':
      return camus.id(`ffffffffffffffffffff000${n}`);
    default:
      return undefined;
  }
}

async function probe(
  client: CamusClient,
  table: string,
  columns: ColumnInfo[],
  pk: ShowRow | undefined,
  findings: Finding[],
  database: string,
): Promise<ProbeResult> {
  const keyColumns = (pk?.['Columns'] ?? '').split(',').map((c) => c.trim()).filter((c) => c.length > 0);
  const keyInfo = keyColumns.map((name) => columns.find((c) => c.name.toLowerCase() === name.toLowerCase()));
  const outcomes: string[] = [];
  const rows: string[][] = [];

  if (keyInfo.length === 0 || keyInfo.some((c) => !c?.type)) return { outcomes: ['no-key'], columns: [], rows };

  for (const n of [1, 2]) {
    const parameters: Record<string, ParameterValue> = {};
    const values = keyInfo.map((c, i) => {
      const value = probeKey(c!.type!.kind, n);
      parameters[`k${i}`] = value ?? null;
      return `@k${i}`;
    });

    try {
      await client.execute(
        `INSERT INTO ${q(table)} (${keyColumns.map(q).join(', ')}) VALUES (${values.join(', ')})`,
        parameters,
      );
      outcomes.push('ok');

      const where = keyColumns.map((c, i) => `${q(c)} = @k${i}`).join(' AND ');
      const read = await readRows(client, table, columns, findings, { database }, { sql: where, parameters });
      rows.push(read[0] ? read[0].map(cellText) : ['<not found>']);
    } catch (error) {
      outcomes.push(errorCode(error));
    }
  }

  return { outcomes, columns: columns.map((c) => c.name), rows };
}
