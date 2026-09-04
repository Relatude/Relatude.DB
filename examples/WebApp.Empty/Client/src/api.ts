// Same-origin calls: Vite proxies /api to the Backend in development,
// and in production the Backend serves this client from wwwroot.
export interface Hello {
  message: string;
  serverTimeUtc: string;
}

export async function getHello(): Promise<Hello> {
  const res = await fetch('/api/hello');
  if (!res.ok) throw new Error(`GET /api/hello failed: ${res.status}`);
  return (await res.json()) as Hello;
}
