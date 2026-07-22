using Relatude.DB.Nodes;
using Website.Simple.Models;

// NB: deliberately NOT in the Website.Simple.Models namespace: that namespace is registered as a
// datamodel source in relatude.db.json, and every class in it is treated as a node type.
namespace Website.Simple.Data;

// Seeds the store with a deterministic product catalog on first run (10 000 products, 12 brands).
// Names and descriptions are combined from word banks so free text search has meaningful words
// to match: try searching for "waterproof", "leather", "wireless", "bamboo", "titanium"...
public static class ShopSeeder {

    record CategoryDef(string Name, string[] Nouns, string[] Uses);
    static readonly CategoryDef[] _categories = [
        new("Furniture", ["Chair", "Table", "Desk", "Shelf", "Sofa", "Bench"], ["living room", "home office", "reading corner", "hallway"]),
        new("Electronics", ["Headphones", "Speaker", "Keyboard", "Monitor", "Camera", "Charger"], ["travel", "gaming", "video calls", "music production"]),
        new("Outdoor", ["Tent", "Backpack", "Lantern", "Hammock", "Thermos", "Boots"], ["hiking", "camping", "fishing trips", "mountain weather"]),
        new("Kitchen", ["Kettle", "Knife", "Pan", "Grinder", "Blender", "Cutting Board"], ["daily cooking", "baking", "meal prep", "espresso lovers"]),
        new("Clothing", ["Jacket", "Sweater", "Gloves", "Scarf", "Cap", "Vest"], ["cold winter days", "commuting", "layering", "rainy weather"]),
        new("Toys", ["Puzzle", "Robot", "Building Kit", "Board Game", "Kite", "Race Car"], ["family evenings", "curious kids", "rainy days", "collectors"]),
    ];
    static readonly string[] _adjectives = ["Compact", "Classic", "Foldable", "Ergonomic", "Portable", "Sturdy", "Elegant", "Rustic", "Modern", "Silent", "Adjustable", "Ultralight"];
    static readonly string[] _materials = ["oak", "leather", "bamboo", "titanium", "wool", "canvas", "steel", "walnut", "aluminium", "cork", "linen", "recycled plastic"];
    static readonly string[] _features = ["waterproof", "wireless", "stackable", "dishwasher safe", "handmade", "foldable", "rechargeable", "machine washable", "scratch resistant", "weatherproof"];
    static readonly string[] _tags = ["bestseller", "eco", "new", "sale", "premium", "handmade", "limited"];
    static readonly string[] _brandNames = ["Fjellrev", "Nordlys", "Kvist & Co", "Bluewhale", "Habitat 7", "Solvind", "Granheim", "Urban Nest", "Polarix", "Drivved", "Lysne", "Vandrer"];

    public static void SeedIfEmpty(NodeStore db, int productCount = 10_000) {
        if (db.Query<Product>().Count() > 0) return;
        var rnd = new Random(2026); // deterministic content
        var brands = _brandNames.Select(n => new Brand { Id = Guid.NewGuid(), Name = n }).ToList();
        db.Insert(brands);
        var batch = new List<Product>(1000);
        for (var i = 0; i < productCount; i++) {
            var cat = _categories[rnd.Next(_categories.Length)];
            var adjective = _adjectives[rnd.Next(_adjectives.Length)];
            var material = _materials[rnd.Next(_materials.Length)];
            var feature = _features[rnd.Next(_features.Length)];
            var feature2 = _features[rnd.Next(_features.Length)];
            var noun = cat.Nouns[rnd.Next(cat.Nouns.Length)];
            var use = cat.Uses[rnd.Next(cat.Uses.Length)];
            var brand = brands[rnd.Next(brands.Count)];
            var product = new Product {
                Name = $"{adjective} {material} {noun}".Replace(material, upperFirst(material)),
                Description = $"A {adjective.ToLower()} {noun.ToLower()} in {material}, {feature} and {feature2}. Made by {brand.Name}, perfect for {use}.",
                Category = cat.Name,
                Price = Math.Round(9 + Math.Pow(rnd.NextDouble(), 2) * 1990, 2), // skewed towards lower prices so range buckets differ in count
                InStock = rnd.Next(5) > 0,
                Tags = Enumerable.Range(0, rnd.Next(3)).Select(_ => _tags[rnd.Next(_tags.Length)]).Distinct().ToArray(),
                Brand = new() { Id = brand.Id },
            };
            batch.Add(product);
            if (batch.Count == 1000) { db.Insert(batch); batch.Clear(); }
        }
        if (batch.Count > 0) db.Insert(batch);
    }
    static string upperFirst(string s) => char.ToUpper(s[0]) + s[1..];
}
