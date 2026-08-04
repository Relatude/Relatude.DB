# Pitfalls, gotchas and where to look in the source

Read this before finishing any non-trivial Relatude.DB answer. These are the things that actually bite people — most produce silently wrong behaviour rather than an error, which is why they are worth checking against deliberately.

## Modelling

- **`Reference`/`References` `foreach` yields nothing unless preloaded.** Use `.Get()` / `.TryGet(out …)` for lazy access, or `.Preload(...)` in the query. This is deliberate — it makes accidental N+1 loads impossible.
- **Never construct a `NodeMeta`.** Default it to `NodeMeta.Empty` and treat it as read-only.
- **Prefer interfaces.** No boilerplate, no parameterless-constructor rule, no initialisation to forget — and multiple inheritance, which classes cannot give you.
- **Put property attributes on the interface, not on the class implementing it.** A property is defined once, on the type that first declares it; attributes anywhere else are ignored.
- **Two parent interfaces may not declare the same property name.** Keep facet interfaces disjoint, or hoist the shared member into a common base interface.
- **Property overriding is not supported.** You cannot redeclare a property further down the hierarchy to change its attributes.
- **Nullable value types are not supported.** No `int?`, `DateTime?`, `GeoCoordinate?`. Use the type's empty/default value — that is exactly what `GeoCoordinate.Empty` and `FileValue.Empty` are for.
- **A parameterless constructor is mandatory** on every non-interface, non-`[Exclude]` node type.
- **Initialise every reference type on concrete classes** — `string.Empty`, `FileValue.Empty`, `NodeMeta.Empty`, `[]`, `new()`. On interfaces these are getter-only; the proxy handles it.
- **Relations are not foreign keys.** No `VenueId` property. Declare the relation class and expose the nested property types.
- **Relation lists are ordered, per side.** `Relate` appends to the bottom; relating an already-related pair **throws**, as does unrelating a pair that is not related. Change position with the `MoveRelation…` / `SetRelationOrder` methods, never by un-relating and re-relating. In a many-to-many relation each side is ordered independently.
- **Ordering is not a reason to choose `References<T>` over a relation.** Relations preserve order too, and add reorder operations. Duplicates and the absence of a reverse index are what distinguish references.
- **`SetRelationOrder` reorders only.** The ids you pass must be exactly the currently related ids — it will not add or remove.
- **Embedded objects are owned.** They live and die with the parent. To share a sub-object, promote it to a node type with a relation.
- **`[EmbeddedMapProperty(KeyProperty = …)]` requires the named property to exist on the value type** and to match the map's key type.
- **You cannot read an embedded map by key until the parent is persisted.** Before insert, only `Add` is safe.
- **Pin `[Node(Id = …)]` and `[Relation(Id = …)]` early.** Without them the id is a hash of the full type name, so a rename or namespace move creates a new, empty type.

## Geo

- **`GeoCoordinate.Empty` never matches `IsWithin`** and every distance to it is infinite.
- **Never `OrderBy` a `GeoCoordinate`.** Z-order is not proximity. Filter by radius, then sort by `DistanceTo` in memory on the materialised page.
- **Coordinates snap to a ~1 cm grid on construction**, so a value you store and read back is equal — but not bit-identical to arbitrary-precision input.

## Querying

- **Filter, sort and facet only on `Indexed = true` properties.** Everything else scans.
- **Prefer builder methods over LINQ on the result set.** The result set is already materialised, so LINQ on it runs in your process.
- **Use `Page(p, n)`, not `Skip().Take()`**, so you get `TotalCount` for free.
- **Use `Count()` on the builder, not `Execute().Count()`.**
- **Use `SelectId()` when you only need ids.**
- **`Query<T>()` includes subtypes by default.** That is the feature that makes facet-interface queries work — and the thing to remember to narrow with `WhereTypes` when you want one type only.
- **`Include` filters never shrink the main result set** — parents with zero matching children still come back.
- **`Traverse` and `ShortestPath` only work over relations**, not references or embedded data. Needing traversal is your signal to model the link as a relation.
- **A single `Where` is pushed to the indexes all or nothing.** If one part of the predicate cannot be answered natively, the whole predicate is evaluated row by row. Chained `Where` calls are pushed down independently, so split the unindexed part into its own `Where` and it runs on the already narrowed set.
- **Text matching in a query is ordinal.** `StartsWith`, `Contains` and `==` on a string all compare by code unit, so case matters. Passing a `StringComparison` is rejected rather than ignored — only `Ordinal` is accepted.
- **`PrefixSearch` / `InfixSearch` are not what `StartsWith` / `Contains` use.** Those two flags allow `term*` and `*term` wildcards inside a *search*; the `StartsWith` and `Contains` query methods work off the value index and need only `Indexed = true`.
- **`MatchesSearch` is the one filter with no unindexed fallback.** It needs `IndexedByWords = true` (or `IndexedBySemantic = true`) on that property and throws a message naming the fix otherwise — a search cannot be reproduced row by row. It also matches indexed *words*, so `MatchesSearch("proof")` will not find "waterproof" while `Contains("proof")` will.
- **A property's own word index is written in the transaction; the node's combined text index usually is not.** So `MatchesSearch` sees a node as soon as `Insert` returns, while `WhereSearch` can miss it until the background text-indexing queue catches up — unless the node type sets `InstantTextIndexing = true`.
- **`db.Context.Admin()` bypasses ACL filtering.** Trusted server code only — never hand it to a request handler.

## Operational

- **Take a backup from the admin UI before upgrading.** The project is pre-1.0.
- **`UseRelatudeDB()` goes after your own `UseCors` / `UseHttpsRedirection` / `UseAuthentication`.**
- **The first-boot admin credentials are printed to the application log.**
- **File conversion is asynchronous.** Check `IsFileReady(...)` rather than assuming a generated URL is immediately serveable.

## Where to look in the source

The public documentation is thin and the API is pre-1.0. **When something disagrees with the installed build, the source wins** — it is small and well commented. Point users here rather than guessing.

| Topic | Source path |
|---|---|
| Attributes | `src/Relatude.DB.NodeStore/Nodes/Attributes.cs` |
| How types, inheritance and properties are built | `src/Relatude.DB.NodeStore/Datamodels/BuildUtils.cs`, `BuildUtilsProperties.cs` |
| Proxy / interface generation | `src/Relatude.DB.NodeStore/CodeGeneration/InterfaceGen.cs`, `ModelGen.cs` |
| Server wiring and the admin UI | `src/Relatude.DB.NodeServer/`, `src/Relatude.DB.ServerUI/` |
| Relation bases and One/Many properties | `src/Relatude.DB.NodeStore/Nodes/Relation.cs` |
| `Reference<T>` / `References<T>` | `src/Relatude.DB.NodeStore/Nodes/Reference.cs`, `References.cs` |
| Full query surface | `src/Relatude.DB.NodeStore/Query/IQueryOfNodes.cs` |
| Facets | `src/Relatude.DB.NodeStore/Query/QueryOfFacets.cs`, `ResultSetFacets.cs` |
| Store & transactions | `src/Relatude.DB.NodeStore/Nodes/NodeStore.cs`, `Transaction.cs` |
| `GeoCoordinate` and spatial indexing | `src/Relatude.DB.Common/Common/GeoCoordinate.cs`, `GeoSpatial.cs` |
| `FileValue` | `src/Relatude.DB.Common/Common/FileValue.cs` |
| A working model | `src/Relatude.DB.NodeStore/Demo/Models/DemoArticle.cs` |

Repository: https://github.com/Relatude/Relatude.DB
