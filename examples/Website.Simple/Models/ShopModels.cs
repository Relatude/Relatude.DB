using Relatude.DB.Common;
using Relatude.DB.Nodes;

namespace Website.Simple.Models;

// Datamodel for the facet search example (see wwwroot/search.html and the /shop/search endpoint).
// The namespace is registered as a datamodel source in relatude.db.json, so these classes are
// loaded from the entry assembly when the store opens.

[Node(TextIndex = BoolValue.True)]
public class Product {
    [InternalIdProperty]
    public int Id { get; set; }
    [StringProperty(Indexed = true)]
    public string Name { get; set; } = "";
    public string Description { get; set; } = ""; // part of the free text index, used by WhereSearch
    [StringProperty(Indexed = true, ExcludeFromTextIndex = true)]
    public string Category { get; set; } = "";
    [DoubleProperty(Indexed = true)]
    public double Price { get; set; }
    [BooleanProperty(Indexed = true)]
    public bool InStock { get; set; }
    [StringArrayProperty(Indexed = true, ExcludeFromTextIndex = true)]
    public string[] Tags { get; set; } = [];
    [ReferenceProperty(Indexed = true)]
    public Reference<Brand> Brand { get; set; } = new();
}

[Node]
public class Brand {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(Indexed = true, DisplayName = true)] // shown as the facet value display name
    public string Name { get; set; } = "";
}
