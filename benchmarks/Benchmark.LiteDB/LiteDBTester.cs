using LiteDB;
using Benchmark.Base;
using Benchmark.Base.Models;
namespace Benchmark.LiteDB;
public class LiteDBTester : ITester {
    public string Name => "LiteDB";
    string _path = null!;
    LiteDatabase _db;
    ILiteCollection<TestUser> _usersCollection = null!;
    ILiteCollection<TestCompany> _companiesCollection = null!;
    ILiteCollection<TestDocument> _documentsCollection = null!;
    public void Initalize(string dataFolderPath) {
        _path = dataFolderPath;
        if (!Directory.Exists(_path)) Directory.CreateDirectory(_path);
    }
    public void Open() {
        var dbFileName = "db.litedb";
        var dbPath = Path.Combine(_path, dbFileName);
        if (!Directory.Exists(_path)) Directory.CreateDirectory(_path);
        _db = new LiteDatabase(dbPath);
        _usersCollection = _db.GetCollection<TestUser>("test_user");
        _companiesCollection = _db.GetCollection<TestCompany>("test_company");
        _documentsCollection = _db.GetCollection<TestDocument>("test_document");
        // ensure index on age:
        _usersCollection.EnsureIndex(x => x.Age);
        _usersCollection.EnsureIndex(x => x.Id);
        _companiesCollection.EnsureIndex(x => x.Id);
        _documentsCollection.EnsureIndex(x => x.Id);

    }
    public void CreateSchema() {
    }
    public void DeleteDataFiles() {
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }
    public void InsertUsers(TestUser[] users) {
        _usersCollection.InsertBulk(users);
    }
    public void InsertCompanies(TestCompany[] companies) {
        _companiesCollection.InsertBulk(companies);
    }
    public void InsertDocuments(TestDocument[] documents) {
        _documentsCollection.InsertBulk(documents);
    }
    public void RelateDocumentsToUsers(IEnumerable<Tuple<Guid, Guid>> relations) {
        _db.BeginTrans();
        foreach (var rel in relations) {
            var doc = _documentsCollection.FindById(rel.Item1);
            doc.Author = _usersCollection.FindById(rel.Item2);
            _documentsCollection.Update(doc);
        }
        _db.Commit();
    }
    public void RelateUsersToCompanies(IEnumerable<Tuple<Guid, Guid>> relations) {
        _db.BeginTrans();
        foreach (var rel in relations) {
            var user = _usersCollection.FindById(rel.Item1);
            user.Company = _companiesCollection.FindById(rel.Item2);
            _usersCollection.Update(user);
        }
        _db.Commit();
    }
    public TestUser[] GetAllUsers() {
        return _usersCollection.FindAll().ToArray();
    }

    public TestUser? GetUserById(Guid id) {
        return _db.GetCollection<TestUser>("test_user").FindById(id);
    }
    public void UpdateUserAge(Guid userId, int newAge) {
        var user = _usersCollection.FindById(userId);
        if (user != null) {
            user.Age = newAge;
            _usersCollection.Update(user);
        }
    }
    public TestUser[] GetUserAtAge(int age) {
        return _usersCollection.Find(u => u.Age == age).ToArray();
    }
    public void DeleteUsers(int age) {
        _usersCollection.DeleteMany(u => u.Age == age);
    }
    public void FlushToDisk() {
        _db.Checkpoint();
    }
    public int CountUsersOfAge(int age) {
        return _usersCollection.Count(u => u.Age == age);
    }
    public void Close() {
        _db.Dispose();
    }
}
