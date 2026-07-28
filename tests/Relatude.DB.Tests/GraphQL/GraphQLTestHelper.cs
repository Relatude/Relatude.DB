using Relatude.DB.DataStores;
using Relatude.DB.GraphQL;
using Relatude.DB.Nodes;
using Relatude.Utils;

namespace Relatude.GraphQL;

/// <summary>
/// Opens an in-memory store with the shared Article/Article2/User/Group model and a small
/// deterministic dataset, plus a GraphQL executor over it.
/// Articles 1..12 are Article, 13..15 are Article2 (Name2 = "extra {i}").
/// IntegerNum = i, DoubleNum = i * 1.5, Size = (Sizes)(i % 3).
/// Odd articles are authored by bob, even by alice; alice is in group "Editors";
/// articles 2 and 3 have article 1 as parent.
/// </summary>
internal static class GraphQLTestHelper {

    public static (NodeStore store, RelatudeGraphQL gql, List<Article> all) Open(GraphQLOptions? options = null) {
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel);
        var store = new NodeStore(storeData);
        var all = seed(store);
        var gql = new RelatudeGraphQL(storeData, options);
        return (store, gql, all);
    }

    static List<Article> seed(NodeStore store) {
        // articles first: their explicit internal ids (1..15) must not collide with engine-assigned ids
        var all = new List<Article>();
        for (var i = 1; i <= 15; i++) {
            Article a = i <= 12 ? new Article() : new Article2 { Name2 = "extra " + i };
            a.Id = i;
            a.Name = $"Article {i:00}";
            a.Body = "body " + i;
            a.IntegerNum = i;
            a.DoubleNum = i * 1.5;
            a.Size = (Sizes)(i % 3);
            store.Insert(a);
            all.Add(a);
        }
        var group = new Group { Id = Guid.NewGuid(), Groupname = "Editors" };
        store.Insert(group);
        var alice = new User { Id = Guid.NewGuid(), Username = "alice" };
        var bob = new User { Id = Guid.NewGuid(), Username = "bob" };
        store.Insert(alice);
        store.Insert(bob);
        store.AddRelation(alice, u => u.Group, group);
        foreach (var a in all) store.AddRelation(a, x => x.Author, a.IntegerNum % 2 == 0 ? alice : bob);
        store.AddRelation(all[1], a => a.Parent, all[0]); // article 2 -> parent article 1
        store.AddRelation(all[2], a => a.Parent, all[0]); // article 3 -> parent article 1
        return all;
    }

    /// <summary>Fetches the public Guid of the article with the given internal number.</summary>
    public static Guid PublicId(NodeStore store, int number)
        => store.Query<Article>().Where(a => a.Id == number).Execute().First().PId;

    /// <summary>Navigates a projected result tree: strings index dictionaries, ints index lists.</summary>
    public static object? Get(object? node, params object[] path) {
        foreach (var segment in path) {
            node = segment switch {
                string key => ((Dictionary<string, object?>)node!)[key],
                int index => ((List<object?>)node!)[index],
                _ => throw new ArgumentException("Path segments must be strings or ints."),
            };
        }
        return node;
    }

    public static Dictionary<string, object?> RequireData(GraphQLResult result) {
        if (result.Errors != null && result.Errors.Count > 0) {
            Assert.Fail("Unexpected GraphQL errors: " + string.Join(" | ", result.Errors.Select(e => e.Message)));
        }
        Assert.IsNotNull(result.Data, "Expected data in the GraphQL result.");
        return result.Data!;
    }
}
