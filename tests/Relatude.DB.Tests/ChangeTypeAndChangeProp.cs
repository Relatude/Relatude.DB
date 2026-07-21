using Relatude.DB.Common;
using Relatude.DB.Nodes;
using Relatude.DB.Transactions;
using Relatude.DB.Utils;

namespace Tests {
    [TestClass]
    public class ChangeTypeAndChangeProp {
        [TestMethod]
        public void Test() {
            using var store = Helper.Open();
            int i = 0;
            while (++i < 1000) {
                store.Insert(new Article() { Name = "Name_" + i, Body = "Body_" + i });
            }
            Assert.IsTrue(store.Query<Article>().Count() == 999);
            i = 0;
            var a1 = store.Get<Article>(1);
            while (++i < 2) {
                var a = store.Get<Article>(i);
                a.File.Name = "Name_" + i;
                store.Update(a);
            }
            // creating a file with random data
            var data = new byte[1000];
            new Random().NextBytes(data);
            var fileKey = Guid.NewGuid().ToString();

            store.UpdateProperty(a1, a => a.Name, "dasdas");

            store.AddToProperty(a1, a => a.Name, "-+");
            store.AddToProperty(a1, a => a.IntegerNum, 10);
            store.MultiplyProperty(a1, a => a.IntegerNum, 2);

            var a2 = store.Get<Article>(1);
            Assert.IsTrue("dasdas" + "-+" == a2.Name);
            Assert.IsTrue(a2.IntegerNum == 20);

            store.ChangeType<Article2>(a2);
            var a3 = store.Get<Article2>(1);
            Assert.IsTrue(a3 is Article2);



        }
    }
}

