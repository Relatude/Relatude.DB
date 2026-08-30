using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.Utils;
using NodeStore = Relatude.DB.Nodes.NodeStore; // disambiguate from the internal DataStores.Stores.NodeStore (visible via InternalsVisibleTo)

namespace Relatude.Persistence;

/// <summary>
/// The crash safety of the state and index snapshot files: every save writes a NEW numbered file
/// (state.00000001.bin / index.[id].00000001.bin) ending with the completion marker, and deletes
/// the older files only after the new one is complete. At open, numbered files that do not end
/// with the marker (interrupted mid-write) are deleted, and the previous complete file is used
/// instead, so the log does not have to be replayed from the beginning. Numbered names rather
/// than write-and-rename, because some IO providers cannot rename files.
/// </summary>
[TestClass]
public class StateFileCompletionTests {

    [TestMethod]
    public void SaveWritesANewNumberedFile_AndDeletesTheOldOne() {
        var io = new IOProviderMemory();
        var storeData = DataStoreLocal.Open(Helper.GetDatamodel(), null, io);
        var store = new NodeStore(storeData);
        var articles = Helper.GenerateArticles(60); // deterministic ids, so the second batch below must come from the same generated set
        foreach (var chunk in articles.Take(50).Chunk(10)) store.Insert(chunk);
        storeData.Maintenance(MaintenanceAction.SaveIndexStates);
        var firstKey = FileKeyUtility.State_GetNewestFileKey(io)!;
        Assert.IsTrue(FileKeyUtility.State_IsNumberedFileKey(firstKey));
        Assert.IsTrue(FileKeyUtility.EndsWithStateFileCompletionMarker(io, firstKey));
        foreach (var indexKey in FileKeyUtility.Index_GetAll(io)) {
            Assert.IsTrue(FileKeyUtility.Index_IsNumberedFileKey(indexKey), "index state files must be numbered: " + indexKey.AsKeyString());
            Assert.IsTrue(FileKeyUtility.EndsWithStateFileCompletionMarker(io, indexKey), "index state files must end with the marker: " + indexKey.AsKeyString());
        }
        store.Insert(articles.Skip(50));
        storeData.Maintenance(MaintenanceAction.SaveIndexStates);
        var secondKey = FileKeyUtility.State_GetNewestFileKey(io)!;
        Assert.AreNotEqual(firstKey.AsKeyString(), secondKey.AsKeyString(), "a save must write a new numbered file");
        Assert.IsFalse(io.Exists(firstKey), "the previous state file must be deleted after the new one is complete");
        Assert.AreEqual(1, FileKeyUtility.State_GetAllFileKeys(io).Length);
        store.Dispose();
    }

    [TestMethod]
    public void IncompleteStateAndIndexFiles_AreDeletedAtOpen_AndTheOlderSnapshotIsUsed() {
        var io = new IOProviderMemory();
        var storeData = DataStoreLocal.Open(Helper.GetDatamodel(), null, io);
        var store = new NodeStore(storeData);
        var articles = Helper.GenerateArticles(100);
        foreach (var chunk in articles.Chunk(10)) store.Insert(chunk);
        storeData.Maintenance(MaintenanceAction.SaveIndexStates);
        store.Dispose();

        // simulate saves interrupted mid-write by a shutdown: newer numbered files without the
        // completion marker, next to the older complete ones
        var completeStateKey = FileKeyUtility.State_GetNewestFileKey(io)!;
        var incompleteStateKey = FileKeyUtility.State_NextFileKey(io);
        io.WriteAllBytes(incompleteStateKey, [1, 2, 3, 4]);
        var completeIndexKey = FileKeyUtility.Index_GetAllNumbered(io).First();
        var indexId = completeIndexKey.FileName().Split('.')[1];
        var incompleteIndexKey = FileKeyUtility.Index_NextFileKey(indexId, FileKeyUtility.Index_GetAllFileKeys(io, indexId));
        io.WriteAllBytes(incompleteIndexKey, [1, 2, 3, 4]);

        storeData = DataStoreLocal.Open(Helper.GetDatamodel(), null, io, null, null, null, null, null, true, true); // throw on bad files: the fallback must be clean
        store = new NodeStore(storeData);
        Assert.AreEqual(articles.Count, store.Query<Article>().Count());
        Assert.IsFalse(io.Exists(incompleteStateKey), "the incomplete state file must be deleted at open");
        Assert.IsFalse(io.Exists(incompleteIndexKey), "the incomplete index state file must be deleted at open");
        Assert.IsTrue(io.Exists(completeStateKey), "the older complete state file must be kept and used");
        Assert.IsTrue(io.Exists(completeIndexKey), "the older complete index state file must be kept and used");
        store.Dispose();
    }

    [TestMethod]
    public void FilesInTheOldNamingFormat_AreDeletedAtOpen_AndTheStateIsRebuiltFromTheLog() {
        var io = new IOProviderMemory();
        var storeData = DataStoreLocal.Open(Helper.GetDatamodel(), null, io);
        var store = new NodeStore(storeData);
        var articles = Helper.GenerateArticles(50);
        foreach (var chunk in articles.Chunk(10)) store.Insert(chunk);
        storeData.Maintenance(MaintenanceAction.SaveIndexStates);
        store.Dispose();

        // turn the numbered files into the old naming format (state.bin / index.[id].bin, no
        // completion marker), the way a store written by an older version looks on disk
        var stateKey = FileKeyUtility.State_GetNewestFileKey(io)!;
        io.WriteAllBytes(FileKeyUtility.State_LegacyFileKey, io.ReadAllBytes(stateKey)[..^16]);
        io.DeleteFileIfItExists(stateKey);
        var legacyIndexKeys = new List<string[]>();
        foreach (var indexKey in FileKeyUtility.Index_GetAllNumbered(io)) {
            var indexId = indexKey.FileName().Split('.')[1];
            var legacyKey = FileKeyUtility.Index_GetLegacyFileKey(indexId);
            io.WriteAllBytes(legacyKey, io.ReadAllBytes(indexKey)[..^16]);
            io.DeleteFileIfItExists(indexKey);
            legacyIndexKeys.Add(legacyKey);
        }

        // the old format files are deleted at open and the state is rebuilt from the log
        storeData = DataStoreLocal.Open(Helper.GetDatamodel(), null, io, null, null, null, null, null, true, true);
        store = new NodeStore(storeData);
        Assert.AreEqual(articles.Count, store.Query<Article>().Count());
        Assert.IsFalse(io.Exists(FileKeyUtility.State_LegacyFileKey), "the old format state file must be deleted at open");
        foreach (var legacyKey in legacyIndexKeys) Assert.IsFalse(io.Exists(legacyKey), "old format index state files must be deleted at open: " + legacyKey.AsKeyString());
        storeData.Maintenance(MaintenanceAction.SaveIndexStates);
        Assert.IsTrue(FileKeyUtility.State_IsNumberedFileKey(FileKeyUtility.State_GetNewestFileKey(io)!));
        foreach (var indexKey in FileKeyUtility.Index_GetAll(io)) {
            Assert.IsTrue(FileKeyUtility.Index_IsNumberedFileKey(indexKey), "index state files must be numbered again after the next save: " + indexKey.AsKeyString());
        }
        store.Dispose();
    }
}
