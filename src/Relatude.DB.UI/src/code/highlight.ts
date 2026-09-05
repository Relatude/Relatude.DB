import type { Language } from "./language";

// Syntax colouring for the code editor: regex tokenizers, one rule list per language, tried in
// order at every position with sticky regexes. The output is HTML holding exactly the input's
// characters (escaped), so it can sit under a transparent textarea and line up with it. Good
// enough to read code by - it does not try to be a parser, and a construct it does not know is
// simply left uncoloured.

export function escapeHtml(text: string): string {
  return text.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;");
}

interface Rule {
  re: RegExp; // sticky ("y"), so it only matches at the current position
  cls: string | null; // the token class, or null for text left as it is
  render?: (match: string) => string; // colours the inside of a compound match (an html tag)
}

function rule(re: RegExp, cls: string | null, render?: (match: string) => string): Rule {
  return { re, cls, render };
}

const whitespace = rule(/\s+/y, null);
const identifier = rule(/[A-Za-z_$][\w$]*/y, null);

const jsonRules: Rule[] = [
  rule(/\/\/[^\n]*|\/\*[\s\S]*?\*\//y, "c"),
  rule(/"(?:[^"\\\n]|\\.)*"(?=\s*:)/y, "p"),
  rule(/"(?:[^"\\\n]|\\.)*"/y, "s"),
  rule(/-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b/y, "n"),
  rule(/\b(?:true|false|null)\b/y, "v"),
  rule(/[{}[\],:]/y, "o"),
  identifier,
  whitespace,
];

const cssRules: Rule[] = [
  rule(/\/\*[\s\S]*?\*\//y, "c"),
  rule(/"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*'/y, "s"),
  rule(/@[\w-]+/y, "k"),
  rule(/!important\b/y, "k"),
  rule(/(?<=[{;]\s*|^\s*)[a-zA-Z-]+(?=\s*:)/my, "p"),
  rule(/#[0-9a-fA-F]{3,8}\b/y, "n"),
  rule(/-?\d*\.?\d+(?:%|[a-zA-Z]+)?/y, "n"),
  rule(/[.#][\w-]+/y, "t"),
  rule(/::?[\w-]+(?:\([^)]*\))?/y, "a"),
  rule(/[A-Za-z_-][\w-]*(?=\s*\()/y, "f"),
  rule(/[{}();:,>+~*=[\]]/y, "o"),
  rule(/[A-Za-z_-][\w-]*/y, null),
  whitespace,
];

const jsKeywords =
  "abstract|any|as|async|await|boolean|break|case|catch|class|const|constructor|continue|debugger|declare|default|delete|do|else|enum|export|extends|finally|for|from|function|get|if|implements|import|in|infer|instanceof|interface|is|keyof|let|module|namespace|never|new|number|object|of|override|package|private|protected|public|readonly|return|satisfies|set|static|string|super|switch|symbol|this|throw|try|type|typeof|unknown|var|void|while|with|yield";

const jsRules: Rule[] = [
  rule(/\/\/[^\n]*|\/\*[\s\S]*?\*\//y, "c"),
  rule(/"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*'|`(?:[^`\\]|\\[\s\S])*`/y, "s"),
  // a regex literal, only where an expression can start; elsewhere "/" is division
  rule(/(?<=[(,=:[!&|?{};]\s*|^\s*|\b(?:return|typeof|case)\s+)\/(?![*/])(?:\[(?:[^\]\\\n]|\\.)*\]|[^/\\\n[]|\\.)+\/[dgimsuvy]*/my, "s"),
  rule(/\b(?:0[xX][\da-fA-F_]+|0[bB][01_]+|0[oO][0-7_]+|\d[\d_]*(?:\.\d[\d_]*)?(?:[eE][+-]?\d+)?n?)\b/y, "n"),
  rule(/\b(?:true|false|null|undefined|NaN|Infinity)\b/y, "v"),
  rule(new RegExp(`\\b(?:${jsKeywords})\\b`, "y"), "k"),
  rule(/[A-Za-z_$][\w$]*(?=\s*\()/y, "f"),
  identifier,
  rule(/[{}()[\];,.<>=!+\-*/%&|^~?:@#]+/y, "o"),
  whitespace,
];

const csKeywords =
  "abstract|as|base|bool|break|byte|case|catch|char|checked|class|const|continue|decimal|default|delegate|do|double|else|enum|event|explicit|extern|finally|fixed|float|for|foreach|goto|if|implicit|in|int|interface|internal|is|lock|long|namespace|new|object|operator|out|override|params|private|protected|public|readonly|record|ref|return|sbyte|sealed|short|sizeof|stackalloc|static|string|struct|switch|this|throw|try|typeof|uint|ulong|unchecked|unsafe|ushort|using|var|virtual|void|volatile|while|async|await|yield|get|set|init|value|when|where|with|nameof|partial|dynamic|global|required|file|scoped|notnull|unmanaged";

const csRules: Rule[] = [
  rule(/\/\/[^\n]*|\/\*[\s\S]*?\*\//y, "c"),
  rule(/"""[\s\S]*?"""|\$?@"(?:[^"]|"")*"|@?\$"(?:[^"\\\n]|\\.)*"|"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*'/y, "s"),
  rule(/\b(?:0[xX][\da-fA-F_]+|0[bB][01_]+|\d[\d_]*(?:\.\d[\d_]*)?(?:[eE][+-]?\d+)?[fFdDmMuUlL]{0,2})\b/y, "n"),
  rule(/\b(?:true|false|null)\b/y, "v"),
  rule(/#\s*(?:if|else|elif|endif|region|endregion|define|undef|nullable|pragma|warning|error|line)\b[^\n]*/y, "c"),
  rule(new RegExp(`\\b(?:${csKeywords})\\b`, "y"), "k"),
  rule(/[A-Za-z_][\w]*(?=\s*[(<])/y, "f"),
  rule(/[A-Za-z_][\w]*/y, null),
  rule(/[{}()[\];,.<>=!+\-*/%&|^~?:]+/y, "o"),
  whitespace,
];

// the inside of a tag: name, attributes, their values
const attribute = /([^\s=>/]+)(\s*=\s*)?("[^"]*"|'[^']*'|[^\s"'>]+)?/g;
function renderTag(tag: string): string {
  const head = /^<\/?[A-Za-z][\w:.-]*/.exec(tag);
  if (!head) return escapeHtml(tag);
  let out = `<span class="tok-t">${escapeHtml(head[0])}</span>`;
  const tail = /\s*\/?>$/.exec(tag);
  const inner = tag.slice(head[0].length, tail ? tag.length - tail[0].length : tag.length);
  let last = 0;
  attribute.lastIndex = 0;
  for (let m = attribute.exec(inner); m; m = attribute.exec(inner)) {
    if (m[0].length === 0) {
      attribute.lastIndex++;
      continue;
    }
    out += escapeHtml(inner.slice(last, m.index));
    out += `<span class="tok-a">${escapeHtml(m[1])}</span>`;
    if (m[2]) out += `<span class="tok-o">${escapeHtml(m[2])}</span>`;
    if (m[3]) out += `<span class="tok-s">${escapeHtml(m[3])}</span>`;
    last = m.index + m[0].length;
  }
  out += escapeHtml(inner.slice(last));
  if (tail) out += `<span class="tok-t">${escapeHtml(tail[0])}</span>`;
  return out;
}

const markupRules: Rule[] = [
  rule(/<!--[\s\S]*?-->/y, "c"),
  rule(/<!\[CDATA\[[\s\S]*?\]\]>|<!DOCTYPE[^>]*>|<\?[\s\S]*?\?>/iy, "c"),
  rule(/<\/?[A-Za-z][\w:.-]*(?:\s+[^\s=>/]+(?:\s*=\s*(?:"[^"]*"|'[^']*'|[^\s"'>]+))?)*\s*\/?>/y, "t", renderTag),
  rule(/&[#\w]+;/y, "e"),
  rule(/[^<&]+/y, null),
];

const markdownRules: Rule[] = [
  rule(/^```[\s\S]*?^```[^\n]*/my, "s"),
  rule(/^#{1,6}[^\n]*/my, "h"),
  rule(/^>[^\n]*/my, "c"),
  rule(/^\s*(?:[-*+]|\d+\.)\s/my, "o"),
  rule(/`[^`\n]+`/y, "s"),
  rule(/!?\[[^\]\n]*\]\([^)\n]*\)/y, "a"),
  rule(/(\*\*|__)(?:(?!\1)[^\n])+\1/y, "k"),
  rule(/[*_][^\n*_]+[*_]/y, "v"),
  rule(/[A-Za-z0-9 ,.;:'"!?()]+/y, null),
  whitespace,
];

function rulesFor(language: Language): Rule[] | null {
  switch (language) {
    case "json":
      return jsonRules;
    case "css":
      return cssRules;
    case "javascript":
    case "typescript":
      return jsRules;
    case "csharp":
      return csRules;
    case "xml":
    case "html":
      return markupRules;
    case "markdown":
      return markdownRules;
    default:
      return null;
  }
}

/** The text as HTML, tokens wrapped in spans with tok-* classes; plain text is only escaped. */
export function highlight(text: string, language: Language): string {
  const rules = rulesFor(language);
  if (!rules) return escapeHtml(text);
  let out = "";
  let i = 0;
  const n = text.length;
  while (i < n) {
    let matched = false;
    for (const r of rules) {
      r.re.lastIndex = i;
      const m = r.re.exec(text);
      if (!m || m[0].length === 0) continue;
      const token = m[0];
      if (r.render) out += r.render(token);
      else if (r.cls) out += `<span class="tok-${r.cls}">${escapeHtml(token)}</span>`;
      else out += escapeHtml(token);
      i += token.length;
      matched = true;
      break;
    }
    if (!matched) {
      out += escapeHtml(text[i]);
      i++;
    }
  }
  return out;
}
