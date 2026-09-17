import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The client is served by the Backend in production, so it builds straight into the Backend's
// wwwroot. In development Vite serves it on :5173 and proxies /api (your endpoints) and
// /relatude.db (the admin UI) to the running Backend.
const backend = {
  target: 'https://localhost:7238',
  changeOrigin: true,
  secure: false, // ASP.NET Core development certificate
};

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': backend,
      '/relatude.db': backend,
    },
  },
  build: {
    outDir: '../Backend/wwwroot',
    emptyOutDir: true,
  },
});
