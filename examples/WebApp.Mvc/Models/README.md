# Models

Node classes for WebApp.Mvc go in this folder, in the namespace `WebAppMvc.Models`.
`relatude.db.json` names that namespace as a datamodel source, so every class here is a node type
the next time the app starts. No registration code, no migrations.

```csharp
using Relatude.DB.Common;
using Relatude.DB.Nodes;

namespace WebAppMvc.Models;

[Node(TextIndex = BoolValue.True)]                       // the whole node is free-text searchable
public class Product {
    [PublicIdProperty] public Guid Id { get; set; }
    [StringProperty(Indexed = true)] public string Name { get; set; } = "";
    public string Description { get; set; } = "";        // no attribute needed
    [DoubleProperty(Indexed = true)] public double Price { get; set; }
    [BooleanProperty(Indexed = true)] public bool InStock { get; set; }
    [StringArrayProperty(Indexed = true)] public string[] Tags { get; set; } = [];
}
```

- **Only node types belong in this namespace.** Every class in it becomes a node type, so keep view
  models, form models and helpers elsewhere, for example in `WebAppMvc.ViewModels`.
- `Indexed = true` makes a property filterable, sortable and facetable.
- Scalars, strings, arrays, `DateTime`, `Guid`, enums, `GeoCoordinate`, `FileValue` (uploaded
  files), `Reference<T>` and `References<T>` (links to other nodes) and `Embedded<T>` (owned
  sub-objects) are all plain properties.
- A relation that must be navigable from both sides is a class deriving from `OneToMany<,>`,
  `ManyToMany<,>`, `OneToOne<,>`, `OneOne<>` or `ManyMany<>`.
- Renaming a type or property loses the data under the old name unless its id is pinned with an
  attribute. `relatude validate` (from this folder's parent) warns about that and other model problems.

Reference: <https://db.relatude.com>
