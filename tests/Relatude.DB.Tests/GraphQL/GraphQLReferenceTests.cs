using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.GraphQL;
using Relatude.DB.Nodes;
using Relatude.Querying;
using static Relatude.GraphQL.GraphQLTestHelper;

namespace Relatude.GraphQL;

/// <summary>Covers plain Reference/References properties (Guid-backed, no relation index) end-to-end.</summary>
[TestClass]
public class GraphQLReferenceTests {

    static (NodeStore store, RelatudeGraphQL gql, PlainRefBrand brand, PlainRefTag[] tags) open() {
        var dm = new Datamodel();
        dm.Add<PlainRefProduct>(); // default: plain node members classify as Reference/References
        dm.Add<PlainRefBrand>();
        dm.Add<PlainRefTag>();
        var storeData = DataStoreLocal.Open(dm);
        var store = new NodeStore(storeData);
        var brand = new PlainRefBrand { Id = Guid.NewGuid(), Name = "Acme" };
        var t1 = new PlainRefTag { Id = Guid.NewGuid(), Name = "T1" };
        var t2 = new PlainRefTag { Id = Guid.NewGuid(), Name = "T2" };
        store.Insert(brand);
        store.Insert(new[] { t1, t2 });
        store.Insert(new PlainRefProduct { Id = Guid.NewGuid(), Name = "P1", Brand = brand, TagsArray = [t1, t2] });
        store.Insert(new PlainRefProduct { Id = Guid.NewGuid(), Name = "P2" }); // no references set
        var gql = new RelatudeGraphQL(storeData);
        return (store, gql, brand, [t1, t2]);
    }

    [TestMethod]
    public void References_AreIncludedAndProjected() {
        var (store, gql, _, _) = open();
        try {
            var data = RequireData(gql.Execute("""
                { plainRefProducts(orderBy: name) { items { name brand { name } tagsArray { name } } } }
                """));
            var items = (List<object?>)Get(data, "plainRefProducts", "items")!;
            Assert.AreEqual(2, items.Count);
            Assert.AreEqual("P1", Get(items[0], "name"));
            Assert.AreEqual("Acme", Get(items[0], "brand", "name"));
            var tagNames = ((List<object?>)Get(items[0], "tagsArray")!).Select(t => (string?)Get(t, "name")).ToArray();
            CollectionAssert.AreEqual(new[] { "T1", "T2" }, tagNames, "stored order is preserved");
            // unset references: single -> null, many -> empty list
            Assert.IsNull(Get(items[1], "brand"));
            Assert.AreEqual(0, ((List<object?>)Get(items[1], "tagsArray")!).Count);
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void ReferenceFilter_ByRelatedNodeId() {
        var (store, gql, brand, _) = open();
        try {
            var data = RequireData(gql.Execute($$"""
                { plainRefProducts(filter: { brand: { eq: "{{brand.Id}}" } }) { totalCount items { name } } }
                """));
            Assert.AreEqual(1, Get(data, "plainRefProducts", "totalCount"));
            Assert.AreEqual("P1", Get(data, "plainRefProducts", "items", 0, "name"));
        } finally { store.Dispose(); }
    }

    [TestMethod]
    public void ManyReference_TopArgument_CapsResults() {
        var (store, gql, _, _) = open();
        try {
            var data = RequireData(gql.Execute("""
                { plainRefProducts(filter: { name: { eq: "P1" } }) { items { tagsArray(top: 1) { name } } } }
                """));
            var tags = (List<object?>)Get(data, "plainRefProducts", "items", 0, "tagsArray")!;
            Assert.AreEqual(1, tags.Count);
            Assert.AreEqual("T1", Get(tags[0], "name"));
        } finally { store.Dispose(); }
    }
}
