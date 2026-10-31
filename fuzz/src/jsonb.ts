import type { Random } from './random.js';

/**
 * JSONB documents for the fuzzer: a generator of JSON input text, and an independent formatter of the
 * text form the server stores and returns.
 *
 * The generator writes the input the way an application might: random white space, keys in any order,
 * a repeated key now and then, numbers with exponents, and escapes the server decodes. The formatter
 * gives the PostgreSQL `jsonb_out` form the server reads back:
 *
 * - keys sorted by UTF-8 byte length, then by bytes, and the last value of a repeated key kept;
 * - a space after each `:` and each `,`, and no other white space;
 * - each number with every digit and its scale, the exponent removed, and no sign on a zero;
 * - strings escaped as `JSON.stringify` escapes them.
 *
 * This file shares no code with the dump tool or the driver, so a bug in one of them does not hide
 * the same bug here.
 */

type Json =
  | { readonly t: 'null' }
  | { readonly t: 'bool'; readonly v: boolean }
  | { readonly t: 'num'; readonly text: string }
  | { readonly t: 'str'; readonly v: string }
  | { readonly t: 'arr'; readonly items: readonly Json[] }
  | { readonly t: 'obj'; readonly pairs: readonly (readonly [string, Json])[] };

// ---------------------------------------------------------------------------------------------
// Generation
// ---------------------------------------------------------------------------------------------

const NUMBERS: readonly string[] = [
  '0', '-0', '-0.0', '1', '-1', '2.50', '0.1', '-0.000100', '1e3', '12E-1', '1.50e1', '5e-3', '-1.20E-2',
  '100e-2', '0e-3', '1E+2', '9007199254740993', '-9223372036854775809', '12345678901234567890123456789.123456789',
  '123456789012345678901234567890e-25', '3.14159265358979323846264338327950288',
];

const STRINGS: readonly string[] = [
  '', 'a', 'login', "it's", '"quoted"', '\\', 'a\\', '\n', '\r\n', '\t', '\u0001', '\u001f', '\u007f', '\u0085',
  '/', ';', '--', "E'", 'é', '日本語', '😀', '👩‍💻', '​', '﻿', 'NULL', 'null', 'true', '{"not": "nested"}',
  '[1, 2]', 'DROP TABLE x;', ' ', '$$',
];

const KEYS: readonly string[] = [
  'a', 'b', 'aa', 'ab', 'B', 'id', 'kind', 'user', 'tags', 'price', 'é', '', 'a b', '"q"', '\\', '😀', 'null',
  'key', 'value', 'z', 'deleted_at',
];

/** A random document, as JSON input text. */
export function generateJsonb(rng: Random): string {
  const roll = rng.float();
  let doc: Json;

  if (roll < 0.05) doc = { t: 'null' };
  else if (roll < 0.1) doc = scalar(rng);
  else if (roll < 0.13) doc = deep(rng, rng.int(65, 200));
  else if (roll < 0.16) doc = large(rng);
  else if (roll < 0.35) doc = array(rng, 0);
  else doc = object(rng, 0);

  return input(doc, rng);
}

function value(rng: Random, depth: number): Json {
  if (depth >= 4) return scalar(rng);
  const roll = rng.float();
  if (roll < 0.15) return object(rng, depth + 1);
  if (roll < 0.25) return array(rng, depth + 1);
  return scalar(rng);
}

function scalar(rng: Random): Json {
  const roll = rng.float();
  if (roll < 0.1) return { t: 'null' };
  if (roll < 0.2) return { t: 'bool', v: rng.chance(0.5) };
  if (roll < 0.55) return { t: 'num', text: number(rng) };
  return { t: 'str', v: string(rng) };
}

function number(rng: Random): string {
  if (rng.chance(0.5)) return rng.pick(NUMBERS);

  const sign = rng.chance(0.3) ? '-' : '';
  const intPart = rng.chance(0.3) ? '0' : String(rng.int(1, 9)) + digits(rng, rng.int(0, 25));
  const frac = rng.chance(0.5) ? '' : '.' + digits(rng, rng.int(1, 12));
  const exp = rng.chance(0.15) ? rng.pick(['e', 'E']) + rng.pick(['', '+', '-']) + String(rng.int(0, 30)) : '';
  return sign + intPart + frac + exp;
}

function digits(rng: Random, count: number): string {
  let text = '';
  for (let i = 0; i < count; i++) text += String(rng.int(0, 9));
  return text;
}

function string(rng: Random): string {
  let text = '';
  for (let i = rng.int(0, 3); i > 0; i--) text += rng.pick(STRINGS);
  return text;
}

function object(rng: Random, depth: number): Json {
  const pairs: [string, Json][] = [];
  for (let i = rng.pick([0, 1, 2, 3, 4, 6]); i > 0; i--) {
    const key = rng.chance(0.8) ? rng.pick(KEYS) : string(rng);
    pairs.push([key, value(rng, depth)]);
  }
  return { t: 'obj', pairs };
}

function array(rng: Random, depth: number): Json {
  const items: Json[] = [];
  for (let i = rng.pick([0, 1, 2, 3, 5]); i > 0; i--) items.push(value(rng, depth));
  return { t: 'arr', items };
}

/** A document nested deeper than the 64 levels a default JSON reader allows; the server allows 512. */
function deep(rng: Random, levels: number): Json {
  let doc: Json = scalar(rng);
  for (let i = 0; i < levels; i++) doc = rng.chance(0.5) ? { t: 'arr', items: [doc] } : { t: 'obj', pairs: [['k', doc]] };
  return doc;
}

/** A document above the server's large-value threshold (2048 bytes), stored out of line. */
function large(rng: Random): Json {
  const items: Json[] = [];
  for (let i = rng.int(150, 400); i > 0; i--) items.push(rng.chance(0.5) ? { t: 'num', text: number(rng) } : { t: 'str', v: string(rng) + 'xxxxxxxx' });
  return { t: 'obj', pairs: [['items', { t: 'arr', items }], ['note', { t: 'str', v: 'x'.repeat(rng.int(0, 3000)) }]] };
}

/** The document as input text, with random white space, escapes and repeated keys. */
function input(doc: Json, rng: Random): string {
  const ws = () => (rng.chance(0.7) ? '' : rng.pick([' ', '  ', '\n', '\t', '\r\n ']));

  const write = (node: Json): string => {
    switch (node.t) {
      case 'null':
        return 'null';
      case 'bool':
        return node.v ? 'true' : 'false';
      case 'num':
        return node.text;
      case 'str':
        return inputString(node.v, rng);
      case 'arr':
        return `[${ws()}${node.items.map((item) => write(item)).join(`${ws()},${ws()}`)}${ws()}]`;
      case 'obj': {
        const parts: string[] = [];
        for (const [key, item] of node.pairs) {
          // A repeated key: the server keeps the last value, so the earlier one is a decoy.
          if (rng.chance(0.05)) parts.push(`${inputString(key, rng)}${ws()}:${ws()}${write(scalar(rng))}`);
          parts.push(`${inputString(key, rng)}${ws()}:${ws()}${write(item)}`);
        }
        return `{${ws()}${parts.join(`${ws()},${ws()}`)}${ws()}}`;
      }
    }
  };

  return ws() + write(doc) + ws();
}

/** A JSON string literal, now and then with escapes the server decodes: \/ and \uXXXX. */
function inputString(text: string, rng: Random): string {
  const plain = JSON.stringify(text);
  if (rng.chance(0.8)) return plain;

  let out = '"';
  for (const unit of plain.slice(1, -1).split(/(\\.)/)) {
    if (unit.startsWith('\\')) {
      out += unit;
      continue;
    }
    // By code point, so both halves of a surrogate pair are escaped or neither is: the server refuses a
    // lone surrogate escape.
    for (const c of unit) {
      if (c === '/') out += rng.chance(0.5) ? '\\/' : '/';
      else if (c.codePointAt(0)! > 0x7f && rng.chance(0.5))
        for (let i = 0; i < c.length; i++) out += `\\u${c.charCodeAt(i).toString(16).padStart(4, '0')}`;
      else out += c;
    }
  }
  return out + '"';
}

// ---------------------------------------------------------------------------------------------
// The server's text form
// ---------------------------------------------------------------------------------------------

/** The text form the server returns for a document written as `text`. */
export function canonicalJsonb(text: string): string {
  const parser = new Parser(text);
  const doc = parser.document();
  return format(doc);
}

function format(node: Json): string {
  switch (node.t) {
    case 'null':
      return 'null';
    case 'bool':
      return node.v ? 'true' : 'false';
    case 'num':
      return canonicalNumber(node.text);
    case 'str':
      return JSON.stringify(node.v);
    case 'arr':
      return `[${node.items.map(format).join(', ')}]`;
    case 'obj': {
      const last = new Map<string, Json>();
      for (const [key, item] of node.pairs) last.set(key, item);
      const keys = [...last.keys()].sort(compareKeys);
      return `{${keys.map((key) => `${JSON.stringify(key)}: ${format(last.get(key)!)}`).join(', ')}}`;
    }
  }
}

/** The PostgreSQL key order: the shorter UTF-8 key first, then by bytes. */
function compareKeys(a: string, b: string): number {
  const x = Buffer.from(a, 'utf8');
  const y = Buffer.from(b, 'utf8');
  return x.length !== y.length ? x.length - y.length : Buffer.compare(x, y);
}

/**
 * A JSON number as PostgreSQL `numeric` writes it. The scale is the count of fraction digits minus
 * the exponent, and never below zero; the digits are written without an exponent; a zero has no sign.
 */
export function canonicalNumber(text: string): string {
  const match = /^(-?)(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$/.exec(text);
  if (!match) throw new Error(`not a JSON number: ${text}`);

  const [, sign = '', intPart = '', frac = '', expText = '0'] = match;
  const exp = Number(expText);
  const scale = Math.max(0, frac.length - exp);
  const scaled = BigInt(intPart + frac) * 10n ** BigInt(exp - frac.length + scale);

  let body = scaled.toString().padStart(scale + 1, '0');
  if (scale > 0) body = `${body.slice(0, -scale)}.${body.slice(-scale)}`;
  return scaled === 0n ? body : sign + body;
}

/** A JSON parser that keeps each number as its source text, so no digit or scale is lost. */
class Parser {
  private pos = 0;

  constructor(private readonly text: string) {}

  document(): Json {
    const doc = this.value();
    this.skip();
    if (this.pos !== this.text.length) throw new Error(`trailing text at ${this.pos}`);
    return doc;
  }

  private skip(): void {
    while (this.pos < this.text.length && ' \t\n\r'.includes(this.text[this.pos]!)) this.pos++;
  }

  private value(): Json {
    this.skip();
    const c = this.text[this.pos];

    if (c === '{') return this.object();
    if (c === '[') return this.array();
    if (c === '"') return { t: 'str', v: this.string() };
    if (this.text.startsWith('null', this.pos)) return (this.pos += 4), { t: 'null' };
    if (this.text.startsWith('true', this.pos)) return (this.pos += 4), { t: 'bool', v: true };
    if (this.text.startsWith('false', this.pos)) return (this.pos += 5), { t: 'bool', v: false };

    const number = /^-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?/.exec(this.text.slice(this.pos));
    if (!number) throw new Error(`unexpected text at ${this.pos}`);
    this.pos += number[0].length;
    return { t: 'num', text: number[0] };
  }

  private object(): Json {
    const pairs: [string, Json][] = [];
    this.pos++;
    this.skip();
    if (this.text[this.pos] === '}') return this.pos++, { t: 'obj', pairs };

    for (;;) {
      this.skip();
      const key = this.string();
      this.skip();
      this.expect(':');
      pairs.push([key, this.value()]);
      this.skip();
      if (this.text[this.pos] === '}') return this.pos++, { t: 'obj', pairs };
      this.expect(',');
    }
  }

  private array(): Json {
    const items: Json[] = [];
    this.pos++;
    this.skip();
    if (this.text[this.pos] === ']') return this.pos++, { t: 'arr', items };

    for (;;) {
      items.push(this.value());
      this.skip();
      if (this.text[this.pos] === ']') return this.pos++, { t: 'arr', items };
      this.expect(',');
    }
  }

  private string(): string {
    this.expect('"');
    let end = this.pos;
    while (this.text[end] !== '"') end += this.text[end] === '\\' ? 2 : 1;
    const raw = this.text.slice(this.pos - 1, end + 1);
    this.pos = end + 1;
    return JSON.parse(raw) as string;
  }

  private expect(c: string): void {
    if (this.text[this.pos] !== c) throw new Error(`expected ${c} at ${this.pos}`);
    this.pos++;
  }
}
