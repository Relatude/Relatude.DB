import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  base: "./",
  server: {
    // PORT lets a harness or a second checkout run this dev server off 5173
    port: Number(process.env.PORT) || 5173,
    // during development the admin API is served by a .NET host (e.g. examples/Website.Simple);
    // API_URL points at it when that host is not on its default port
    proxy: {
      "/relatude.db": process.env.API_URL || "http://localhost:5052",
    },
  },
  build: {
    // the build output is embedded in the Relatude.DB.NodeServer assembly (see the csproj)
    // and served at {ApiUrlRoot}; fixed file names, the server adds hash-based cache busting
    outDir: "../Relatude.DB.NodeServer/NodeServer/ClientUI",
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
