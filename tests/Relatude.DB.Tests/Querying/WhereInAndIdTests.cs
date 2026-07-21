using Relatude.DB.Query;
using Relatude.DB.Utils;
using static Tests.QueryTestHelpers;

namespace Tests;

[TestClass]
public class WhereInAndIdTests {

    [TestMethod]
    public void TestWhereInComparedToLinq() {
        var store = OpenStoreWithArticles(200);
        var all = store.Query<Article>().ToList();

        { // WhereIn on integers
            var values = new[] { 1, 3, 5, 7 };
            var fromStore = store.Query<Article>().WhereIn(c => c.IntegerNum, values).Count();
            var fromLinq = all.Count(c => values.Contains(c.IntegerNum));
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // WhereIn on strings
            var names = new[] { "Test 1", "Test 2", "Test 3", "No such name" };
            var fromStore = store.Query<Article>().WhereIn(c => c.Name, names).Count();
            var fromLinq = all.Count(c => names.Contains(c.Name));
            Assert.AreEqual(3, fromStore);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // WhereIn with empty list
            var fromStore = store.Query<Article>().WhereIn(c => c.IntegerNum, Array.Empty<int>()).Count();
            Assert.AreEqual(0, fromStore);
        }

        { // WhereIn combined with Where
            var values = new[] { 2, 4, 6 };
            var fromStore = store.Query<Article>().WhereIn(c => c.IntegerNum, values).Where(c => c.DoubleNum > 5).Count();
            var fromLinq = all.Count(c => values.Contains(c.IntegerNum) && c.DoubleNum > 5);
            Assert.AreEqual(fromLinq, fromStore);
        }

        store.Dispose();
    }

    [TestMethod]
    public void TestQueryByIdComparedToLinq() {
        // Note: the IQueryOfNodes.Where(int/Guid/IEnumerable) id-overloads emit query strings the
        // parser rejects ("where" only accepts lambdas). The NodeStore.Query<T>(id) overloads below
        // are the supported paths.
        var store = OpenStoreWithArticles(50);
        var all = store.Query<Article>().ToList();

        { // Query<T>(int id)
            var expected = all.Single(c => c.Id == 7);
            var fromStore = store.Query<Article>(7).Execute().ToList();
            Assert.AreEqual(1, fromStore.Count);
            Assert.AreEqual(expected.Id, fromStore[0].Id);
        }

        { // Query<T>(Guid id)
            var expected = all.Single(c => c.Id == 7);
            var fromStore = store.Query<Article>(expected.PId).Execute().ToList();
            Assert.AreEqual(1, fromStore.Count);
            Assert.AreEqual(expected.PId, fromStore[0].PId);
        }

        { // Query<T>(IEnumerable<Guid> ids)
            var guids = all.Where(c => c.Id <= 4).Select(c => c.PId).ToList();
            var fromStore = store.Query<Article>(guids).Execute().Select(c => c.PId).OrderBy(g => g).ToList();
            var fromLinq = all.Where(c => guids.Contains(c.PId)).Select(c => c.PId).OrderBy(g => g).ToList();
            CollectionAssert.AreEqual(fromLinq, fromStore);
        }

        { // lambda on internal id
            var ids = new[] { 3, 5, 9 };
            var fromStore = store.Query<Article>().Where(c => c.Id == 3 || c.Id == 5 || c.Id == 9).Execute().Select(c => c.Id).OrderBy(id => id).ToList();
            var fromLinq = all.Where(c => ids.Contains(c.Id)).Select(c => c.Id).OrderBy(id => id).ToList();
            CollectionAssert.AreEqual(fromLinq, fromStore);
        }

        store.Dispose();
    }
}
