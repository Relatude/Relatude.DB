import type { Language } from "./language";

// Basic linting for the code editor: what can be checked without a real parser for each language.
// JSON and XML get a proper parse (the browser has one); HTML gets a tag balance; CSS, JavaScript,
// TypeScript and C# get bracket, string and comment balance. The point is to catch the slip that
// breaks a config file - a missing brace or quote - not to judge the code.

export interface LintIssue {
  line: number; // 1-based
  message: string;
}

const maxIssues = 20;

export function lint(text: string, language: Language): LintIssue[] {
  if (text.trim().length === 0) return [];
  switch (language) {
    case "json":
      return lintJson(text);
    case "xml":
      return lintXml(text);
    case "html":
      return lintHtml(text);
    case "css":
      return lintBrackets(text, "css");
    case "javascript":
    case "typescript":
      return lintBrackets(text, "js");
    case "csharp":
      return lintBrackets(text, "cs");
    default:
      return [];
  }
}

/** Whether lint knows anything about the language (so an empty result means "no problems found"). */
export function canLint(language: Language): boolean {
  return language !== "plain" && language !== "markdown";
}

export function lineOfOffset(text: string, offset: number): number {
  let line = 1;
  const end = Math.min(offset, text.length);
  for (let i = 0; i < end; i++) if (text.charCodeAt(i) === 10) line++;
  return line;
}

// ---- json ----

/** Comments blanked out (same length, so offsets survive): tsconfig and friends carry them. */
export function stripJsonComments(text: string): string {
  let out = "";
  let i = 0;
  const n = text.length;
  while (i < n) {
    const c = text[i];
    if (c === '"') {
      let j = i + 1;
      while (j < n && text[j] !== '"' && text[j] !== "\n") {
        if (text[j] === "\\") j++;
        j++;
      }
      out += text.slice(i, Math.min(j + 1, n));
      i = j + 1;
    } else if (c === "/" && text[i + 1] === "/") {
      let j = i;
      while (j < n && text[j] !== "\n") j++;
      out += " ".repeat(j - i);
      i = j;
    } else if (c === "/" && text[i + 1] === "*") {
      let j = text.indexOf("*/", i + 2);
      if (j < 0) j = n;
      else j += 2;
      out += text.slice(i, j).replace(/[^\n]/g, " ");
      i = j;
    } else {
      out += c;
      i++;
    }
  }
  return out;
}

function lintJson(text: string): LintIssue[] {
  try {
    JSON.parse(stripJsonComments(text));
    return [];
  } catch (error) {
    const raw = error instanceof Error ? error.message : String(error);
    // Chromium: "... in JSON at position 12 (line 2 column 3)"; Firefox: "JSON.parse: ... at line 2 column 3 of the JSON data"
    const lineMatch = /line (\d+)/i.exec(raw);
    const positionMatch = /position (\d+)/i.exec(raw);
    const line = lineMatch ? Number(lineMatch[1]) : positionMatch ? lineOfOffset(text, Number(positionMatch[1])) : 1;
    const message = raw
      .replace(/^JSON\.parse:\s*/, "")
      .replace(/\s*at position \d+(?:\s*\(line \d+ column \d+\))?/, "")
      .replace(/\s+in JSON$/, "")
      .replace(/\s*at line \d+ column \d+ of the JSON data$/, "");
    return [{ line, message: capitalize(message) }];
  }
}

// ---- xml ----

function lintXml(text: string): LintIssue[] {
  if (typeof DOMParser === "undefined") return [];
  const doc = new DOMParser().parseFromString(text, "application/xml");
  const error = doc.getElementsByTagName("parsererror")[0];
  if (!error) return [];
  const raw = error.textContent ?? "";
  // Chromium: "error on line 3 at column 5: Opening and ending tag mismatch ..."
  // Firefox: "XML Parsing Error: mismatched tag ...\nLocation: ...\nLine Number 3, Column 5:"
  let line = 1;
  let message = raw.trim();
  const chromium = /error on line (\d+) at column \d+:\s*([^\n]*)/i.exec(raw);
  const firefox = /XML Parsing Error:\s*([^\n]*)[\s\S]*?Line Number (\d+)/i.exec(raw);
  if (chromium) {
    line = Number(chromium[1]);
    message = chromium[2];
  } else if (firefox) {
    line = Number(firefox[2]);
    message = firefox[1];
  }
  return [{ line, message: capitalize(message.trim()) }];
}

// ---- html ----

const voidElements = new Set(["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr", "!doctype"]);
// elements whose end tag the html parser supplies itself, so leaving it out is not an error
const optionalEnd = new Set(["p", "li", "dt", "dd", "option", "tr", "td", "th", "thead", "tbody", "tfoot", "colgroup", "html", "head", "body", "rt", "rp", "optgroup"]);

function lintHtml(text: string): LintIssue[] {
  const issues: LintIssue[] = [];
  const stack: { name: string; line: number }[] = [];
  const re = /<!--[\s\S]*?-->|<!\[CDATA\[[\s\S]*?\]\]>|<(\/?)([a-zA-Z][\w:-]*)((?:"[^"]*"|'[^']*'|[^>"'])*)>/g;
  for (let m = re.exec(text); m; m = re.exec(text)) {
    if (!m[2]) continue; // a comment
    const closing = m[1] === "/";
    const name = m[2].toLowerCase();
    const line = lineOfOffset(text, m.index);
    if (!closing) {
      if (voidElements.has(name) || m[3].trimEnd().endsWith("/")) continue;
      stack.push({ name, line });
      // raw text elements: skip to their end tag, the inside is not markup
      if (name === "script" || name === "style") {
        const end = new RegExp(`</${name}\\s*>`, "ig");
        end.lastIndex = re.lastIndex;
        const closeMatch = end.exec(text);
        if (!closeMatch) {
          issues.push({ line, message: `<${name}> is never closed` });
          stack.pop();
          break;
        }
        stack.pop();
        re.lastIndex = closeMatch.index + closeMatch[0].length;
      }
      continue;
    }
    // a closing tag: it may close over elements whose end tag is optional
    let index = stack.length - 1;
    while (index >= 0 && stack[index].name !== name && optionalEnd.has(stack[index].name)) index--;
    if (index >= 0 && stack[index].name === name) {
      stack.length = index;
    } else if (!optionalEnd.has(name)) {
      issues.push({ line, message: `Unexpected closing tag </${name}>` + (stack.length > 0 ? `, <${stack[stack.length - 1].name}> from line ${stack[stack.length - 1].line} is still open` : "") });
      if (issues.length >= maxIssues) return issues;
    }
  }
  for (const open of stack) {
    if (optionalEnd.has(open.name)) continue;
    issues.push({ line: open.line, message: `<${open.name}> is never closed` });
    if (issues.length >= maxIssues) break;
  }
  return issues.sort((a, b) => a.line - b.line);
}

// ---- brackets, strings and comments (css, js/ts, c#) ----

const closers: Record<string, string> = { ")": "(", "]": "[", "}": "{" };

function lintBrackets(text: string, flavour: "css" | "js" | "cs"): LintIssue[] {
  const issues: LintIssue[] = [];
  const stack: { char: string; line: number }[] = [];
  const n = text.length;
  let i = 0;
  let line = 1;
  const push = (issue: LintIssue) => {
    issues.push(issue);
    return issues.length >= maxIssues;
  };
  while (i < n) {
    const c = text[i];
    const next = text[i + 1];
    if (c === "\n") {
      line++;
      i++;
      continue;
    }
    // comments
    if (c === "/" && next === "*") {
      const end = text.indexOf("*/", i + 2);
      if (end < 0) {
        push({ line, message: "Comment is never closed" });
        break;
      }
      line += countNewlines(text, i, end);
      i = end + 2;
      continue;
    }
    if (flavour !== "css" && c === "/" && next === "/") {
      while (i < n && text[i] !== "\n") i++;
      continue;
    }
    // strings
    if (flavour === "cs" && c === '"' && next === '"' && text[i + 2] === '"') {
      const end = text.indexOf('"""', i + 3);
      if (end < 0) {
        push({ line, message: "Raw string is never closed" });
        break;
      }
      line += countNewlines(text, i, end);
      i = end + 3;
      continue;
    }
    if (flavour === "cs" && (c === "@" || (c === "$" && next === "@") || (c === "@" && next === "$")) && text[i + (next === "@" || next === "$" ? 2 : 1)] === '"') {
      const start = i + (next === "@" || next === "$" ? 3 : 2);
      let j = start;
      for (;;) {
        j = text.indexOf('"', j);
        if (j < 0) break;
        if (text[j + 1] === '"') {
          j += 2;
          continue;
        }
        break;
      }
      if (j < 0) {
        push({ line, message: "Verbatim string is never closed" });
        break;
      }
      line += countNewlines(text, i, j);
      i = j + 1;
      continue;
    }
    if (c === '"' || c === "'" || (flavour === "js" && c === "`")) {
      const quote = c;
      let j = i + 1;
      let closed = false;
      while (j < n) {
        const d = text[j];
        if (d === "\\") {
          if (text[j + 1] === "\n") line++;
          j += 2;
          continue;
        }
        if (d === quote) {
          closed = true;
          break;
        }
        if (d === "\n") {
          if (quote === "`") line++;
          else break;
        }
        j++;
      }
      if (!closed) {
        if (push({ line, message: quote === "`" ? "Template string is never closed" : "String is never closed" })) return issues;
        if (quote === "`") return issues;
        i = j; // the newline that ended it
        continue;
      }
      i = j + 1;
      continue;
    }
    // a regex literal in javascript: skipped so the quotes and brackets inside it do not count
    if (flavour === "js" && c === "/" && next !== "/" && next !== "*" && regexCanStart(text, i)) {
      let j = i + 1;
      let inClass = false;
      while (j < n && text[j] !== "\n") {
        const d = text[j];
        if (d === "\\") j += 2;
        else if (d === "[") {
          inClass = true;
          j++;
        } else if (d === "]") {
          inClass = false;
          j++;
        } else if (d === "/" && !inClass) break;
        else j++;
      }
      if (j < n && text[j] === "/") {
        i = j + 1;
        while (i < n && /[dgimsuvy]/.test(text[i])) i++;
        continue;
      }
      // no closing slash on the line: it was a division after all
    }
    if (c === "(" || c === "[" || c === "{") {
      stack.push({ char: c, line });
    } else if (c === ")" || c === "]" || c === "}") {
      const open = stack.pop();
      if (!open) {
        if (push({ line, message: `Unexpected "${c}", nothing is open` })) return issues;
      } else if (open.char !== closers[c]) {
        if (push({ line, message: `"${c}" does not match "${open.char}" from line ${open.line}` })) return issues;
        // resync: the matching opener is probably further down the stack
        while (stack.length > 0 && stack[stack.length - 1].char !== closers[c]) stack.pop();
        stack.pop();
      }
    }
    i++;
  }
  for (const open of stack) {
    if (push({ line: open.line, message: `"${open.char}" is never closed` })) break;
  }
  return issues.sort((a, b) => a.line - b.line);
}

// what precedes a "/" that starts a regex rather than dividing: an operator, a bracket, a keyword
// or the start of a line
function regexCanStart(text: string, i: number): boolean {
  let j = i - 1;
  while (j >= 0 && (text[j] === " " || text[j] === "\t")) j--;
  if (j < 0) return true;
  const c = text[j];
  if ("(,=:[!&|?{};\n".includes(c)) return true;
  const word = /(\breturn|\btypeof|\bcase|\bin|\bof)$/.exec(text.slice(Math.max(0, j - 6), j + 1));
  return word !== null;
}

function countNewlines(text: string, from: number, to: number): number {
  let count = 0;
  for (let i = from; i < to; i++) if (text.charCodeAt(i) === 10) count++;
  return count;
}

function capitalize(s: string): string {
  return s.length > 0 ? s[0].toUpperCase() + s.slice(1) : s;
}
