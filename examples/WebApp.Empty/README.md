# WebApp.Empty

A minimal SPA example: an ASP.NET Core + Relatude.DB API server and a
React/TypeScript/Vite client.

```
Client/            React + TypeScript + Vite
Server/Backend/    ASP.NET Core minimal API with Relatude.DB
```

## How they connect

The client only makes same-origin calls to `/api/...`.

- **Development** — Vite serves the client on <http://localhost:5173> and proxies
  `/api` to the Backend on <https://localhost:7238> (`vite.config.ts`). No CORS needed.
- **Production** — `npm run build` writes the client into `Server/Backend/wwwroot`,
  and the Backend serves it (`UseStaticFiles` + `MapFallbackToFile("index.html")`).
  `dotnet publish` runs the client build automatically (`BuildClient` target in
  `Backend.csproj`).

The one endpoint is `GET /api/hello` in `Server/Backend/Program.cs`.

## Running it

Terminal 1 — server:

```bash
dotnet run --project Server/Backend --launch-profile https
```

Terminal 2 — client:

```bash
npm install --prefix Client && npm run dev --prefix Client
```

Then open <http://localhost:5173>.

To run everything from the server alone, build the client first
(`npm run build --prefix Client`) and open <https://localhost:7238>.

> `Server/Backend/wwwroot` is generated and git-ignored; it only exists after a
> client build.
