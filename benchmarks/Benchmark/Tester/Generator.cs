using Benchmark.Base.Models;
using Benchmark.Tester;

namespace Benchmark.Base.ContentGeneration;
public static class Generator {
    public static TestData Generate(TestOptions options) {
        TextGenerator textGenerator = new(options.GenerationSeed);
        TestData data = new ();
        data.Users = createUsers(options.UserCount, options.GenerationSeed, textGenerator);
        data.Companies = createCompanies(options.CompanyCount, data.Users, options.GenerationSeed, textGenerator);
        data.Documents = createDocuments(options.DocumentCount, data.Users, options.GenerationSeed, textGenerator);
        return data;    
    }
    static TestUser[] createUsers(int count, int seed, TextGenerator textGenerator) {
        Random random = new(seed);
        TestUser[] users = new TestUser[count];
        for (int i = 0; i < count; i++) {
            users[i] = new TestUser {
                Id = Guid.NewGuid(),
                Name = textGenerator.GenerateTitle(10 + random.Next(20))
            };
        }
        return users;
    }
    static TestCompany[] createCompanies(int count, TestUser[] users, int seed, TextGenerator textGenerator) {
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
                Name = textGenerator.GenerateTitle(10 + random.Next(20)),
                Users = companyUsers.ToArray()
            };
            foreach (var user in company.Users) {
                user.Company = company;
            }
            companies[i] = company;
        }
        return companies;
    }
    static TestDocument[] createDocuments(int count, TestUser[] users, int seed, TextGenerator textGenerator) {
        Random random = new(seed);
        TestDocument[] documents = new TestDocument[count];
        for (int i = 0; i < count; i++) {
            var author = users[random.Next(users.Length)];
            var titleLength = 10 + random.Next(50);
            var contentLength = 100 + random.Next(5000);
            documents[i] = new TestDocument {
                Id = Guid.NewGuid(),
                Title = textGenerator.GenerateTitle(titleLength),
                Content = textGenerator.GenerateText(contentLength),
                Author = author
            };
        }
        return documents;
    }
}
