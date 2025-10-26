using Benchmark.Base;
using Benchmark.Base.Models;
using Microsoft.Data.Sqlite;

namespace Benchmark.SQLite;
public class SQLiteDBTester : ITester {
    string _dataFolderPath = null!;
    SqliteConnection _connection = null!;
    public string Name => "SQLite";
    public void Initalize(string dataFolderPath, TestOptions options) {
        _dataFolderPath = dataFolderPath;
    }
    public void Open() {
        var dbFileName = Guid.NewGuid() + "sqlite.db"; // unique file per test run, as old may be locked and not deletable
        if (!Directory.Exists(_dataFolderPath)) Directory.CreateDirectory(_dataFolderPath);
        var dbPath = Path.Combine(_dataFolderPath, dbFileName);


        //dbPath = ":memory:"; // memory connection string:
        var cnnStr = "Data Source=" + dbPath;
        _connection = new SqliteConnection(cnnStr);
        _connection.Open();
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL";
        cmd.ExecuteNonQuery();
        // ensuring full diskflush for every transaction commit:
        cmd.CommandText = "PRAGMA synchronous=FULL";
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
        executeCommand("CREATE INDEX test_user_age ON test_user(age)");
        executeCommand("CREATE TABLE test_company (id TEXT PRIMARY KEY, name TEXT)");
        executeCommand("CREATE TABLE test_document (id TEXT PRIMARY KEY, title TEXT, content TEXT, author_id TEXT)");
        executeCommand("CREATE INDEX test_document_author_id ON test_document(author_id)");
    }
    public void InsertUsers(TestUser[] users) {
        var transaction = _connection.BeginTransaction();
        foreach (var user in users) {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
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
            cmd.Transaction = transaction;
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
            cmd.Transaction = transaction;
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
            cmd.Transaction = transaction;
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
            cmd.Transaction = transaction;
            cmd.CommandText = "UPDATE test_document SET author_id=@author_id WHERE id=@id";
            cmd.Parameters.AddWithValue("@id", relation.Item1);
            cmd.Parameters.AddWithValue("@author_id", relation.Item2);
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public void DeleteUsersOfAge(int age) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM test_user WHERE age=@age";
        cmd.Parameters.AddWithValue("@age", age);
        cmd.ExecuteNonQuery();
    }
    public TestUser[] GetAllUsers() {
        var users = new List<TestUser>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, age FROM test_user";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) {
            users.Add(new TestUser {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1),
                Age = reader.GetInt32(2),
            });
        }
        return users.ToArray();
    }
    public TestUser? GetUserById(Guid id) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, age FROM test_user WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        using var reader = cmd.ExecuteReader();
        if (reader.Read()) {
            return new TestUser {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1),
                Age = reader.GetInt32(2),
            };
        }
        return null;
    }
    public TestUser[] GetUsersAtAge(int age) {
        lock (this) // SQLite connection is not thread-safe
         {
            var users = new List<TestUser>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id, name, age FROM test_user WHERE age=@age";
            cmd.Parameters.AddWithValue("@age", age);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) {
                users.Add(new TestUser {
                    Id = Guid.Parse(reader.GetString(0)),
                    Name = reader.GetString(1),
                    Age = reader.GetInt32(2),
                });
            }
            return users.ToArray();
        }
    }
    public int CountUsersOfAge(int age) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM test_user WHERE age == @age";
        cmd.Parameters.AddWithValue("@age", age);
        var result = cmd.ExecuteScalar();
        return Convert.ToInt32(result);
    }
    public void UpdateUserAge(Guid userId, int newAge) {
        lock (this) // SQLite connection is not thread-safe
         {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE test_user SET age=@age WHERE id=@id";
            cmd.Parameters.AddWithValue("@id", userId.ToString());
            cmd.Parameters.AddWithValue("@age", newAge);
            cmd.ExecuteNonQuery();
        }
    }

    public void Close() {
        _connection.Close();
        _connection.Dispose();
    }
    public void DeleteDataFiles() {
        try { // swallow any exceptions as Sqlite may still have locks on files
            if (Directory.Exists(_dataFolderPath)) Directory.Delete(_dataFolderPath, true);
        } catch { }
    }

    public void FlushToDisk() {
        // SQLite auto-commits transactions, so no explicit flush is needed.
    }
}
