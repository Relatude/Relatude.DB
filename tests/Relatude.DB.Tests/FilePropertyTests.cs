using Relatude.DB.Common;
using Relatude.DB.Nodes;
using Relatude.DB.Transactions;
using Relatude.DB.Utils;

namespace Tests {
    [TestClass]
    public class FilePropertyTests {
        [TestMethod]
        public async Task Insert() {
            using var store = Helper.Open();
            uint i = 0;
            while (++i < 1000) {
                store.Insert(new Article() { Name = "Name_" + i, Body = "Body_" + i });
            }
            Assert.IsTrue(store.Query<Article>().Count() == 999);
            i = 0;
            var a1 = store.Get<Article>(1);

            // creating a file with random data
            var data = new byte[1000];
            new Random().NextBytes(data);
            var fileKey = Guid.NewGuid().ToString();

            await store.FileUploadAsync(a1, a => a.File, data, fileKey);
            Assert.IsTrue(await store.FileUploadedAndAvailableAsync(a1, a => a.File));
            var downloadedData = await store.FileDownloadAsync(a1, a => a.File);

            Assert.IsTrue(data.SequenceEqual(downloadedData));



        }
    }
}

