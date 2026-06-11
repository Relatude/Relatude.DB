import { defineConfig } from "vite";

export default defineConfig({
    server: {
        port: 5231,
        proxy: {
            // during development the API runs separately on its own port
            "/api": "http://localhost:5230",
        },
    },
    build: {
        // the production bundle is served by Relatude.DB.ContentApi
        outDir: "../Relatude.DB.ContentApi/wwwroot",
        emptyOutDir: true,
    },
});
