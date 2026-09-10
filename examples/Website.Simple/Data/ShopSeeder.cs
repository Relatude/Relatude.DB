using Relatude.DB.Common;
using Relatude.DB.Nodes;
using Website.Simple.Models;

// NB: deliberately NOT in the Website.Simple.Models namespace: that namespace is registered as a
// datamodel source in relatude.db.json, and every class in it is treated as a node type.
namespace Website.Simple.Data;

// Seeds the store with a deterministic product catalog on first run (12 brands, 8 colors, and
// products related to 1-3 colors each with a run of available sizes). Names and descriptions are
// combined from word banks so free text search has meaningful words to match: try searching for
// "waterproof", "leather", "wireless", "bamboo", "titanium", "ruby red"...
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
    static readonly string[] _colorNames = ["Black", "White", "Navy", "Forest Green", "Burnt Orange", "Slate Grey", "Ruby Red", "Sand"];

    // Where the products are stocked. Weighted cities rather than a scatter over the whole globe,
    // because a map of evenly random points says nothing: what a map is for is showing that things
    // crowd, and these crowd the way stock does - a few big places, a long tail of small ones, and
    // a scattering of nothing in particular in between.
    record CityDef(string Name, double Lat, double Lon, int Weight);
    static readonly CityDef[] _cities = [
        new("Oslo", 59.9139, 10.7522, 120), new("Bergen", 60.3913, 5.3221, 45), new("Trondheim", 63.4305, 10.3951, 30),
        new("Tromso", 69.6492, 18.9553, 12), new("Stockholm", 59.3293, 18.0686, 70), new("Gothenburg", 57.7089, 11.9746, 30),
        new("Copenhagen", 55.6761, 12.5683, 60), new("Helsinki", 60.1699, 24.9384, 35), new("Reykjavik", 64.1466, -21.9426, 10),
        new("London", 51.5074, -0.1278, 130), new("Manchester", 53.4808, -2.2426, 35), new("Dublin", 53.3498, -6.2603, 25),
        new("Amsterdam", 52.3676, 4.9041, 55), new("Brussels", 50.8503, 4.3517, 30), new("Paris", 48.8566, 2.3522, 110),
        new("Lyon", 45.764, 4.8357, 25), new("Madrid", 40.4168, -3.7038, 65), new("Barcelona", 41.3874, 2.1686, 50),
        new("Lisbon", 38.7223, -9.1393, 30), new("Berlin", 52.52, 13.405, 95), new("Munich", 48.1351, 11.582, 45),
        new("Hamburg", 53.5511, 9.9937, 40), new("Zurich", 47.3769, 8.5417, 30), new("Vienna", 48.2082, 16.3738, 40),
        new("Prague", 50.0755, 14.4378, 35), new("Warsaw", 52.2297, 21.0122, 45), new("Rome", 41.9028, 12.4964, 55),
        new("Milan", 45.4642, 9.19, 45), new("Athens", 37.9838, 23.7275, 25), new("Istanbul", 41.0082, 28.9784, 70),
        new("Moscow", 55.7558, 37.6173, 60), new("Dubai", 25.2048, 55.2708, 45), new("Mumbai", 19.076, 72.8777, 80),
        new("Delhi", 28.6139, 77.209, 75), new("Bangalore", 12.9716, 77.5946, 45), new("Singapore", 1.3521, 103.8198, 55),
        new("Bangkok", 13.7563, 100.5018, 45), new("Hong Kong", 22.3193, 114.1694, 50), new("Shanghai", 31.2304, 121.4737, 85),
        new("Beijing", 39.9042, 116.4074, 70), new("Seoul", 37.5665, 126.978, 55), new("Tokyo", 35.6762, 139.6503, 100),
        new("Osaka", 34.6937, 135.5023, 40), new("Sydney", -33.8688, 151.2093, 55), new("Melbourne", -37.8136, 144.9631, 45),
        new("Auckland", -36.8485, 174.7633, 20), new("New York", 40.7128, -74.006, 140), new("Boston", 42.3601, -71.0589, 40),
        new("Chicago", 41.8781, -87.6298, 55), new("Toronto", 43.6532, -79.3832, 50), new("Vancouver", 49.2827, -123.1207, 30),
        new("San Francisco", 37.7749, -122.4194, 75), new("Los Angeles", 34.0522, -118.2437, 80), new("Seattle", 47.6062, -122.3321, 45),
        new("Denver", 39.7392, -104.9903, 25), new("Austin", 30.2672, -97.7431, 30), new("Miami", 25.7617, -80.1918, 35),
        new("Mexico City", 19.4326, -99.1332, 55), new("Bogota", 4.711, -74.0721, 25), new("Lima", -12.0464, -77.0428, 25),
        new("Sao Paulo", -23.5505, -46.6333, 60), new("Rio de Janeiro", -22.9068, -43.1729, 35), new("Buenos Aires", -34.6037, -58.3816, 40),
        new("Santiago", -33.4489, -70.6693, 25), new("Cape Town", -33.9249, 18.4241, 25), new("Johannesburg", -26.2041, 28.0473, 30),
        new("Nairobi", -1.2921, 36.8219, 20), new("Lagos", 6.5244, 3.3792, 30), new("Cairo", 30.0444, 31.2357, 35),
        new("Casablanca", 33.5731, -7.5898, 18), new("Tel Aviv", 32.0853, 34.7818, 22),
    ];
    static readonly int[] _cityLadder = buildLadder();
    static int[] buildLadder() { // running weights, so a city is picked with one binary search
        var ladder = new int[_cities.Length];
        var sum = 0;
        for (var i = 0; i < _cities.Length; i++) { sum += _cities[i].Weight; ladder[i] = sum; }
        return ladder;
    }

    /// <summary>
    /// Somewhere a product is stocked: usually near one of the cities above, sometimes anywhere at
    /// all, and now and then nowhere - a stocked-nowhere product is an empty coordinate, which is
    /// what the store keeps "no location" as and what a map counts as unplaced.
    /// </summary>
    static GeoCoordinate pickLocation(Random rnd) {
        var roll = rnd.NextDouble();
        if (roll < 0.04) return GeoCoordinate.Empty;                                    // no location at all
        if (roll < 0.12) return new GeoCoordinate(rnd.NextDouble() * 140 - 60, rnd.NextDouble() * 360 - 180); // anywhere
        var pick = rnd.Next(_cityLadder[^1]);
        var at = Array.BinarySearch(_cityLadder, pick);
        var city = _cities[at < 0 ? ~at : Math.Min(at + 1, _cities.Length - 1)];
        // a normal scatter round the city, tighter in latitude than the degree suggests further
        // north - a degree of longitude is shorter there, so the spread stays roughly circular
        var spread = 0.05 + Math.Pow(rnd.NextDouble(), 3) * 1.4;
        var angle = rnd.NextDouble() * Math.PI * 2;
        var lat = city.Lat + Math.Sin(angle) * spread;
        var lon = city.Lon + (Math.Cos(angle) * spread) / Math.Max(0.2, Math.Cos(city.Lat * Math.PI / 180));
        return new GeoCoordinate(Math.Clamp(lat, -89, 89), ((lon + 540) % 360) - 180);
    }

    public static void SeedIfEmpty(NodeStore db, int productCount = 10_000_000, int batchSize=1000) {
        if (db.Query<Product>().Count() > 0) return;
        var rnd = new Random(2026); // deterministic content
        var brands = _brandNames.Select(n => new Brand { Id = Guid.NewGuid(), Name = n }).ToList();
        db.Insert(brands);
        var colors = _colorNames.Select(n => new Color { Id = Guid.NewGuid(), Name = n }).ToList();
        db.Insert(colors);       
        var batch = new List<Product>(batchSize);
        for (var i = 0; i < productCount; i++) {
            var cat = _categories[rnd.Next(_categories.Length)];
            var adjective = _adjectives[rnd.Next(_adjectives.Length)];
            var material = _materials[rnd.Next(_materials.Length)];
            var feature = _features[rnd.Next(_features.Length)];
            var feature2 = _features[rnd.Next(_features.Length)];
            var noun = cat.Nouns[rnd.Next(cat.Nouns.Length)];
            var use = cat.Uses[rnd.Next(cat.Uses.Length)];
            var brand = brands[rnd.Next(brands.Count)];
            var productColors = pickColors(rnd, colors); // one or more colors per product
            var product = new Product {
                Name = $"{adjective} {material} {noun}".Replace(material, upperFirst(material)),
                Description = $"A {adjective.ToLower()} {noun.ToLower()} in {material}, {feature} and {feature2}. Made by {brand.Name}, perfect for {use}. Available in {string.Join(", ", productColors.Select(c => c.Name))}.",
                Category = cat.Name,
                Price = Math.Round(9 + Math.Pow(rnd.NextDouble(), 2) * 1990, 2), // skewed towards lower prices so range buckets differ in count
                InStock = rnd.Next(5) > 0,
                Tags = Enumerable.Range(0, rnd.Next(3)).Select(_ => _tags[rnd.Next(_tags.Length)]).Distinct().ToArray(),
                //Brand = new() { Id = brand.Id },
                Sizes = pickSizes(rnd),
                Location = pickLocation(rnd),
                Colors = productColors, // the color nodes already exist, so Insert only creates the relations
            };
            product.Brand.Set(brand.Id);
            batch.Add(product);
            if (batch.Count == batchSize) { db.BulkInsert(batch); batch.Clear(); }
        }
        if (batch.Count > 0) db.BulkInsert(batch);
    }
    static List<Color> pickColors(Random rnd, List<Color> all)
        => Enumerable.Range(0, 1 + rnd.Next(3)).Select(_ => all[rnd.Next(all.Count)]).Distinct().ToList();
    static Size[] pickSizes(Random rnd) { // a contiguous size run, e.g. S-L
        var all = Enum.GetValues<Size>();
        var first = rnd.Next(all.Length);
        return all[first..(first + 1 + rnd.Next(all.Length - first))];
    }
    static string upperFirst(string s) => char.ToUpper(s[0]) + s[1..];
}
