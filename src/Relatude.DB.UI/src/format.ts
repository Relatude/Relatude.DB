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
