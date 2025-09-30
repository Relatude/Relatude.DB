using Benchmark.Base.Models;

namespace Benchmark.Base;

public interface ITester {
    void Initalize(string dataFolderPath);
    string Name { get; }
    void CreateSchema();
    void Open();
    void DeleteDataFiles();
    void InsertUsers(TestUser[] users);
    void InsertCompanies(TestCompany[] companies);
    void InsertDocuments(TestDocument[] documents);
    void RelateUsersToCompanies(IEnumerable<Tuple<Guid, Guid>> relations);
    void RelateDocumentsToUsers(IEnumerable< Tuple<Guid,Guid>> relations);
    void FlushToDisk();
    TestUser? GetUserById(Guid id);
    TestUser[] GetAllUsers();
    TestUser[] SearchUsersWithDocuments(int age);
    void DeleteUsers(int age);
    void Close();
}
