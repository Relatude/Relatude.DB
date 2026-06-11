# Relatude.DB Content Studio

A general purpose, CMS-like editing environment for any content stored in a Relatude.DB database.
It consists of two projects:

| Project | What it is |
| --- | --- |
| `Relatude.DB.ContentApi` | ASP.NET Core minimal web API exposing the datamodel, generic node CRUD, relation editing and full text search over a `NodeStore`. Also serves the built SPA from `wwwroot`. |
| `Relatude.DB.ContentUI` | TypeScript SPA (no framework, built with Vite). Blade-based UI: blades open sideways as you navigate relational properties, like the Azure portal / a CMS content tree. |

## Run it

```bash
# 1. build the UI into ContentApi/wwwroot (only needed after UI changes)
cd src/Relatude.DB.ContentUI
npm install
npm run build

# 2. run the API + UI
dotnet run --project src/Relatude.DB.ContentApi
# then open http://localhost:5230
```

For UI development with hot reload, run the API as above and `npm run dev` in
`Relatude.DB.ContentUI` (the Vite dev server on port 5231 proxies `/api` to port 5230).

The database files live in `src/Relatude.DB.ContentApi/data`. On first start the store is
seeded with a small CMS-like demo model (`Article`, `Author`, `Category`, `Tag`).
Delete the `data` folder to reset and reseed.

## The API is generic

The UI is driven entirely by `GET /api/model` - the runtime datamodel. To manage your own
content types, register your classes in `Program.cs` (`dataModel.Add<YourType>()`) instead of
the demo types; no UI changes are required. Endpoints:

| Endpoint | Purpose |
| --- | --- |
| `GET /api/model` | Node types, properties, constraints and relation metadata |
| `GET /api/types/{typeId}/nodes?search=&page=&pageSize=` | Paged, text-filtered node list per type |
| `GET /api/nodes/{id}` | Full node: values + relation values (with display names) |
| `POST /api/nodes` | Create node from `{ typeId, values }` |
| `PUT /api/nodes/{id}` | Update value properties |
| `DELETE /api/nodes/{id}` | Delete node |
| `PUT /api/nodes/{id}/relations/{propertyId}` | Replace a relation's targets `{ ids: [...] }` |
| `GET /api/search?q=&take=` | Ranked global full text search with highlighted samples |

Notes for model authors:

- Mark a property with `[StringProperty(DisplayName = true)]` to control list/chip captions.
- Plain navigation property pairs (`Author? Author` / `Article[] Articles`) are inferred as
  one-to-many relations. Many-to-many relations must be declared explicitly with a relation
  class (see `ArticleTags` in `Demo/CmsModels.cs`) and registered with `dataModel.Add<ArticleTags>()`.
- There is no authentication; this is a development/admin tool. Put it behind your own auth
  before exposing it.
