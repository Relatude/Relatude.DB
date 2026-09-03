// A small text diff, for showing how one version of a value became another.
//
// Nothing clever: the two texts are cut into tokens (words, characters or lines), the common
// prefix and suffix are set aside, and what is left is aligned with a longest-common-subsequence
// table. That is quadratic in the middle part, which is fine for the values a node holds (a few
// thousand words) and is capped: two middles too large to table are shown as one replacement
// rather than left to lock the page up. The result is a list of runs to paint - equal, deleted,
// inserted - with every change hunk giving its deletions before its insertions, which is how a
// person expects to read one.

export type Granularity = "words" | "chars" | "lines";

export interface DiffRun {
  kind: "equal" | "delete" | "insert";
  text: string;
}

/** A hunk pairs what was on the old side with what is on the new side; equal hunks have both the same. */
export interface DiffHunk {
  equal: boolean;
  old: string;
  new: string;
}

// the largest LCS table that is built; beyond it the middle is one replacement (Uint16 cells, so ~8MB)
const maxTableCells = 4_000_000;

/** Cuts a text into the units a diff aligns. Separators stay as tokens of their own, so joining the tokens gives the text back. */
export function tokenize(text: string, granularity: Granularity): string[] {
  if (text === "") return [];
  switch (granularity) {
    case "chars":
      return Array.from(text);
    case "lines":
      return text.split(/(\r?\n)/).filter((t) => t !== "");
    case "words":
      // words and the whitespace between them alternate, and punctuation stays on its word:
      // a diff of prose reads best when "word," is one unit
      return text.split(/(\s+)/).filter((t) => t !== "");
  }
}

export function diffText(a: string, b: string, granularity: Granularity): DiffRun[] {
  return diffTokens(tokenize(a, granularity), tokenize(b, granularity));
}

export function diffTokens(a: string[], b: string[]): DiffRun[] {
  let start = 0;
  while (start < a.length && start < b.length && a[start] === b[start]) start++;
  let endA = a.length;
  let endB = b.length;
  while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) {
    endA--;
    endB--;
  }
  const runs: DiffRun[] = [];
  if (start > 0) runs.push({ kind: "equal", text: a.slice(0, start).join("") });
  const midA = a.slice(start, endA);
  const midB = b.slice(start, endB);
  if (midA.length === 0) {
    if (midB.length > 0) runs.push({ kind: "insert", text: midB.join("") });
  } else if (midB.length === 0) {
    runs.push({ kind: "delete", text: midA.join("") });
  } else if (midA.length * midB.length > maxTableCells) {
    runs.push({ kind: "delete", text: midA.join("") }, { kind: "insert", text: midB.join("") });
  } else {
    runs.push(...lcsDiff(midA, midB));
  }
  if (endA < a.length) runs.push({ kind: "equal", text: a.slice(endA).join("") });
  return merge(runs);
}

// the classic table: cell (i, j) is the length of the longest common subsequence of a[0..i) and
// b[0..j); walking back from the corner reads off one alignment
function lcsDiff(a: string[], b: string[]): DiffRun[] {
  const n = a.length;
  const m = b.length;
  const width = m + 1;
  const table = new Uint16Array((n + 1) * width);
  for (let i = 1; i <= n; i++) {
    const ai = a[i - 1];
    const row = i * width;
    const above = row - width;
    for (let j = 1; j <= m; j++) {
      table[row + j] = ai === b[j - 1] ? table[above + j - 1] + 1 : Math.max(table[above + j], table[row + j - 1]);
    }
  }
  const reversed: DiffRun[] = [];
  let i = n;
  let j = m;
  while (i > 0 || j > 0) {
    if (i > 0 && j > 0 && a[i - 1] === b[j - 1]) {
      reversed.push({ kind: "equal", text: a[i - 1] });
      i--;
      j--;
    } else if (j > 0 && (i === 0 || table[i * width + j - 1] >= table[(i - 1) * width + j])) {
      reversed.push({ kind: "insert", text: b[j - 1] });
      j--;
    } else {
      reversed.push({ kind: "delete", text: a[i - 1] });
      i--;
    }
  }
  reversed.reverse();
  return reversed;
}

// joins runs of one kind, and within a change hunk puts every deletion before every insertion
function merge(runs: DiffRun[]): DiffRun[] {
  const out: DiffRun[] = [];
  let deleted = "";
  let inserted = "";
  const flush = () => {
    if (deleted) out.push({ kind: "delete", text: deleted });
    if (inserted) out.push({ kind: "insert", text: inserted });
    deleted = "";
    inserted = "";
  };
  for (const run of runs) {
    if (run.kind === "equal") {
      flush();
      const last = out[out.length - 1];
      if (last && last.kind === "equal") last.text += run.text;
      else out.push({ kind: "equal", text: run.text });
    } else if (run.kind === "delete") deleted += run.text;
    else inserted += run.text;
  }
  flush();
  return out;
}

/** The runs as aligned hunks, for a side by side view: a change hunk shows its old text on the left and its new text on the right. */
export function toHunks(runs: DiffRun[]): DiffHunk[] {
  const hunks: DiffHunk[] = [];
  let pending: DiffHunk | null = null;
  for (const run of runs) {
    if (run.kind === "equal") {
      if (pending) hunks.push(pending);
      pending = null;
      hunks.push({ equal: true, old: run.text, new: run.text });
    } else {
      pending ??= { equal: false, old: "", new: "" };
      if (run.kind === "delete") pending.old += run.text;
      else pending.new += run.text;
    }
  }
  if (pending) hunks.push(pending);
  return hunks;
}

/** Characters inserted and deleted, for a one-glance measure of how much changed. */
export function diffStats(runs: DiffRun[]): { inserted: number; deleted: number } {
  let inserted = 0;
  let deleted = 0;
  for (const run of runs) {
    if (run.kind === "insert") inserted += run.text.length;
    else if (run.kind === "delete") deleted += run.text.length;
  }
  return { inserted, deleted };
}
