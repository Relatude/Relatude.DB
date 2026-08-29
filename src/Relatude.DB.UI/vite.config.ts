import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  base: "./",
  server: {
    port: 5173,
    // during development the admin API is served by a .NET host (e.g. examples/Website.Simple)
    proxy: {
      "/relatude.db": "http://localhost:5052",
    },
  },
  build: {
    // the build output is embedded in the Relatude.DB.NodeServer assembly (see the csproj)
    // and served at {ApiUrlRoot}2; fixed file names, the server adds hash-based cache busting
    outDir: "../Relatude.DB.NodeServer/NodeServer/ClientUI2",
    emptyOutDir: true,
    rollupOptions: {
      output: {
        entryFileNames: "index.js",
        chunkFileNames: "[name].js",
        assetFileNames: "index[extname]",
      },
    },
  },
});
