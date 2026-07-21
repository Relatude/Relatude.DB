using Relatude.DB.DataStores;
using Relatude.DB.Nodes;
using Relatude.DB.Utils;
using Relatude.DB.DataStores.Indexes.KvStore;

namespace Tests;

internal static class QueryTestHelpers {

    internal static NodeStore OpenStoreWithArticles(int count) {
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel, new SettingsLocal() {
            UsePersistedValueIndexesByDefault = true,
            PersistedValueIndexEngine = PersistedValueIndexEngine.Native,
        }, null, null, null, null, null,
        () => new NativeKvIndexStore(null, null)

        );
        var store = new NodeStore(storeData);
        store.Insert(Helper.GenerateArticles(count));
        return store;
    }

    // Runs the exact same expression against the store and compiled LINQ and compares the full id sets.
    internal static void AssertSameNodes(NodeStore store, List<Article> all, System.Linq.Expressions.Expression<Func<Article, bool>> predicate, bool mustDiscriminate = true) {
        var fromStore = store.Query<Article>().Where(predicate).Execute().Select(c => c.Id).OrderBy(i => i).ToList();
        var fromLinq = all.Where(predicate.Compile()).Select(c => c.Id).OrderBy(i => i).ToList();
        CollectionAssert.AreEqual(fromLinq, fromStore, "Store and LINQ disagree for: " + predicate);
        if (mustDiscriminate) // guard against a parse bug reducing the predicate to constant true/false
            Assert.IsTrue(fromLinq.Count > 0 && fromLinq.Count < all.Count, "Predicate does not discriminate (matched " + fromLinq.Count + " of " + all.Count + "): " + predicate);
    }
}
