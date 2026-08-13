using System.Text.Json;
using Relatude.DB.DataStores;
using Relatude.DB.Nodes;
using Relatude.DB.Query;
using Relatude.Utils;

namespace Relatude.Querying;

/// <summary>
/// Projections - Select(x => new { ... }) - materialise into whatever the caller asked for: the anonymous
/// type when the typed API supplies one, and the projected members by name when the caller reads the result
/// as object, which is what the JSON results used by the HTTP API and the command line tool do.
/// </summary>
[TestClass]
public class ProjectionTests {

    static NodeStore openStore() {
        var store = new NodeStore(DataStoreLocal.Open(Helper.GetDatamodel()));
        for (var i = 1; i <= 3; i++) {
            store.Insert(new Article { Id = i, Name = "a" + i, IntegerNum = i, DoubleNum = i * 1.5, Size = Sizes.Medium });
        }
        return store;
    }
    static ResultSetNotEnumerable<object?> forJson(NodeStore store, string query)
        => (ResultSetNotEnumerable<object?>)store.EvaluateForJsonAsync(query, []).GetAwaiter().GetResult()!;

    [TestMethod]
    public void TestProjectionToAnonymousType() {
        var store = openStore();
        var rows = store.Query<Article>().OrderBy(a => a.Name).Select(a => new { a.Name, a.IntegerNum }).Execute().ToList();
        Assert.AreEqual(3, rows.Count);
        Assert.AreEqual("a1", rows[0]!.Name);
        Assert.AreEqual(1, rows[0]!.IntegerNum);
        store.Dispose();
    }

    [TestMethod]
    public void TestProjectionAsObjectIsReadableByName() {
        var store = openStore();
        var result = forJson(store, "Article.Where(a => a.IntegerNum == 2).Select(a => new { a.Name, a.DoubleNum })");
        var row = (Dictionary<string, object?>)result.Values.Single()!;
        Assert.AreEqual("a2", row["Name"]);
        Assert.AreEqual(3.0, row["DoubleNum"]);
        CollectionAssert.AreEqual(new[] { "Name", "DoubleNum" }, row.Keys.ToArray()); // in the order projected
        store.Dispose();
    }

    [TestMethod]
    public void TestProjectionAsJsonIsAnObjectOfItsMembers() {
        var store = openStore();
        var json = JsonSerializer.Serialize(forJson(store, "Article.OrderBy(a => a.Name).Select(a => new { a.Name, a.IntegerNum })"));
        using var document = JsonDocument.Parse(json);
        var values = document.RootElement.GetProperty("Values");
        Assert.AreEqual(3, values.GetArrayLength());
        var first = values[0];
        Assert.AreEqual(JsonValueKind.Object, first.ValueKind);
        Assert.AreEqual("a1", first.GetProperty("Name").GetString());
        Assert.AreEqual(1, first.GetProperty("IntegerNum").GetInt32());
        Assert.AreEqual(2, first.EnumerateObject().Count()); // only what was projected
        store.Dispose();
    }

    [TestMethod]
    public void TestProjectionWithRenamedAndComputedMembers() {
        var store = openStore();
        var result = forJson(store, "Article.Where(a => a.IntegerNum == 3).Select(a => new { Title = a.Name, Doubled = a.IntegerNum * 2 })");
        var row = (Dictionary<string, object?>)result.Values.Single()!;
        Assert.AreEqual("a3", row["Title"]);
        Assert.AreEqual(6, row["Doubled"]);
        store.Dispose();
    }

    [TestMethod]
    public void TestProjectionOfOneMemberStaysAValueList() {
        var store = openStore(); // no anonymous object: the result is the values themselves, not objects
        var result = forJson(store, "Article.OrderBy(a => a.Name).Select(a => a.Name)");
        CollectionAssert.AreEqual(new[] { "a1", "a2", "a3" }, result.Values.Cast<string>().ToArray());
        store.Dispose();
    }

    [TestMethod]
    public void TestNodeQueryAsJsonIsStillTheNode() {
        var store = openStore(); // regression: without a projection the mapper still builds the node objects
        var result = forJson(store, "Article.Where(a => a.IntegerNum == 1)");
        var node = (Article)result.Values.Single()!;
        Assert.AreEqual("a1", node.Name);
        store.Dispose();
    }
}
