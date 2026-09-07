export function formatBytes(bytes: number): string {
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${unit === 0 || value >= 100 ? Math.round(value) : value.toFixed(1)} ${units[unit]}`;
}

// 534158000 -> "6d 04:22:38"
export function formatDuration(ms: number): string {
  const total = Math.floor(ms / 1000);
  const days = Math.floor(total / 86400);
  const pad = (n: number) => String(n).padStart(2, "0");
  const clock = `${pad(Math.floor(total / 3600) % 24)}:${pad(Math.floor(total / 60) % 60)}:${pad(total % 60)}`;
  return days > 0 ? `${days}d ${clock}` : clock;
}

export function formatCount(n: number): string {
  return n.toLocaleString("en-US");
}

export function formatTime(iso: string): string {
  const date = new Date(iso);
  const today = new Date();
  const sameDay = date.toDateString() === today.toDateString();
  return sameDay ? date.toLocaleTimeString() : date.toLocaleString();
}

/**
 * A query string as lines: what is being queried, then one call per line.
 *
 * The page shows the query it sends so it can be read as an explanation of the result and pasted
 * into code, and both want it shaped like the C# it is - a single four-hundred character line
 * scrolling sideways is neither. Only the dots BETWEEN calls break: a dot inside a string
 * ("id|Name"), inside a lambda (n => n.Name) or anywhere else inside an argument list belongs to
 * what it is part of, so the split tracks quotes and bracket depth rather than splitting on ".".
 */
export function formatQuery(query: string): string {
  const parts: string[] = [];
  let start = 0;
  let depth = 0;
  let inString = false;
  for (let i = 0; i < query.length; i++) {
    const c = query[i];
    if (inString) {
      if (c === "\\") i++; // an escaped quote does not end the string
      else if (c === '"') inString = false;
      continue;
    }
    if (c === '"') inString = true;
    else if (c === "(" || c === "[") depth++;
    else if (c === ")" || c === "]") depth--;
    else if (c === "." && depth === 0 && i > start) {
      parts.push(query.slice(start, i));
      start = i;
    }
  }
  parts.push(query.slice(start));
  if (parts.length < 2) return query;
  return parts[0] + "\n" + parts.slice(1).map((p) => "  " + p).join("\n");
}
