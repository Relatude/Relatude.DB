using Benchmark.Base.Models;

namespace Benchmark.Base.ContentGeneration;
public static class Generator {
    public static TestData Generate(TestOptions options) {
        TextGenerator textGenerator = new(options.GenerationSeed);
        TestData data = new ();
        data.Users = createUsers(options.UserCount, options.GenerationSeed, textGenerator);
        data.Companies = createCompanies(options.CompanyCount, options.GenerationSeed, textGenerator);
        data.Documents = createDocuments(options.DocumentCount, options.GenerationSeed, textGenerator);
        Random random = new(options.GenerationSeed);
        foreach (var doc in data.Documents) {
            var user = data.Users[random.Next(data.Users.Length)];
            data.DocsToUsers.Add(new(doc.Id, user.Id));
        }
        foreach (var user in data.Users) {
            var company = data.Companies[random.Next(data.Companies.Length)];
            data.UsersToCompany.Add(new(user.Id, company.Id));
        }
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
    static TestCompany[] createCompanies(int count, int seed, TextGenerator textGenerator) {
        Random random = new(seed);
        TestCompany[] companies = new TestCompany[count];
        for (int i = 0; i < count; i++) {
            var company = new TestCompany {
                Id = Guid.NewGuid(),
                Name = textGenerator.GenerateTitle(10 + random.Next(20)),
            };
            companies[i] = company;
        }
        return companies;
    }
    static TestDocument[] createDocuments(int count, int seed, TextGenerator textGenerator) {
        Random random = new(seed);
        TestDocument[] documents = new TestDocument[count];
        for (int i = 0; i < count; i++) {
            var titleLength = 10 + random.Next(50);
            var contentLength = 100 + random.Next(5000);
            documents[i] = new TestDocument {
                Id = Guid.NewGuid(),
                Title = textGenerator.GenerateTitle(titleLength),
                Content = textGenerator.GenerateText(contentLength),
            };
        }
        return documents;
    }
}
