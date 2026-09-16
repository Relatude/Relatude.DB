# Models

Node classes for WebApp.Empty go in this folder, in the namespace `WebAppEmpty.Models`.
`relatude.db.json` names that namespace as a datamodel source, so every class here is a node type
the next time the Backend starts. No registration code, no migrations.

```csharp
using Relatude.DB.Common;
using Relatude.DB.Nodes;

namespace WebAppEmpty.Models;

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

- `Indexed = true` makes a property filterable, sortable and facetable.
- Scalars, strings, arrays, `DateTime`, `Guid`, enums, `GeoCoordinate`, `FileValue` (uploaded
  files), `Reference<T>` and `References<T>` (links to other nodes) and `Embedded<T>` (owned
  sub-objects) are all plain properties.
- A relation that must be navigable from both sides is a class deriving from `OneToMany<,>`,
  `ManyToMany<,>`, `OneToOne<,>`, `OneOne<>` or `ManyMany<>`.
- Renaming a type or property loses the data under the old name unless its id is pinned with an
  attribute. `relatude validate` (from the Backend folder) warns about that and other model problems.

Reference: <https://db.relatude.com>
