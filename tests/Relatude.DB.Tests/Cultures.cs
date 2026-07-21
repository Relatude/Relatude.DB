using System.Diagnostics;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.Utils;

namespace Tests {
    [TestClass]
    public class Cultures {
        [TestMethod]
        public void TestCultures() {
           // var io = new IOProviderMemory();
           // var datamodel = Helper.GetDatamodel();
           // var storeData = DataStoreLocal.Open(datamodel, null, io);

           // var settings = storeData.GetContentSettings().Result;
           // settings.Cultures.Add(new() { Id = Guid.NewGuid(), LCID = 1044, Name = "Norsk" });

           //// storeData.GetContentSettings



           // var store = new NodeStore(storeData);


           // var articles = Helper.GenerateArticles(1000);

           // foreach (var chunk in articles.Chunk(10)) store.Insert(chunk);

           // store.Dispose();

        }
    }
}