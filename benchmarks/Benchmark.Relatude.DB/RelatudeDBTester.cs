using Benchmark.Base;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Indexes.KvStore;
using Relatude.DB.IO;
using Relatude.DB.Native;
using Relatude.DB.Nodes;

namespace Benchmark.Relatude.DB;

public enum RelatudeDiskFlushMode {
    NoFlush,
    AutoFlush,
    StreamFlush,
    DiskFlush
}
public enum RelatudeIndexType {
    Memory,
    Sqlite,
    Native,
}
public class RelatudeDBTester : ITester {
    public RelatudeDBTester(RelatudeDiskFlushMode mode, RelatudeIndexType indexType) {
        _flushMode = mode;
        _indexType = indexType;
    }
    RelatudeDiskFlushMode _flushMode;
    RelatudeIndexType _indexType;
    string _dataFolderPath = null!;
    IDataStore _dataStore = null!;
    public string Name => "Relatude " + _flushMode.ToString().Decamelize(false) + " " + _indexType.ToString().Decamelize(false);
    NodeStore _store = null!;
    bool _flushEveryTrans = false;
    public void Initalize(string dataFolderPath, TestOptions options) {
        _dataFolderPath = dataFolderPath;
        _flushEveryTrans = _flushMode == RelatudeDiskFlushMode.StreamFlush || _flushMode == RelatudeDiskFlushMode.DiskFlush;
    }
    public void Open() {
        var dm = new Datamodel();
        dm.Add<TestUser>();
        dm.Add<TestCompany>();
        dm.Add<TestDocument>();
        var settings = new SettingsLocal();
        settings.WriteSystemLogConsole = false;
        settings.DoNotCacheMapperFile = false;
        settings.EnableTextIndexByDefault = false;
        settings.DefaultReadAccess = SystemGroupType.Everyone;
        settings.DefaultWriteAccess = SystemGroupType.Everyone;
        settings.NodeCacheSizeGb = 10;
        settings.SetCacheSizeGb = 0;
        settings.SecondaryBackupLog = false;
        settings.AutoPurgeCache = false;
        settings.AutoSaveIndexStates = false;
        settings.AutoSaveIndexStatesActionCountLowerLimit = 10000000;
        settings.AutoSaveIndexStatesIntervalInMinutes = 10000;
        settings.MaxDelayAutoDiskFlushIfBusyInSeconds = int.MaxValue;
        settings.ForceDiskFlushAfterActionCountLimit = int.MaxValue;
        settings.AutoFlushDiskInBackground = _flushMode == RelatudeDiskFlushMode.AutoFlush;
        settings.DeepFlushDisk = _flushMode == RelatudeDiskFlushMode.DiskFlush;
        Func<IPersistedIndexStore>? createIndex = null;
        
        if (_indexType == RelatudeIndexType.Sqlite) {
            settings.PersistedValueIndexEngine = PersistedValueIndexEngine.Sqlite;
            settings.UsePersistedValueIndexesByDefault = true;
            createIndex = () => new SqliteIndexStore(_dataFolderPath, null);
        } else if (_indexType == RelatudeIndexType.Native) {
            settings.PersistedValueIndexEngine = PersistedValueIndexEngine.Native;
            settings.UsePersistedValueIndexesByDefault = true;
            createIndex = () => new NativeKvIndexStore(_dataFolderPath, null);
        }

        var io = new IOProviderDisk(_dataFolderPath!);
        _dataStore = new DataStoreLocal(datamodel: dm, settings: settings, dbIO: io, createPersistedIndexStore: createIndex);
        _dataStore.Open();
        _store = new NodeStore(_dataStore);
    }
    public void CreateSchema() {
    }
    public void InsertUsers(TestUser[] users) {
        var result = _store.Insert(users, flushToDisk: _flushEveryTrans, ignoreRelated: true);
    }
    public void InsertCompanies(TestCompany[] companies) {
        _store.Insert(companies, flushToDisk: _flushEveryTrans, ignoreRelated: true);
    }
    public void InsertDocuments(TestDocument[] documents) {
        _store.Insert(documents, flushToDisk: _flushEveryTrans, ignoreRelated: true);
    }
    public void RelateDocumentsToUsers(IEnumerable<Tuple<Guid, Guid>> relations) {
        var transaction = _store.CreateTransaction();
        foreach (var relation in relations) {
            transaction.AddRelation<TestDocument>(relation.Item1, d => d.Author, relation.Item2);
        }
        _store.Execute(transaction, _flushEveryTrans);
    }
    public void RelateUsersToCompanies(IEnumerable<Tuple<Guid, Guid>> relations) {
        var transaction = _store.CreateTransaction();
        foreach (var relation in relations) {
            transaction.AddRelation<TestUser>(relation.Item1, d => d.Company, relation.Item2);
        }
        _store.Execute(transaction, _flushEveryTrans);
    }
    public TestUser[] GetAllUsers() {
        return _store.Query<TestUser>().Execute().ToArray();
    }
    public TestUser? GetUserById(Guid id) {
        return _store.Get<TestUser>(id);
    }
    public int CountUsersOfAge(int age) {
        return _store.Query<TestUser>().Where(u => u.Age == age).Count();
    }
    public TestUser[] GetUsersAtAge(int age) {
        return _store.Query<TestUser>().Where(u => u.Age == age).Execute().ToArray();
    }
    public void UpdateUserAge(Guid userId, int newAge) {
        _store.UpdateIfDifferentProperty<TestUser, int>(userId, u => u.Age, newAge, _flushEveryTrans);
    }
    public void FlushToDisk() {
        _store.Flush();
    }
    public void DeleteUsersOfAge(int age) {
        var ids = _store.Query<TestUser>(u => u.Age == age).SelectId().Execute();
        _store.DeleteIfExists(ids, _flushEveryTrans);
    }
    public void Close() {
        _store.Dispose();
    }
    public void DeleteDataFiles() {
        if (Directory.Exists(_dataFolderPath)) Directory.Delete(_dataFolderPath, true);
    }
}
