using Benchmark.Base.Models;
using Benchmark.Base.Operations;
using Microsoft.Data.Sqlite;

namespace Benchmark.SQLite;
public class SQLiteDBTester : ITester {
    string _dataFolderPath = null!;
    SqliteConnection _connection = null!;
    public void Initalize(string dataFolderPath) {
        _dataFolderPath = dataFolderPath;
    }
    public void Open() {
        var dbFileName = "sqlite.db";
        if (!Directory.Exists(_dataFolderPath)) Directory.CreateDirectory(_dataFolderPath);
        var dbPath = Path.Combine(_dataFolderPath, dbFileName);
        var cnnStr = "Data Source=" + dbPath;
        _connection = new SqliteConnection(cnnStr);
        _connection.Open();
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL";
        cmd.ExecuteNonQuery();
    }

    void executeCommand(string sql) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
    public void CreateSchema() {
        executeCommand("CREATE TABLE test_user (id TEXT PRIMARY KEY, name TEXT, age INTEGER, company_id TEXT)");
        executeCommand("CREATE INDEX test_user_company_id ON test_user(company_id)");
        executeCommand("CREATE TABLE test_company (id TEXT PRIMARY KEY, name TEXT)");
        executeCommand("CREATE TABLE test_document (id TEXT PRIMARY KEY, title TEXT, content TEXT, author_id TEXT)");
        executeCommand("CREATE INDEX test_document_author_id ON test_document(author_id)");
    }
    public void InsertUsers(TestUser[] users) {
        var transaction = _connection.BeginTransaction();
        foreach (var user in users) {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO test_user(id, name, age) VALUES(@id, @name, @age)";
            cmd.Parameters.AddWithValue("@id", user.Id);
            cmd.Parameters.AddWithValue("@name", user.Name);
            cmd.Parameters.AddWithValue("@age", user.Age);
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public void InsertCompanies(TestCompany[] companies) {
        var transaction = _connection.BeginTransaction();
        foreach (var user in companies) {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO test_company(id, name) VALUES(@id, @name)";
            cmd.Parameters.AddWithValue("@id", user.Id);
            cmd.Parameters.AddWithValue("@name", user.Name);
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public void InsertDocuments(TestDocument[] documents) {
        var transaction = _connection.BeginTransaction();
        foreach (var user in documents) {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO test_document(id, title, content) VALUES(@id, @title, @content)";
            cmd.Parameters.AddWithValue("@id", user.Id);
            cmd.Parameters.AddWithValue("@title", user.Title);
            cmd.Parameters.AddWithValue("@content", user.Content);
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public void RelateUsersToCompanies(IEnumerable<Tuple<Guid, Guid>> relations) {
        var transaction = _connection.BeginTransaction();
        foreach (var relation in relations) {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE test_user SET company_id=@company_id WHERE id=@id";
            cmd.Parameters.AddWithValue("@id", relation.Item1);
            cmd.Parameters.AddWithValue("@company_id", relation.Item2);
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public void RelateDocumentsToUsers(IEnumerable<Tuple<Guid, Guid>> relations) {
        var transaction = _connection.BeginTransaction();
        foreach (var relation in relations) {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE test_document SET author_id=@author_id WHERE id=@id";
            cmd.Parameters.AddWithValue("@id", relation.Item1);
            cmd.Parameters.AddWithValue("@author_id", relation.Item2);
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public void DeleteUsers(int age) {
        throw new NotImplementedException();
    }
    public TestUser[] GetAllUsers() {
        throw new NotImplementedException();
    }
    public TestUser? GetUserById(Guid id) {
        throw new NotImplementedException();
    }
    public TestUser[] SearchUsersWithDocuments(int age) {
        throw new NotImplementedException();
    }
    public void Close() {
        _connection.Close();
        _connection.Dispose();
    }
    public void DeleteDataFiles() {
        if (Directory.Exists(_dataFolderPath)) Directory.Delete(_dataFolderPath, true);
    }
}
