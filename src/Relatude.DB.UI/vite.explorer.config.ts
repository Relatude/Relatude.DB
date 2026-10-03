import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The explorer page a GraphQL endpoint serves on its own url (src/explorer). Built into the Relatude.DB.GraphQL
// project, which embeds the three files (see its csproj) and serves them as "?explorer-asset=" requests to the
// endpoint's url, so a code-first endpoint has the page too. Fixed file names; the server adds a version to the urls.
export default defineConfig({
  plugins: [react()],
  base: "./",
  publicDir: false,
  build: {
    chunkSizeWarningLimit: 2000,
    outDir: "../Relatude.DB.GraphQL/Explorer",
    emptyOutDir: true,
    rollupOptions: {
      input: "explorer.html",
      output: {
        entryFileNames: "explorer.js",
        chunkFileNames: "explorer-[name].js",
        assetFileNames: "explorer[extname]",
        inlineDynamicImports: true,
      },
    },
  },
});
