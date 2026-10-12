import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { parseArgs } from 'node:util';

import { CamusClient } from 'camusdb';

import type { Endpoints, Finding, Protocol } from './catalog.js';
import { closeQuietly, errorText, groupFindings } from './catalog.js';
import { compare, formatDifferences } from './compare.js';
import { generate } from './generate.js';
import { Random, parseSeed } from './random.js';
import type { Snapshot } from './snapshot.js';
import { snapshot } from './snapshot.js';

const USAGE = `camusdb-dump-fuzz <command> [options]

Commands:
  wait        Wait until the server answers.
  generate    Build random databases and fill them through the camusdb driver.
  dump-plan   Print the camus-dump options this seed tests, as shell assignments.
  snapshot    Record every database object and every row of the fuzz databases.
  compare     Compare two snapshots. Exits 1 when they differ.
  findings    Print the findings in report files. Exits 1 when there is one.

Common options:
  --rest <url>      REST endpoint (default http://localhost:5095)
  --grpc <url>      gRPC endpoint (default http://localhost:5096)
  --seed <n>        Seed for every random choice (default: random)
`;

const { positionals, values } = parseArgs({
  allowPositionals: true,
  options: {
    rest: { type: 'string', default: 'http://localhost:5095' },
    grpc: { type: 'string', default: 'http://localhost:5096' },
    seed: { type: 'string' },
    databases: { type: 'string', default: '3' },
    out: { type: 'string' },
    protocol: { type: 'string', default: 'grpc' },
    prefix: { type: 'string', default: 'fuzz' },
    label: { type: 'string', default: 'snapshot' },
    probe: { type: 'boolean', default: false },
    expected: { type: 'string' },
    actual: { type: 'string' },
    title: { type: 'string', default: 'Dump verification' },
    report: { type: 'string' },
    file: { type: 'string', multiple: true, default: [] },
    allow: { type: 'string', multiple: true, default: [] },
    timeout: { type: 'string', default: '180' },
    help: { type: 'boolean', default: false },
  },
});

const endpoints: Endpoints = { rest: values.rest, grpc: values.grpc };

function seed(): bigint {
  return values.seed ? parseSeed(values.seed) : BigInt(Date.now()) * 1000n + BigInt(Math.floor(Math.random() * 1000));
}

function required(name: string, value: string | undefined): string {
  if (!value) throw new Error(`--${name} is required`);
  return value;
}

function json(value: unknown): string {
  return JSON.stringify(value, (_, v: unknown) => (typeof v === 'bigint' ? v.toString() : v), 2);
}

async function wait(): Promise<void> {
  const deadline = Date.now() + Number(values.timeout) * 1000;
  let last: unknown;

  for (const protocol of ['rest', 'grpc'] as const) {
    while (true) {
      const client = new CamusClient({ endpoint: endpoints[protocol], database: 'test', protocol, timeoutSeconds: 5 });
      try {
        await client.query('SHOW DATABASES');
        console.log(`${protocol}: ready`);
        break;
      } catch (error) {
        last = error;
        if (Date.now() > deadline) throw new Error(`the server did not answer on ${protocol}: ${errorText(last)}`);
        await new Promise((resolve) => setTimeout(resolve, 2000));
      } finally {
        await closeQuietly(client);
      }
    }
  }
}

async function runGenerate(): Promise<void> {
  const out = required('out', values.out);
  mkdirSync(out, { recursive: true });

  const s = seed();
  console.log(`seed: ${s}`);
  writeFileSync(join(out, 'generated.sql'), `-- camusdb-dump-fuzz seed ${s}\n`);

  const generated = await generate({ seed: s, endpoints, databases: Number(values.databases), logFile: join(out, 'generated.sql') });
  const report = { ...generated, findings: groupFindings(generated.findings) };
  writeFileSync(join(out, 'generate-report.json'), json(report));

  for (const db of report.databases)
    console.log(`${db.name}: ${db.tables} tables, ${db.sequences} sequences, ${db.rows} rows, written over ${db.protocol}`);
  console.log(`statements the server refused: ${JSON.stringify(report.rejected)}`);
  console.log(`findings: ${report.findings.length}`);

  // The rows are read as of one second before the dump starts, so the dump must not start sooner.
  await new Promise((resolve) => setTimeout(resolve, 2500));
}

/** The camus-dump options under test. Each run tries a different mix, and the seed makes it repeatable. */
function dumpPlan(): void {
  const rng = new Random(seed()).fork('dump-plan');

  const plan = (name: string) => {
    const r = rng.fork(name);
    const protocol: Protocol = r.chance(0.7) ? 'grpc' : 'rest';
    const args = ['--protocol', protocol, '-e', endpoints[protocol], '--no-progress', '-b', String(r.pick([1, 7, 100, 100, 1000]))];

    const consistency = r.pick(['as-of', 'as-of', 'as-of', 'single-transaction', 'no-as-of']);
    if (consistency !== 'as-of') args.push(`--${consistency}`);
    if (r.chance(0.3)) args.push('--defer-indexes');
    if (r.chance(0.3)) args.push('--add-drop-table');
    if (r.chance(0.3)) args.push('--if-not-exists');

    const ext = r.chance(0.4) ? 'sql.gz' : 'sql';
    console.log(`${name}_ARGS='${args.join(' ')}'`);
    console.log(`${name}_EXT='${ext}'`);
  };

  plan('PER_DB');
  plan('ALL_DB');
}

async function runSnapshot(): Promise<void> {
  const out = required('out', values.out);
  const result = await snapshot({
    endpoints,
    protocol: values.protocol as Protocol,
    prefix: values.prefix,
    label: values.label,
    probe: values.probe,
  });
  writeFileSync(out, json({ ...result, findings: groupFindings(result.findings) }));

  for (const [name, db] of Object.entries(result.databases)) {
    const rows = Object.values(db.tables).reduce((sum, t) => sum + t.rowCount, 0);
    console.log(`${name}: ${Object.keys(db.tables).length} tables, ${db.sequences.length} sequences, ${rows} rows`);
  }
  console.log(`findings: ${result.findings.length}`);
}

function runCompare(): number {
  const expected = JSON.parse(readFileSync(required('expected', values.expected), 'utf8')) as Snapshot;
  const actual = JSON.parse(readFileSync(required('actual', values.actual), 'utf8')) as Snapshot;

  const differences = compare(expected, actual);
  const text = formatDifferences(values.title, differences);
  console.log(text);
  if (values.report) writeFileSync(values.report, text);

  return differences.length === 0 ? 0 : 1;
}

function runFindings(): number {
  const allowed = new Set(values.allow);
  let failing = 0;

  for (const file of values.file) {
    const findings = (JSON.parse(readFileSync(file, 'utf8')) as { findings: Finding[] }).findings;
    const groups = new Map<string, Finding[]>();
    for (const f of findings) groups.set(f.kind, [...(groups.get(f.kind) ?? []), f]);

    for (const [kind, list] of groups) {
      const note = allowed.has(kind) ? ' (allowed)' : '';
      console.log(`${file}: ${kind}${note}, ${list.reduce((n, f) => n + (f.count ?? 1), 0)} times`);
      for (const f of list.slice(0, 10)) {
        const times = f.count && f.count > 1 ? ` (×${f.count})` : '';
        console.log(`  - ${[f.database, f.table].filter(Boolean).join('.')}${times}: ${f.detail.replaceAll('\n', '\n    ')}`);
      }
      if (list.length > 10) console.log(`  … and ${list.length - 10} more`);
      if (!allowed.has(kind)) failing += list.length;
    }
  }

  if (failing === 0) console.log('No findings.');
  return failing === 0 ? 0 : 1;
}

async function main(): Promise<number> {
  const command = positionals[0];
  if (values.help || !command) {
    console.log(USAGE);
    return command ? 0 : 2;
  }

  switch (command) {
    case 'wait':
      await wait();
      return 0;
    case 'generate':
      await runGenerate();
      return 0;
    case 'dump-plan':
      dumpPlan();
      return 0;
    case 'snapshot':
      await runSnapshot();
      return 0;
    case 'compare':
      return runCompare();
    case 'findings':
      return runFindings();
    default:
      console.error(`unknown command '${command}'\n\n${USAGE}`);
      return 2;
  }
}

main().then(
  (code) => process.exit(code),
  (error: unknown) => {
    console.error(errorText(error));
    if (error instanceof Error && error.stack) console.error(error.stack);
    process.exit(1);
  },
);
