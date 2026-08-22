using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Nodes;
using Relatude.DB.Query;

namespace Relatude.Querying;

/// <summary>
/// Single must throw when more than one row matches. It fetches Take(2) internally, so the guard
/// is these tests: with the old Take(1) it silently behaved like First and never threw.
/// Reuses the ImmProduct model from QueryImmutabilityTests: 30 products, category round-robin
/// over Toys/Games/Tools, price = i * 10.
/// </summary>
[TestClass]
public class SingleRowHelperTests {

    static readonly string[] _categories = ["Toys", "Games", "Tools"];

    static NodeStore openStore() {
        var dm = new Datamodel();
        // ImmProduct.Brand needs the related types in the model too, even though these tests never use it
        dm.Add<ImmProduct>(autoDeduceRelations: true);
        dm.Add<ImmBrand>(autoDeduceRelations: true);
        dm.Add<ImmCountry>(autoDeduceRelations: true);
        var store = new NodeStore(DataStoreLocal.Open(dm));
        for (var i = 1; i <= 30; i++) {
            store.Insert(new ImmProduct { PId = Guid.NewGuid(), Name = "gadget " + i, Category = _categories[i % 3], Price = i * 10 });
        }
        return store;
    }

    [TestMethod]
    public void SingleReturnsTheOnlyMatch() {
        using var store = openStore();
        var product = store.Query<ImmProduct>().Where(p => p.Price == 10).Single();
        Assert.AreEqual(10, product.Price);
    }

    [TestMethod]
    public void SingleThrowsOnMultipleMatches() {
        using var store = openStore();
        var q = store.Query<ImmProduct>().Where(p => p.Category == "Toys"); // 10 matches
        Assert.ThrowsException<InvalidOperationException>(() => q.Single());
    }

    [TestMethod]
    public void SingleThrowsOnNoMatch() {
        using var store = openStore();
        var q = store.Query<ImmProduct>().Where(p => p.Price == -1);
        Assert.ThrowsException<InvalidOperationException>(() => q.Single());
    }

    [TestMethod]
    public void SingleOnProjectionThrowsOnMultipleMatches() {
        using var store = openStore();
        var many = store.Query<ImmProduct>().Where(p => p.Category == "Toys").Select(p => p.Price);
        Assert.ThrowsException<InvalidOperationException>(() => many.Single());
        var one = store.Query<ImmProduct>().Where(p => p.Price == 10).Select(p => p.Price);
        Assert.AreEqual(10, one.Single());
    }
}
