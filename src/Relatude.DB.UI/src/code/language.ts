// What a file is, told from its name: which viewer shows it and, for text, which highlighter and
// linter apply. Every list here is a plain lookup - nothing is sniffed from the content, except
// that the viewer refuses to edit text that turns out to hold NUL bytes.

export type Language = "json" | "xml" | "html" | "css" | "javascript" | "typescript" | "csharp" | "markdown" | "plain";

export type FileKind = "image" | "video" | "audio" | "pdf" | "text" | "other";

const imageExtensions = new Set(["png", "jpg", "jpeg", "gif", "webp", "svg", "bmp", "ico", "avif"]);
const videoExtensions = new Set(["mp4", "webm", "m4v", "ogv", "mov"]);
const audioExtensions = new Set(["mp3", "wav", "ogg", "oga", "m4a", "flac", "aac", "opus"]);

const languageByExtension: Record<string, Language> = {
  json: "json",
  jsonc: "json",
  map: "json",
  xml: "xml",
  csproj: "xml",
  props: "xml",
  targets: "xml",
  slnx: "xml",
  svg: "xml",
  xaml: "xml",
  config: "xml",
  resx: "xml",
  nuspec: "xml",
  xsd: "xml",
  xslt: "xml",
  html: "html",
  htm: "html",
  cshtml: "html",
  razor: "html",
  css: "css",
  js: "javascript",
  mjs: "javascript",
  cjs: "javascript",
  jsx: "javascript",
  ts: "typescript",
  tsx: "typescript",
  cs: "csharp",
  md: "markdown",
  markdown: "markdown",
  txt: "plain",
  log: "plain",
  csv: "plain",
  tsv: "plain",
  yml: "plain",
  yaml: "plain",
  toml: "plain",
  ini: "plain",
  env: "plain",
  gitignore: "plain",
  gitattributes: "plain",
  editorconfig: "plain",
  sql: "plain",
  ps1: "plain",
  sh: "plain",
  bat: "plain",
  cmd: "plain",
  sln: "plain",
  user: "xml",
  lock: "plain",
  license: "plain",
  dockerfile: "plain",
};

// a few well known extension-less names that are text
const textNames = new Set(["dockerfile", "license", "readme", "makefile", "cname", "procfile"]);

const typeNames: Record<string, string> = {
  json: "JSON",
  xml: "XML",
  html: "HTML",
  css: "CSS",
  javascript: "JavaScript",
  typescript: "TypeScript",
  csharp: "C#",
  markdown: "Markdown",
};

/** The lower case extension without the dot, or "" for a name without one. */
export function extensionOf(name: string): string {
  const i = name.lastIndexOf(".");
  return i < 0 ? "" : name.slice(i + 1).toLowerCase();
}

export function fileKind(name: string): FileKind {
  const ext = extensionOf(name);
  if (imageExtensions.has(ext)) return "image";
  if (videoExtensions.has(ext)) return "video";
  if (audioExtensions.has(ext)) return "audio";
  if (ext === "pdf") return "pdf";
  if (ext in languageByExtension) return "text";
  if (ext === "" && textNames.has(name.toLowerCase())) return "text";
  return "other";
}

export function languageOf(name: string): Language {
  return languageByExtension[extensionOf(name)] ?? "plain";
}

/** The languages the editor lints and formats; everything else is edited as plain text. */
export function canEdit(name: string): boolean {
  return fileKind(name) === "text";
}

/** A short type label for a listing, for folders that carry no database descriptions. */
export function displayType(name: string): string {
  const ext = extensionOf(name);
  const kind = fileKind(name);
  if (kind === "image") return `${ext.toUpperCase()} image`;
  if (kind === "video") return `${ext.toUpperCase()} video`;
  if (kind === "audio") return `${ext.toUpperCase()} audio`;
  if (kind === "pdf") return "PDF";
  if (kind === "text") {
    const language = languageOf(name);
    return typeNames[language] ?? (ext ? `${ext.toUpperCase()} text` : "Text");
  }
  return ext ? `${ext.toUpperCase()} file` : "File";
}
