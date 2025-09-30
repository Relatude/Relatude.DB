using Benchmark.Base;
using Benchmark.Base.Models;
using Microsoft.Data.SqlClient;

namespace Benchmark.MSSql;
public class MsSqlDBTester : ITester {
    string _dataFolderPath = null!;
    SqlConnection _connection;
    string _cnnStr = null!;
    public string Name => "MsSql";
    public void Initalize(string dataFolderPath) {
        _dataFolderPath = dataFolderPath;
        var dbFileName = "mssql.db";
        if (!Directory.Exists(_dataFolderPath)) Directory.CreateDirectory(_dataFolderPath);
        var dbPath = Path.Combine(_dataFolderPath, dbFileName);

        // connection string direct to localdb instance:
        _cnnStr = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;Initial Catalog=BenchmarkDB;";

        using (var connection = new SqlConnection(_cnnStr)) {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"IF DB_ID('BenchmarkDB') IS NULL CREATE DATABASE BenchmarkDB";
            cmd.ExecuteNonQuery();
        }
    }
    public void Open() {        
        _connection = new SqlConnection(_cnnStr);
        _connection.Open();
    }
    void executeCommand(string sql) {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
    public void CreateSchema() {
        executeCommand("IF OBJECT_ID('test_user', 'U') IS NULL CREATE TABLE test_user (id UNIQUEIDENTIFIER PRIMARY KEY, name NVARCHAR(100), age INT, company_id UNIQUEIDENTIFIER)");
        //executeCommand("IF OBJECT_ID('test_user_company_id', 'I') IS NULL CREATE INDEX test_user_company_id ON test_user(company_id)");
        executeCommand("IF OBJECT_ID('test_company', 'U') IS NULL CREATE TABLE test_company (id UNIQUEIDENTIFIER PRIMARY KEY, name NVARCHAR(100))");
        executeCommand("IF OBJECT_ID('test_document', 'U') IS NULL CREATE TABLE test_document (id UNIQUEIDENTIFIER PRIMARY KEY, title NVARCHAR(200), content NVARCHAR(MAX), author_id UNIQUEIDENTIFIER)");
        //executeCommand("IF OBJECT_ID('test_document_author_id', 'I') IS NULL CREATE INDEX test_document_author_id ON test_document(author_id)");
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
            //using var cmd = _connection.CreateCommand();
            //cmd.CommandText = "INSERT INTO test_user(id, name, age) VALUES(@id, @name, @age)";
            //cmd.Parameters.AddWithValue("@id", user.Id);
            //cmd.Parameters.AddWithValue("@name", user.Name);
            //cmd.Parameters.AddWithValue("@age", user.Age);
            //cmd.ExecuteNonQuery();
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

    public void FlushToDisk() {
        // SQLite auto-commits transactions, so no explicit flush is needed.
    }
}
