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
    }
    public void CreateSchema() {
    }
    public void DeleteDataFiles() {
        if (Directory.Exists(_path)) Directory.Delete(_path, true);
    }
    public void InsertUsers(TestUser[] users) {
        foreach (var user in users) {
            var company = user.Company;
            user.Company = null;
            _usersCollection.Insert(user);
            user.Company = company;
        }
    }
    public void InsertCompanies(TestCompany[] companies) {
        foreach (var company in companies) {
            var users = company.Users;
            company.Users = [];
            _companiesCollection.Insert(company);
            company.Users = users;
        }
    }
    public void InsertDocuments(TestDocument[] documents) {
        foreach (var document in documents) {
            var author = document.Author;
            document.Author = null;
            _documentsCollection.Insert(document);
            document.Author = author;
        }
    }
    public void RelateDocumentsToUsers(IEnumerable<Tuple<Guid, Guid>> relations) {

    }
    public void RelateUsersToCompanies(IEnumerable<Tuple<Guid, Guid>> relations) {

    }
    public TestUser[] GetAllUsers() {
        return [];
    }

    public TestUser? GetUserById(Guid id) {
        return _db.GetCollection<TestUser>("test_user").FindById(id);
    }
    public void DeleteUsers(int age) {
    }
    public void FlushToDisk() {
        _db.Checkpoint();
    }
    public TestUser[] SearchUsersWithDocuments(int age) {
        throw new NotImplementedException();
    }
    public void Close() {
        _db.Dispose();
    }
}
