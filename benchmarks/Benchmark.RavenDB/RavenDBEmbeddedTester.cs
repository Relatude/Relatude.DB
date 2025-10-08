using Benchmark.Base;
using Benchmark.Base.Models;
using Raven.Client.Documents;
using Raven.Embedded;
namespace Benchmark.RavenDB;
public class RavenDBEmbeddedTester : ITester {
    IDocumentStore? _store;
    string _dataPath = string.Empty;
    public string Name => "Raven DB Embedded";
    public void Initalize(string dataFolderPath) {
        _dataPath = dataFolderPath;
    }
    public void Open() {
        if(!Directory.Exists(_dataPath)) {
            Directory.CreateDirectory(_dataPath);
        }
        EmbeddedServer.Instance.StartServer(new ServerOptions {
            DataDirectory = _dataPath,
        });
        _store = EmbeddedServer.Instance.GetDocumentStore(new DatabaseOptions("embedded"));
        _store.Initialize();
    }
    public void CreateSchema() {

    }
    public int CountUsersOfAge(int age) {
        using var session = _store!.OpenSession();
        return session.Query<TestUser>().Where(u => u.Age == age).Count();
    }
    public void DeleteDataFiles() {
        if (Directory.Exists(_dataPath)) {
            Directory.Delete(_dataPath, true);
        }
    }
    public void DeleteUsers(int age) {
        using var session = _store!.OpenSession();
        var users = session!.Query<TestUser>().Where(u => u.Age == age).ToList();
        foreach (var user in users) {
            session.Delete(user);
        }
        session.SaveChanges();
    }
    public void FlushToDisk() {
        
    }
    public TestUser[] GetAllUsers() {
        using var session = _store!.OpenSession();
        return session!.Query<TestUser>().ToArray();
    }
    public TestUser[] GetUserAtAge(int age) {
        using var session = _store!.OpenSession();
        return session!.Query<TestUser>().Where(u => u.Age == age).ToArray();
    }
    public TestUser? GetUserById(Guid id) {
        using var session = _store!.OpenSession();
        return session!.Load<TestUser>(id.ToString());
    }
    public void InsertCompanies(TestCompany[] companies) {
        using var session = _store!.OpenSession();
        session!.Store(companies);
    }
    public void InsertDocuments(TestDocument[] documents) {
        using var session = _store!.OpenSession();
        session!.Store(documents);
    }
    public void InsertUsers(TestUser[] users) {
        using var session = _store!.OpenSession();
        session!.Store(users);
    }
    public void RelateDocumentsToUsers(IEnumerable<Tuple<Guid, Guid>> relations) {
    }
    public void RelateUsersToCompanies(IEnumerable<Tuple<Guid, Guid>> relations) {
    }
    public void UpdateUserAge(Guid userId, int newAge) {
        using var session = _store!.OpenSession();
        var user = session!.Load<TestUser>(userId.ToString());
        if (user != null) {
            user.Age = newAge;
            session.SaveChanges();
        }
    }
    public void Close() {
        _store?.Dispose();
        EmbeddedServer.Instance.Dispose();
    }
}
