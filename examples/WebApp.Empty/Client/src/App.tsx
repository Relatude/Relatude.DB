import { useEffect, useState } from 'react';
import { getHello, type Hello } from './api';

export default function App() {
  const [hello, setHello] = useState<Hello | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    getHello()
      .then(setHello)
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)));
  }, []);

  return (
    <main style={{ fontFamily: 'system-ui, sans-serif', padding: '2rem' }}>
      <h1>WebApp.Empty</h1>
      {error && <p style={{ color: 'crimson' }}>{error}</p>}
      {!error && !hello && <p>Loading…</p>}
      {hello && (
        <p>
          {hello.message}
          <br />
          <small>
            {hello.nodesInDatabase} nodes in the database. Server time (UTC): {hello.utc}
          </small>
        </p>
      )}
      <p>
        <a href="/relatude.db">Relatude.DB admin UI</a>
      </p>
    </main>
  );
}
