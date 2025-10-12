using Benchmark.Base.Models;

namespace Benchmark.Base;

public interface ITester {
    void Initalize(string dataFolderPath, TestOptions options);
    string Name { get; }
    void CreateSchema();
    void Open();
    void DeleteDataFiles();
    void InsertUsers(TestUser[] users);
    void InsertCompanies(TestCompany[] companies);
    void InsertDocuments(TestDocument[] documents);
    void RelateUsersToCompanies(IEnumerable<Tuple<Guid, Guid>> relations);
    void RelateDocumentsToUsers(IEnumerable<Tuple<Guid, Guid>> relations);
    void FlushToDisk();
    TestUser? GetUserById(Guid id);
    TestUser[] GetAllUsers();
    int CountUsersOfAge(int age);
    void UpdateUserAge(Guid userId, int newAge);
    TestUser[] GetUsersAtAge(int age);
    void DeleteUsersOfAge(int age);
    void Close();
}

public static class ITesterExtensions {
    public static void UpdateAndGetUsers(this ITester tester, Guid userId, int age) {
        tester.UpdateUserAge(userId, age + 1);
        tester.GetUsersAtAge(age);
    }
}