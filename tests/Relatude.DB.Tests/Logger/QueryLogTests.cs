using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.Query;
using Relatude.Utils;

namespace Relatude.Logger;
/// <summary>The query log of a database: what a query leaves in it, and when.</summary>
[TestClass]
public class QueryLogTests {
    static readonly TimeSpan _long = TimeSpan.FromSeconds(20);
    static LogEntry[] recorded(IStoreLogger logger, out int total)
        => logger.ExtractLog("query", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1), 0, 10, true, out total);

    [TestMethod]
    public void AQueryIsRecordedWithWhatItFound() {
        var store = Helper.Open(io: new IOProviderMemory());
        var logger = store.Datastore.Logger;
        logger.EnableLog("query", true);
        store.Insert(new Article { Name = "a" });
        store.Insert(new Article { Name = "b" });
        Assert.AreEqual(2, store.Query<Article>().ToList().Count);
        var entry = recorded(logger, out var total).Single();
        Assert.AreEqual(1, total);
        Assert.AreEqual(2, entry.Values["resultCount"]);
        StringAssert.Contains((string)entry.Values["query"], "Article");
        Assert.IsTrue((double)entry.Values["duration"] >= 0);
        store.Dispose();
    }

    [TestMethod]
    public void AQueryRecordsItsEntryAfterLettingGoOfTheReadLock() {
        // A writer waiting for the database's lock holds up every reader that comes after it, so a
        // query that waited for its log entry inside the read lock would keep the database waiting.
        var io = new GatedIOProvider(new IOProviderMemory(), FileKeyUtility.Logger_NamePrefix("query", FileInterval.Day));
        var store = Helper.Open(io: io);
        var logger = store.Datastore.Logger;
        logger.EnableLog("query", true);
        store.Insert(new Article { Name = "a" });
        store.Query<Article>().Count(); // an entry on disk, for the page below to read
        logger.LogStore.GetLogFileSize("query");
        io.Close();
        Task page;
        Task<int> query;
        try {
            // a page of the query log being read, and its backlog full: the next entry waits for the page
            page = Task.Run(() => recorded(logger, out _));
            Assert.IsTrue(io.ReadWaiting.Wait(_long), "the page never came to its file");
            for (var i = 0; i < Log.MaxBacklog; i++) logger.RecordQuery("filler", TimeSpan.Zero, 0, new Metrics());
            query = Task.Factory.StartNew(() => store.Query<Article>().Count(), TaskCreationOptions.LongRunning);
            Assert.IsFalse(query.Wait(TimeSpan.FromMilliseconds(500)), "the query waits to record its entry");
            var insert = Task.Run(() => store.Insert(new Article { Name = "b" }));
            Assert.IsTrue(insert.Wait(_long), "a writer waited for a query recording its log entry");
            Assert.IsFalse(query.IsCompleted, "the writer got in while the query was still waiting for the log");
        } finally {
            io.Open();
        }
        Assert.IsTrue(page.Wait(_long));
        Assert.IsTrue(query.Wait(_long));
        Assert.AreEqual(1, query.Result, "the query had read the database before the writer came");
        Assert.AreEqual(2, store.Query<Article>().Count());
        store.Dispose();
    }
}
