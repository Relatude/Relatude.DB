using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores;
using Relatude.DB.Nodes;

namespace Relatude.Querying;

#region test model: native relation properties over two different types, declared with the nested side classes
[Node]
public class XtBrand {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(Indexed = true)]
    public string Name { get; set; } = "";
    [StringProperty(Indexed = true)] // only on the brand, so a filter on it can only resolve against XtBrand
    public string Country { get; set; } = "";
    public XtBrandProducts.Products Products { get; set; } = new();
}
[Node]
public class XtProduct {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(Indexed = true)]
    public string Name { get; set; } = "";
    [IntegerProperty(Indexed = true)]
    public int Price { get; set; }
    public XtBrandProducts.Brand Brand { get; set; } = new();
}
public class XtBrandProducts : OneToMany<XtBrand, XtProduct> {
    public class Brand : One { }       // declared on XtProduct, relates to XtBrand
    public class Products : Many { }   // declared on XtBrand, relates to XtProduct
}
#endregion

// The shared test models are all self referential (Article.Parent/Children), so they cannot show whether
// the related type of a relation property is resolved to the right end of the relation.
[TestClass]
public class CrossTypeRelationTests {

    static Datamodel model() {
        var dm = new Datamodel();
        dm.Add<XtBrand>();
        dm.Add<XtProduct>();
        dm.Add<XtBrandProducts>();
        return dm;
    }

    // brand "Acme" with products P100/P200/P300 (Price 100/200/300), brand "Other" with product P50
    static NodeStore openStore(out XtBrand acme, out XtBrand other) {
        var store = new NodeStore(DataStoreLocal.Open(model()));
        acme = new XtBrand { Name = "Acme", Country = "NO" };
        other = new XtBrand { Name = "Other", Country = "SE" };
        store.Insert(acme);
        store.Insert(other);
        foreach (var price in new[] { 100, 200, 300 }) {
            var p = new XtProduct { Name = "P" + price, Price = price };
            store.Insert(p);
            store.AddRelation(p, x => x.Brand, acme);
        }
        var cheap = new XtProduct { Name = "P50", Price = 50 };
        store.Insert(cheap);
        store.AddRelation(cheap, x => x.Brand, other);
        return store;
    }

    [TestMethod]
    public void RelatedNodeTypeIsTheOtherEndOfTheRelation() {
        var dm = model();
        dm.EnsureInitalization();
        var brandType = dm.NodeTypesByFullName[typeof(XtBrand).FullName!];
        var productType = dm.NodeTypesByFullName[typeof(XtProduct).FullName!];

        var products = (RelationPropertyModel)brandType.AllPropertiesByName["Products"];
        var brand = (RelationPropertyModel)productType.AllPropertiesByName["Brand"];

        Assert.AreEqual(productType.Id, products.NodeTypeOfRelated, "XtBrand.Products relates to XtProduct. ");
        Assert.AreEqual(brandType.Id, brand.NodeTypeOfRelated, "XtProduct.Brand relates to XtBrand. ");
    }

    [TestMethod]
    public void TraverseAcrossTwoTypes() {
        using var store = openStore(out _, out _);
        { // many side: brand -> its products
            var r = store.Query<XtBrand>().Where(b => b.Name == "Acme").Traverse<XtProduct>(b => b.Products, maxLevel: 1).Execute();
            CollectionAssert.AreEqual(new[] { "P100", "P200", "P300" }, r.Select(p => p.Name).OrderBy(n => n).ToArray());
        }
        { // one side: product -> its brand
            var r = store.Query<XtProduct>().Where(p => p.Name == "P200").Traverse<XtBrand>(p => p.Brand, maxLevel: 1).Execute();
            CollectionAssert.AreEqual(new[] { "Acme" }, r.Select(b => b.Name).ToArray());
        }
        { // chained, re-typed both ways: product -> brand -> all products of that brand
            var r = store.Query<XtProduct>().Where(p => p.Name == "P200")
                .Traverse<XtBrand>(p => p.Brand, maxLevel: 1)
                .Traverse<XtProduct>(b => b.Products, maxLevel: 1)
                .Execute();
            CollectionAssert.AreEqual(new[] { "P100", "P200", "P300" }, r.Select(p => p.Name).OrderBy(n => n).ToArray());
        }
    }

    [TestMethod]
    public void IncludeWithFilterAcrossTwoTypes() {
        using var store = openStore(out _, out _);
        { // filter on the related type, indexed property: only the expensive products load
            var brands = store.Query<XtBrand>().Where(b => b.Name == "Acme")
                .Preload<XtProduct>(b => b.Products, p => p.Price >= 200).Execute();
            var acme = brands.Single();
            CollectionAssert.AreEqual(new[] { "P200", "P300" }, acme.Products.Select(p => p.Name).OrderBy(n => n).ToArray());
        }
        { // same, filtered down to nothing
            var acme = store.Query<XtBrand>().Where(b => b.Name == "Acme")
                .Preload<XtProduct>(b => b.Products, p => p.Price > 1000).Execute().Single();
            Assert.AreEqual(0, acme.Products.Count());
        }
        { // one side, filter passes
            var p = store.Query<XtProduct>().Where(p => p.Name == "P200")
                .Preload<XtBrand>(p => p.Brand, b => b.Country == "NO").Execute().Single();
            Assert.IsTrue(p.Brand.HasPreloadedData());
            Assert.AreEqual("Acme", p.Brand.Get().Name);
        }
        { // one side, filter fails -> nothing preloaded
            var p = store.Query<XtProduct>().Where(p => p.Name == "P200")
                .Preload<XtBrand>(p => p.Brand, b => b.Country == "SE").Execute().Single();
            Assert.IsFalse(p.Brand.HasPreloadedData());
        }
    }
}
