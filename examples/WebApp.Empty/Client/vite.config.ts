import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The client is served by the Backend in production, so it builds straight
// into the server's wwwroot. In development Vite serves it on :5173 and
// proxies /api to the running Backend.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'https://localhost:7238',
        changeOrigin: true,
        secure: false, // ASP.NET Core dev certificate
      },
    },
  },
  build: {
    outDir: '../Server/Backend/wwwroot',
    emptyOutDir: true,
  },
});
