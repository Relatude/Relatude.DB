using Relatude.DB.Nodes;

namespace Relatude.DB.ContentApi.Demo;

public static class Seeder {
    public static void SeedIfEmpty(NodeStore store) {
        if (store.Count<Article>() > 0) return;

        var ada = new Author { Id = Guid.NewGuid(), Name = "Ada Lovelace", Email = "ada@example.com", Bio = "Mathematician and writer, the first computer programmer." };
        var alan = new Author { Id = Guid.NewGuid(), Name = "Alan Turing", Email = "alan@example.com", Bio = "Father of theoretical computer science and artificial intelligence." };
        var grace = new Author { Id = Guid.NewGuid(), Name = "Grace Hopper", Email = "grace@example.com", Bio = "Pioneer of compiler design and COBOL." };

        var news = new Category { Id = Guid.NewGuid(), Name = "News", Description = "Product and company news." };
        var engineering = new Category { Id = Guid.NewGuid(), Name = "Engineering", Description = "Deep dives into database internals." };
        var tutorials = new Category { Id = Guid.NewGuid(), Name = "Tutorials", Description = "Step by step guides." };

        var tagDb = new Tag { Id = Guid.NewGuid(), Name = "database" };
        var tagPerf = new Tag { Id = Guid.NewGuid(), Name = "performance" };
        var tagCsharp = new Tag { Id = Guid.NewGuid(), Name = "csharp" };
        var tagSearch = new Tag { Id = Guid.NewGuid(), Name = "search" };

        store.Insert(new object[] { ada, alan, grace, news, engineering, tutorials, tagDb, tagPerf, tagCsharp, tagSearch });

        var articles = new (Article Article, Author Author, Category Category, Tag[] Tags)[] {
            (new Article {
                Id = Guid.NewGuid(),
                Title = "Introducing Relatude.DB",
                Summary = "A general purpose C# native object database with relations, full text search and file storage.",
                Body = "Relatude.DB stores plain C# objects as nodes in a graph. Relations are first class citizens, queries are strongly typed, and a built-in text index gives instant full text search across all content.",
                PublishedUtc = new DateTime(2026, 1, 15, 9, 0, 0, DateTimeKind.Utc),
                IsPublished = true, ViewCount = 1532,
            }, ada, news, new[] { tagDb, tagCsharp }),
            (new Article {
                Id = Guid.NewGuid(),
                Title = "How the transaction log keeps your data safe",
                Summary = "A look at the append-only log and crash recovery.",
                Body = "Every transaction is appended to a durable log before it is acknowledged. On startup the store replays the log to rebuild indexes, which means a crash never loses committed data.",
                PublishedUtc = new DateTime(2026, 2, 2, 12, 30, 0, DateTimeKind.Utc),
                IsPublished = true, ViewCount = 847,
            }, alan, engineering, new[] { tagDb, tagPerf }),
            (new Article {
                Id = Guid.NewGuid(),
                Title = "Full text search in three lines of code",
                Summary = "Use WhereSearch and Search to query any indexed property.",
                Body = "Call Query<T>().WhereSearch(\"term\") to filter, or Search(\"term\") to get ranked hits with highlighted text samples. Semantic search can be blended in with a single parameter.",
                PublishedUtc = new DateTime(2026, 3, 10, 8, 0, 0, DateTimeKind.Utc),
                IsPublished = true, ViewCount = 2210,
            }, grace, tutorials, new[] { tagSearch, tagCsharp }),
            (new Article {
                Id = Guid.NewGuid(),
                Title = "Modelling relations with plain C# properties",
                Summary = "One-to-many and many-to-many relations from navigation properties.",
                Body = "Declare a property of another node type for a one-relation, or an array for a many-relation. The datamodel builder pairs the two sides automatically and keeps both in sync.",
                PublishedUtc = new DateTime(2026, 4, 5, 14, 0, 0, DateTimeKind.Utc),
                IsPublished = false, ViewCount = 95,
            }, ada, tutorials, new[] { tagDb, tagCsharp }),
            (new Article {
                Id = Guid.NewGuid(),
                Title = "Benchmarking the node cache",
                Summary = "Millions of reads per second from the in-memory node cache.",
                Body = "The node cache keeps hot nodes deserialized in memory. This article measures read throughput with different cache sizes and shows how the cache interacts with the set cache for query results.",
                PublishedUtc = new DateTime(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc),
                IsPublished = true, ViewCount = 612,
            }, alan, engineering, new[] { tagPerf }),
        };

        var tagsPropertyId = store.Datastore.Datamodel
            .NodeTypesByFullName[typeof(Article).FullName!].AllPropertyIdsByName[nameof(Article.Tags)];
        foreach (var (article, author, category, tags) in articles) {
            store.Insert(article);
            store.SetRelation<Article>(article.Id, a => a.Author!, author.Id);
            store.SetRelation<Article>(article.Id, a => a.Category!, category.Id);
            var transaction = store.CreateTransaction();
            foreach (var tag in tags) transaction.Relate(article.Id, tagsPropertyId, tag.Id);
            transaction.Execute();
        }
    }
}
