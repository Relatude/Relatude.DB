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
    [EnumArrayProperty(Indexed = true)]
    public Size[] Sizes { get; set; } = []; // facet buckets carry the int values, displayed with the enum names
    [RelationProperty<ProductColors>(Facet = true)] // faceting is opt-in for relation properties
    public IEnumerable<Color>? Colors { get; set; }
}

[Node]
public class Brand {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(Indexed = true, DisplayName = true)] // shown as the facet value display name
    public string Name { get; set; } = "";
}

[Node]
public class Color {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(Indexed = true, DisplayName = true)] // shown as the facet value display name
    public string Name { get; set; } = "";
}

// A product comes in one or more colors and a color is shared by many products. Navigation
// property pairs alone are only ever inferred as one-to-many, so the many-to-many relation
// is declared explicitly and referenced from the property attribute above.
public class ProductColors : ManyToMany<Product, Color> { }

// Enums are skipped by the datamodel namespace scan; the legal values and names are captured
// as metadata on the EnumArrayProperty when the Product model is built.
public enum Size { XS, S, M, L, XL }

