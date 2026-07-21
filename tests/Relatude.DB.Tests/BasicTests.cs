using Relatude.DB.Utils;

namespace Tests {
    [TestClass]
    public class BasicTests {
        [TestMethod]
        public void TestDatamodel() {
            var dm = Helper.GetDatamodel();
            dm.EnsureInitalization();
            Assert.IsTrue(dm.NodeTypes.Count == 5);
            Assert.IsTrue(dm.Relations.Count == 3);
        }
        [TestMethod]
        public void OpenClose() {
            var store = Helper.Open();
            store.Dispose();
        }
        [TestMethod]
        public void Insert() {
            using var store = Helper.Open();
            int i = 0;
            while (++i < 1000) {
                store.Insert(new Article() { Name = "Name_" + i, Body = "Body_" + i });
            }
            Assert.IsTrue(store.Query<Article>().Count() == 999);
            i = 0;
            while (++i < 1000) {
                store.Get<Article>(i);
            }
        }

    }
}

