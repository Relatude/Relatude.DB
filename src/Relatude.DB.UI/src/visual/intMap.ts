/**
 * A map from non-negative int keys to non-negative int values on two typed arrays - open addressing,
 * linear probing - for the one place the visual pivot needs to look a node up by id: matching the
 * cards of a new result to the cards of the old one, so the ones that stay can travel from where
 * they were. A Map of a million entries is tens of megabytes and a good part of a second to build;
 * this is eight bytes an entry and a pass over the ids.
 */
export class IntMap {
  private readonly keys: Int32Array;
  private readonly values: Int32Array;
  private readonly mask: number;

  constructor(capacity: number) {
    let size = 16;
    while (size < capacity * 2) size *= 2;
    this.keys = new Int32Array(size).fill(-1);
    this.values = new Int32Array(size);
    this.mask = size - 1;
  }

  set(key: number, value: number): void {
    if (key < 0) return;
    let i = hash(key) & this.mask;
    while (this.keys[i] !== -1 && this.keys[i] !== key) i = (i + 1) & this.mask;
    this.keys[i] = key;
    this.values[i] = value;
  }

  /** the value, or -1 */
  get(key: number): number {
    if (key < 0) return -1;
    let i = hash(key) & this.mask;
    while (this.keys[i] !== -1) {
      if (this.keys[i] === key) return this.values[i];
      i = (i + 1) & this.mask;
    }
    return -1;
  }
}

function hash(key: number): number {
  let h = key | 0;
  h = Math.imul(h ^ (h >>> 16), 0x45d9f3b);
  h = Math.imul(h ^ (h >>> 16), 0x45d9f3b);
  return (h ^ (h >>> 16)) >>> 0;
}
