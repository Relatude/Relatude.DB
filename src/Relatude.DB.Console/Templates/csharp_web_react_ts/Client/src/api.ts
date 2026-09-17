// Same-origin calls: Vite proxies /api to the Backend in development, and in production the
// Backend serves this client from its wwwroot. Add one function per endpoint here.
export interface Hello {
  message: string;
  utc: string;
  nodesInDatabase: number;
}

export async function getHello(): Promise<Hello> {
  const res = await fetch('/api/hello');
  if (!res.ok) throw new Error(`GET /api/hello failed: ${res.status}`);
  return (await res.json()) as Hello;
}
