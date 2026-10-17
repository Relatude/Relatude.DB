export interface PatternPart {
  code: string;
  meaning: string;
  depth: number;
}

interface WorkingPart extends PatternPart {
  literal?: string;
  quantified?: boolean;
  groupEnd?: boolean;
}

const shorthand: Record<string, string> = {
  d: "a digit 0-9",
  D: "any character that is not a digit",
  w: "a letter, digit or underscore",
  W: "any character that is not a letter, digit or underscore",
  s: "a space, tab or line break",
  S: "any character that is not a space, tab or line break",
  b: "a word boundary (between a letter and a non-letter)",
  B: "not a word boundary",
  A: "the start of the value",
  z: "the very end of the value",
  Z: "the end of the value",
  t: "a tab",
  n: "a line break",
  r: "a carriage return",
};

const unicodeCategories: Record<string, string> = {
  L: "a letter in any language",
  Lu: "an upper case letter in any language",
  Ll: "a lower case letter in any language",
  N: "a digit or number character in any script",
  Nd: "a digit in any script",
  P: "a punctuation character",
  S: "a symbol",
  Z: "a space character",
  Zs: "a space character",
  M: "an accent or other mark",
};

function describeCharacter(c: string): string {
  if (c === " ") return "a space";
  return `the character "${c}"`;
}

function describeUnicode(name: string, negated: boolean): string {
  const meaning = unicodeCategories[name] ?? `a character in the Unicode category ${name}`;
  return negated ? `anything but ${meaning}` : meaning;
}

function readEscape(pattern: string, i: number): { code: string; meaning: string; literal?: string } {
  const c = pattern[i + 1];
  if (c === undefined) return { code: "\\", meaning: "a lone backslash - this is an error" };
  if ((c === "p" || c === "P") && pattern[i + 2] === "{") {
    const end = pattern.indexOf("}", i + 3);
    if (end > 0) {
      const name = pattern.slice(i + 3, end);
      return { code: pattern.slice(i, end + 1), meaning: describeUnicode(name, c === "P") };
    }
  }
  if (shorthand[c]) return { code: "\\" + c, meaning: shorthand[c] };
  if (/[1-9]/.test(c)) return { code: "\\" + c, meaning: `the same text as group ${c} matched` };
  return { code: "\\" + c, meaning: describeCharacter(c), literal: c };
}

function readCharacterClass(pattern: string, start: number): { code: string; meaning: string; end: number } {
  let i = start + 1;
  const negated = pattern[i] === "^";
  if (negated) i++;
  const items: string[] = [];
  let ranges = 0;
  let singles = 0;
  let first = true;
  while (i < pattern.length && (pattern[i] !== "]" || first)) {
    first = false;
    let item: string;
    let itemEnd: number;
    if (pattern[i] === "\\") {
      const e = readEscape(pattern, i);
      item = e.literal !== undefined ? quote(e.literal) : e.meaning;
      itemEnd = i + e.code.length;
    } else {
      item = quote(pattern[i]);
      itemEnd = i + 1;
    }
    if (pattern[itemEnd] === "-" && pattern[itemEnd + 1] !== undefined && pattern[itemEnd + 1] !== "]") {
      const to = pattern[itemEnd + 1] === "\\" ? readEscape(pattern, itemEnd + 1) : { code: pattern[itemEnd + 1], literal: pattern[itemEnd + 1], meaning: "" };
      items.push(`${item} to ${quote(to.literal ?? to.code)}`);
      ranges++;
      i = itemEnd + 1 + to.code.length;
    } else {
      items.push(item);
      if (item.startsWith('"') || item === "space") singles++;
      i = itemEnd;
    }
  }
  const end = Math.min(i + 1, pattern.length);
  const list = items.length <= 1 ? (items[0] ?? "nothing") : items.slice(0, -1).join(", ") + " or " + items[items.length - 1];
  const kind = ranges === items.length ? "one character from" : singles === items.length ? "one of the characters" : "one character:";
  return { code: pattern.slice(start, end), meaning: negated ? `one character that is not ${list}` : `${kind} ${list}`, end };
}

function quote(c: string): string {
  return c === " " ? "space" : `"${c}"`;
}

function readQuantifier(pattern: string, i: number): { code: string; phrase: string } | null {
  const c = pattern[i];
  let code: string;
  let phrase: string;
  if (c === "*") {
    code = "*";
    phrase = "any number of times, also none";
  } else if (c === "+") {
    code = "+";
    phrase = "one or more times";
  } else if (c === "?") {
    code = "?";
    phrase = "optional";
  } else if (c === "{") {
    const m = /^\{(\d+)(,(\d*))?\}/.exec(pattern.slice(i));
    if (!m) return null;
    code = m[0];
    const from = m[1];
    if (m[2] === undefined) phrase = `exactly ${from} time${from === "1" ? "" : "s"}`;
    else if (m[3] === "") phrase = `${from} or more times`;
    else phrase = `${from} to ${m[3]} times`;
  } else {
    return null;
  }
  if (pattern[i + code.length] === "?") {
    code += "?";
    phrase += ", as few as possible";
  }
  return { code, phrase };
}

function describeGroupStart(pattern: string, i: number): { code: string; meaning: string; opensGroup: boolean } {
  const rest = pattern.slice(i);
  const named = /^\(\?<([A-Za-z_]\w*)>/.exec(rest) ?? /^\(\?'([A-Za-z_]\w*)'/.exec(rest);
  if (named) return { code: named[0], meaning: `start of a group named "${named[1]}"`, opensGroup: true };
  if (rest.startsWith("(?:")) return { code: "(?:", meaning: "start of a group", opensGroup: true };
  if (rest.startsWith("(?=")) return { code: "(?=", meaning: "followed by (checked, not part of the match):", opensGroup: true };
  if (rest.startsWith("(?!")) return { code: "(?!", meaning: "not followed by:", opensGroup: true };
  if (rest.startsWith("(?<=")) return { code: "(?<=", meaning: "preceded by:", opensGroup: true };
  if (rest.startsWith("(?<!")) return { code: "(?<!", meaning: "not preceded by:", opensGroup: true };
  if (rest.startsWith("(?>")) return { code: "(?>", meaning: "start of a group that never gives back what it matched", opensGroup: true };
  const options = /^\(\?([imnsx]+)(-[imnsx]+)?(:|\))/.exec(rest);
  if (options) {
    const ignoresCase = options[1].includes("i");
    const scope = options[3] === ":" ? "inside this group" : "from here on";
    return { code: options[0], meaning: ignoresCase ? `${scope}, upper and lower case count as the same` : `${scope}, matching options ${options[1]} apply`, opensGroup: options[3] === ":" };
  }
  return { code: "(", meaning: "start of a group", opensGroup: true };
}

export function explainPattern(pattern: string): PatternPart[] {
  const parts: WorkingPart[] = [];
  let depth = 0;
  let i = 0;
  const add = (part: WorkingPart) => parts.push(part);
  const addLiteral = (char: string, code: string) => {
    const last = parts[parts.length - 1];
    if (last && last.literal !== undefined && !last.quantified && last.depth === depth && !readQuantifier(pattern, i + code.length)) {
      last.literal += char;
      last.code += code;
      last.meaning = `the text "${last.literal}"`;
      return;
    }
    add({ code, meaning: describeCharacter(char), depth, literal: char });
  };
  while (i < pattern.length) {
    const c = pattern[i];
    const quantifier = readQuantifier(pattern, i);
    if (quantifier && parts.length > 0) {
      const last = parts[parts.length - 1];
      const subject = last.groupEnd ? "the group" : "this";
      last.code += quantifier.code;
      last.meaning = last.groupEnd ? `end of the group - ${subject} appears ${quantifier.phrase}` : `${last.meaning}, ${quantifier.phrase}`;
      last.quantified = true;
      i += quantifier.code.length;
      continue;
    }
    if (c === "^") {
      add({ code: "^", meaning: depth === 0 && i === 0 ? "the start of the value" : "the start of the value (or line)", depth });
      i++;
    } else if (c === "$") {
      add({ code: "$", meaning: "the end of the value", depth });
      i++;
    } else if (c === ".") {
      add({ code: ".", meaning: "any character except a line break", depth });
      i++;
    } else if (c === "|") {
      add({ code: "|", meaning: depth === 0 ? "or - everything before is one choice, everything after another" : "or", depth: Math.max(0, depth - 1) });
      i++;
    } else if (c === "[") {
      const cls = readCharacterClass(pattern, i);
      add({ code: cls.code, meaning: cls.meaning, depth });
      i = cls.end;
    } else if (c === "(") {
      const g = describeGroupStart(pattern, i);
      add({ code: g.code, meaning: g.meaning, depth });
      if (g.opensGroup) depth++;
      i += g.code.length;
    } else if (c === ")") {
      depth = Math.max(0, depth - 1);
      add({ code: ")", meaning: "end of the group", depth, groupEnd: true });
      i++;
    } else if (c === "\\") {
      const e = readEscape(pattern, i);
      if (e.literal !== undefined) addLiteral(e.literal, e.code);
      else add({ code: e.code, meaning: e.meaning, depth });
      i += e.code.length;
    } else {
      addLiteral(c, c);
      i++;
    }
  }
  return parts.map(({ code, meaning, depth }) => ({ code, meaning, depth }));
}

const caseIgnoredPrefix = "(?i)";
const optionalWhole = /^\^\(\?:([\s\S]*)\)\?\$$/;

export function ignoresCase(pattern: string): boolean {
  return pattern.startsWith(caseIgnoredPrefix);
}

export function withCaseIgnored(pattern: string, ignored: boolean): string {
  if (ignored === ignoresCase(pattern)) return pattern;
  return ignored ? caseIgnoredPrefix + pattern : pattern.slice(caseIgnoredPrefix.length);
}

function splitCasePrefix(pattern: string): [prefix: string, body: string] {
  return ignoresCase(pattern) ? [caseIgnoredPrefix, pattern.slice(caseIgnoredPrefix.length)] : ["", pattern];
}

export function allowsEmptyValue(pattern: string): boolean {
  return optionalWhole.test(splitCasePrefix(pattern)[1]);
}

export function withEmptyValueAllowed(pattern: string, allowed: boolean): string {
  if (allowed === allowsEmptyValue(pattern)) return pattern;
  const [prefix, body] = splitCasePrefix(pattern);
  if (allowed) return `${prefix}^(?:${body.replace(/^\^/, "").replace(/\$$/, "")})?$`;
  return `${prefix}^${optionalWhole.exec(body)![1]}$`;
}
