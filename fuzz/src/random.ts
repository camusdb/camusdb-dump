/**
 * A seeded random source. Every choice the fuzzer makes comes from one of these, so a seed printed by
 * a failed run reproduces that run exactly.
 */
export class Random {
  private state: bigint;

  constructor(seed: bigint) {
    this.state = BigInt.asUintN(64, seed);
  }

  /** splitmix64: small, fast, and good enough to spread choices evenly. */
  private next64(): bigint {
    this.state = BigInt.asUintN(64, this.state + 0x9e3779b97f4a7c15n);
    let z = this.state;
    z = BigInt.asUintN(64, (z ^ (z >> 30n)) * 0xbf58476d1ce4e5b9n);
    z = BigInt.asUintN(64, (z ^ (z >> 27n)) * 0x94d049bb133111ebn);
    return z ^ (z >> 31n);
  }

  /** A float in [0, 1). */
  float(): number {
    return Number(this.next64() >> 11n) / 2 ** 53;
  }

  /** An integer in [min, max], both ends included. */
  int(min: number, max: number): number {
    return min + Math.floor(this.float() * (max - min + 1));
  }

  /** A 64-bit signed integer, uniform over the whole range. */
  int64(): bigint {
    return BigInt.asIntN(64, this.next64());
  }

  /** A bigint in [min, max], both ends included. */
  bigintIn(min: bigint, max: bigint): bigint {
    const span = max - min + 1n;
    return min + (BigInt.asUintN(64, this.next64()) % span);
  }

  chance(probability: number): boolean {
    return this.float() < probability;
  }

  pick<T>(items: readonly T[]): T {
    if (items.length === 0) throw new Error('pick from an empty list');
    return items[this.int(0, items.length - 1)] as T;
  }

  /** A random subset in the original order. */
  subset<T>(items: readonly T[], probability: number): T[] {
    return items.filter(() => this.chance(probability));
  }

  shuffle<T>(items: readonly T[]): T[] {
    const copy = [...items];
    for (let i = copy.length - 1; i > 0; i--) {
      const j = this.int(0, i);
      [copy[i], copy[j]] = [copy[j] as T, copy[i] as T];
    }
    return copy;
  }

  bytes(length: number): Uint8Array {
    const out = new Uint8Array(length);
    for (let i = 0; i < length; i++) out[i] = this.int(0, 255);
    return out;
  }

  /** A child source whose choices do not shift when the parent makes more or fewer choices. */
  fork(label: string): Random {
    let h = this.state;
    for (const c of label) h = BigInt.asUintN(64, (h ^ BigInt(c.charCodeAt(0))) * 0x100000001b3n);
    return new Random(h ^ this.next64());
  }
}

/** Parses a seed given as a decimal number, or hashes any other text into one. */
export function parseSeed(text: string): bigint {
  if (/^\d+$/.test(text)) return BigInt(text);

  let h = 0xcbf29ce484222325n;
  for (const c of text) h = BigInt.asUintN(64, (h ^ BigInt(c.charCodeAt(0))) * 0x100000001b3n);
  return h;
}
