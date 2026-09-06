// A table on the clipboard.
//
// Two shapes at once: tab-separated text, which a text editor takes as lines and a spreadsheet as
// cells, and an HTML table, which a spreadsheet or a document prefers when it can read it. Where
// the richer clipboard is not available - an older browser, a page not served over https - the text
// alone goes.

export interface TableData {
  header: string[];
  rows: string[][];
}

export async function copyTable(table: TableData): Promise<void> {
  const cell = (s: string) => s.replace(/[\t\r\n]+/g, " ");
  const tsv = [table.header, ...table.rows].map((row) => row.map(cell).join("\t")).join("\n");
  const esc = (s: string) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  const html =
    "<table><thead><tr>" +
    table.header.map((h) => `<th>${esc(h)}</th>`).join("") +
    "</tr></thead><tbody>" +
    table.rows.map((row) => "<tr>" + row.map((c) => `<td>${esc(c)}</td>`).join("") + "</tr>").join("") +
    "</tbody></table>";
  if (typeof ClipboardItem !== "undefined" && navigator.clipboard?.write) {
    try {
      await navigator.clipboard.write([new ClipboardItem({ "text/plain": new Blob([tsv], { type: "text/plain" }), "text/html": new Blob([html], { type: "text/html" }) })]);
      return;
    } catch {
      // the browser did not take both kinds; the text alone will do
    }
  }
  await navigator.clipboard.writeText(tsv);
}
