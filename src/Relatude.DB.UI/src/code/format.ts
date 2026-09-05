import type { Language } from "./language";

// Reformatting for the code editor's "Format" button, offered where it can be done reliably from
// the browser: JSON goes through parse and stringify, XML through a tag-level re-indenter. Both
// refuse text that does not parse, so a broken file is never rewritten into something else.

export type FormatResult = { text: string } | { error: string };

export function canFormat(language: Language): boolean {
  return language === "json" || language === "xml";
}

export function format(text: string, language: Language): FormatResult {
  switch (language) {
    case "json":
      return formatJson(text);
    case "xml":
      return formatXml(text);
    default:
      return { error: "Formatting is not available for this file type." };
  }
}

function formatJson(text: string): FormatResult {
  try {
    const value: unknown = JSON.parse(text);
    return { text: JSON.stringify(value, null, 2) + (text.endsWith("\n") ? "\n" : "") };
  } catch (error) {
    if (/\/\/|\/\*/.test(text)) return { error: "The JSON holds comments, which formatting would lose. It is left as it is." };
    return { error: error instanceof Error ? error.message : String(error) };
  }
}

// Every tag on its own line at its depth, except an element holding only text, which stays on one
// line (<Name>value</Name>). Comments, processing instructions and CDATA are kept as they are.
function formatXml(text: string): FormatResult {
  if (typeof DOMParser !== "undefined") {
    const doc = new DOMParser().parseFromString(text, "application/xml");
    if (doc.getElementsByTagName("parsererror").length > 0) return { error: "The XML does not parse, so it is left as it is." };
  }
  const tokens = text.match(/<!--[\s\S]*?-->|<!\[CDATA\[[\s\S]*?\]\]>|<\?[\s\S]*?\?>|<!DOCTYPE[^>]*>|<[^>]+>|[^<]+/gi) ?? [];
  const lines: string[] = [];
  let depth = 0;
  let pendingText: string | null = null; // text that may be joined with its closing tag
  const indent = (d: number) => "  ".repeat(Math.max(0, d));
  for (const token of tokens) {
    if (!token.startsWith("<")) {
      const trimmed = token.trim();
      if (trimmed.length === 0) continue;
      pendingText = trimmed;
      continue;
    }
    const isClose = token.startsWith("</");
    const isSelfClosing = token.endsWith("/>") || token.startsWith("<?") || token.startsWith("<!");
    if (isClose) {
      depth--;
      if (pendingText !== null && lines.length > 0) {
        // <a>text</a>: fold the text and the closing tag onto the opening tag's line
        lines[lines.length - 1] += pendingText + token;
        pendingText = null;
        continue;
      }
      if (pendingText !== null) lines.push(indent(depth + 1) + pendingText);
      pendingText = null;
      lines.push(indent(depth) + token);
      continue;
    }
    if (pendingText !== null) {
      lines.push(indent(depth) + pendingText);
      pendingText = null;
    }
    lines.push(indent(depth) + token.replace(/\s+/g, " ").replace(/ \/>$/, " />"));
    if (!isSelfClosing) depth++;
  }
  if (pendingText !== null) lines.push(pendingText);
  return { text: lines.join("\n") + "\n" };
}
