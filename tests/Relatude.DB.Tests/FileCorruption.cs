using System.Diagnostics;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.Utils;

namespace Tests {
    [TestClass]
    public class FileCorruption {
        [TestMethod]
        public void TestCorruptionInLogFile() {
            var io = new IOProviderMemory();
            var datamodel = Helper.GetDatamodel();
            var storeData = DataStoreLocal.Open(datamodel, null, io);
            var store = new NodeStore(storeData);
            var articles = Helper.GenerateArticles(1000);
            foreach (var chunk in articles.Chunk(10)) store.Insert(chunk);
            store.Dispose();

            // adds corruption to the log file:
            io.AddCorruption("db.00000001.bin", 10000, 1); // corrupting one transaction somewhere in the log file

            // validate that the store cannot be opened:
            Assert.ThrowsException<LogReadException>(() => {
                DataStoreLocal.Open(datamodel, null, io, null, null, null, null, null, true, true);
            });

            // validate that the store can be opened with bad data:
            storeData = DataStoreLocal.Open(datamodel, null, io, null, null, null, null, null, false, false);
            //Assert.IsTrue(((DataStore)storeData).GetWarnings().Count() > 0); // verify that warnings are generated

            store = new NodeStore(storeData);
            var all = store.Query<Article>().Execute();
            var countStored = all.Count();
            var countInserted = articles.Count;
            Assert.IsTrue(countInserted - countStored == 10); // veryify that the corrupted insert was skipped ( one transaction is 10 art )
            storeData.Dispose();
        }
        [TestMethod]
        public void TestCorruptionInIndexFile() {
            var io = new IOProviderMemory();
            var datamodel = Helper.GetDatamodel();
            var storeData = DataStoreLocal.Open(datamodel, null, io);
            var store = new NodeStore(storeData);
            var articles = Helper.GenerateArticles(1000);
            foreach (var chunk in articles.Chunk(10)) store.Insert(chunk);
            storeData.Maintenance(MaintenanceAction.SaveIndexStates);
            store.Dispose();

            // add corruption to the state file:
            io.AddCorruption(new FileKeyUtility(null).StateFileKey, 1000, 1000);

            // will throw exception because the index state file is corrupted
            try {
                storeData = DataStoreLocal.Open(datamodel, null, io, null, null, null, null, null, true, false);
            } catch (Exception err){
                Assert.IsTrue(err.InnerException is StateFileReadException);
            }
            //Assert.ThrowsException<StateFileReadException>(() => storeData = DataStoreLocal.Open(datamodel, null, io, null, null, null, null, null, true, false));

            // will not throw exception, will delete the statefile and reload from the log file
            storeData = DataStoreLocal.Open(datamodel, null, io, null, null, null, null, null, false, false);

            storeData.Dispose();

            // statefile is not deleted, so it should not throw exception:
            storeData = DataStoreLocal.Open(datamodel, null, io, null, null, null, null, null, true, false); // open again without corrupted index state file, loading state from log file
            store = new NodeStore(storeData);
            var all = store.Query<Article>().Execute();
            var countStored = all.Count();
            var countInserted = articles.Count;
            Assert.IsTrue(countStored == countInserted);
            storeData.Dispose();
        }
    }
}