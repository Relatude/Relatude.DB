using Benchmark.Base;
using Benchmark.Base.Models;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;

namespace Benchmark.Relatude.DB;
public enum RelatudeDiskFlushMode {
    NoFlush,
    AutoFlush,
    StreamFlush,
    DiskFlush
}
public class RelatudeDBTester : ITester {
    public RelatudeDBTester(RelatudeDiskFlushMode mode) {
        _flushMode = mode;
    }
    RelatudeDiskFlushMode _flushMode;
    string _dataFolderPath = null!;
    IDataStore _dataStore = null!;
    public string Name => "Relatude " + _flushMode.ToString().Decamelize(false);
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
        settings.WriteSystemLogConsole = true;
        settings.DoNotCacheMapperFile = false;
        settings.EnableTextIndexByDefault = false;
        settings.AutoFlushDiskInBackground = _flushMode == RelatudeDiskFlushMode.AutoFlush;
        settings.DeepFlushDisk = _flushMode == RelatudeDiskFlushMode.DiskFlush;
        var io = new IOProviderDisk(_dataFolderPath!);
        _dataStore = new DataStoreLocal(dm, settings, io);
        _dataStore.Open();
        _store = new NodeStore(_dataStore);
    }
    public void CreateSchema() {
    }
    public void InsertUsers(TestUser[] users) {
        var result = _store.Insert(users, _flushEveryTrans, true);
    }
    public void InsertCompanies(TestCompany[] companies) {
        _store.Insert(companies, _flushEveryTrans, true);
    }
    public void InsertDocuments(TestDocument[] documents) {
        _store.Insert(documents, _flushEveryTrans, true);
    }
    public void RelateDocumentsToUsers(IEnumerable<Tuple<Guid, Guid>> relations) {
        var transaction = _store.CreateTransaction();
        foreach (var relation in relations) {
            transaction.Relate<TestDocument>(relation.Item1, d => d.Author, relation.Item2);
        }
        _store.Execute(transaction, _flushEveryTrans);
    }
    public void RelateUsersToCompanies(IEnumerable<Tuple<Guid, Guid>> relations) {
        var transaction = _store.CreateTransaction();
        foreach (var relation in relations) {
            transaction.Relate<TestUser>(relation.Item1, d => d.Company, relation.Item2);
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
        _store.UpdateProperty<TestUser, int>(userId, u => u.Age, newAge, _flushEveryTrans);
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
