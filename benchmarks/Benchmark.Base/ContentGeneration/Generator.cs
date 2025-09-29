using Benchmark.Base.Models;

namespace Benchmark.Base.ContentGeneration;
public class Generator(int seed = 0) {
    TextGenerator _textGenerator = new(seed);
    public TestUser[] CreateUsers(int count) {
        Random random = new(seed);
        TestUser[] users = new TestUser[count];
        for (int i = 0; i < count; i++) {
            users[i] = new TestUser {
                Id = Guid.NewGuid(),
                Name = _textGenerator.GenerateTitle(10 + random.Next(20))
            };
        }
        return users;
    }
    public TestCompany[] CreateCompanies(int count, TestUser[] users) {
        Random random = new(seed);
        TestCompany[] companies = new TestCompany[count];
        for (int i = 0; i < count; i++) {
            var companyUsersCount = random.Next(1, Math.Max(2, users.Length / count));
            var companyUsers = new HashSet<TestUser>();
            while (companyUsers.Count < companyUsersCount) {
                companyUsers.Add(users[random.Next(users.Length)]);
            }
            var company = new TestCompany {
                Id = Guid.NewGuid(),
                Name = _textGenerator.GenerateTitle(10 + random.Next(20)),
                Users = companyUsers.ToArray()
            };
            foreach (var user in company.Users) {
                user.Company = company;
            }
            companies[i] = company;
        }
        return companies;
    }
    public TestDocument[] CreateDocuments(int count, TestUser[] users) {
        Random random = new(seed);
        TestDocument[] documents = new TestDocument[count];
        for (int i = 0; i < count; i++) {
            var author = users[random.Next(users.Length)];
            var titleLength = 10 + random.Next(50);
            var contentLength = 100 + random.Next(5000);
            documents[i] = new TestDocument {
                Id = Guid.NewGuid(),
                Title = _textGenerator.GenerateTitle(titleLength),
                Content = _textGenerator.GenerateText(contentLength),
                Author = author
            };
        }
        return documents;
    }
}
