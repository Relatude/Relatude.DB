using Benchmark.Base.Models;

namespace Benchmark.Base.Operations;

public interface ITester {
    void Open();
    void Reset();
    void CreateSchema();
    void InsertUsers(TestUser[] users);
    void InsertCompanies(TestCompany[] companies);
    void InsertDocuments(TestDocument[] documents);
    void RelateUsersToCompanies((Guid userId, Guid companyId)[] relations);
    void RelateDocumentsToUsers((Guid documentId, Guid userId)[] relations);
    TestUser? GetUserById(Guid id);
    TestUser[] GetAllUsers();
    TestUser[] SearchUsersWithDocuments(int age);
    void DeleteUsers(int age);
    void Close();
}
