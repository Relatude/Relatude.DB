using Benchmark.Base.Models;
using Benchmark.Base.Operations;

namespace Benchmark.Relatude.DB {
    public class RelatudeDBTester : ITester {
        void ITester.Close() {
            throw new NotImplementedException();
        }

        void ITester.CreateSchema() {
            throw new NotImplementedException();
        }

        void ITester.DeleteUsers(int age) {
            throw new NotImplementedException();
        }

        TestUser[] ITester.GetAllUsers() {
            throw new NotImplementedException();
        }

        TestUser? ITester.GetUserById(Guid id) {
            throw new NotImplementedException();
        }

        void ITester.InsertCompanies(TestCompany[] companies) {
            throw new NotImplementedException();
        }

        void ITester.InsertDocuments(TestDocument[] documents) {
            throw new NotImplementedException();
        }

        void ITester.InsertUsers(TestUser[] users) {
            throw new NotImplementedException();
        }

        void ITester.Open() {
            throw new NotImplementedException();
        }

        void ITester.RelateDocumentsToUsers((Guid documentId, Guid userId)[] relations) {
            throw new NotImplementedException();
        }

        void ITester.RelateUsersToCompanies((Guid userId, Guid companyId)[] relations) {
            throw new NotImplementedException();
        }

        void ITester.Reset() {
            throw new NotImplementedException();
        }

        TestUser[] ITester.SearchUsersWithDocuments(int age) {
            throw new NotImplementedException();
        }
    }
}
