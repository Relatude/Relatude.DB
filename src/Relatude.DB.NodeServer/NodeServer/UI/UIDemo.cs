using Relatude.DB.Demo;
using Relatude.DB.Demo.Models;
using Relatude.DB.NodeServer.API;
using Relatude.DB.Nodes;
using System.Diagnostics;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// Demo content: generated <see cref="DemoArticle"/> nodes, so an empty database has something to
/// search, facet and page through. The same two generators the old admin UI offered - random words,
/// or real articles read from a wikipedia dump when one happens to be on the machine.
///
/// It only works on a database whose datamodel actually has the demo node type (the "Demo" datamodel
/// source, which is what a default installation starts with); on any other model there is nothing to
/// insert, and saying so is more useful than an insert that fails on an unknown type.
///
/// Generating runs as a polled background job for the same reason the file scans do: a million
/// articles is far longer than a request should be, and the caller wants progress and a way out.
/// </summary>
sealed class UIDemo {
    // one transaction per chunk: big enough that the per-transaction cost disappears, small enough
    // that cancelling is quick and progress moves
    const int chunkSize = 1000;
    const int maxCount = 10_000_000;
    const string jobKind = "demo content";

    // A developer machine convenience, the way it was in the old UI: the wikipedia dump is not
    // shipped with anything, so it is offered only when it is actually lying at this path.
    static string wikipediaPath =>
        Environment.OSVersion.Platform is PlatformID.Unix or PlatformID.MacOSX
            ? "/mnt/c/WAF_Sources/wikipedia/wiki-articles.json" // the same folder seen from WSL
            : @"C:\WAF_Sources\wikipedia\wiki-articles.json";

    readonly RelatudeDBServer _server;
    internal UIDemo(RelatudeDBServer server) => _server = server;

    internal void Register(UICommands commands) {
        commands.Register("demo-info", ctx => info(ctx.Payload<DemoInfoPayload>()));
        commands.Register("demo-start", ctx => start(ctx.Payload<DemoStartPayload>()));
        commands.Register("demo-progress", ctx => progress(ctx.Payload<DemoJobPayload>()));
        commands.Register("demo-cancel", ctx => cancel(ctx.Payload<DemoJobPayload>()));
    }

    object info(DemoInfoPayload p) {
        var c = container(p.StoreId);
        var store = c.IsOpen() ? c.Store : null;
        var available = store != null && hasDemoType(store);
        return new {
            Open = store != null,
            Available = available,
            NodeType = typeof(DemoArticle).FullName,
            // what is already there: the generators continue from it, so the panel says where a run starts
            Existing = available ? store!.Count<DemoArticle>() : 0,
            Wikipedia = File.Exists(wikipediaPath),
            WikipediaPath = wikipediaPath,
        };
    }

    object start(DemoStartPayload p) {
        var store = openStore(p.StoreId);
        if (!hasDemoType(store)) throw new Exception(noDemoTypeMessage);
        var count = p.Count;
        if (count < 1) throw new Exception("Nothing to create. ");
        if (count > maxCount) throw new Exception("At most " + maxCount.ToString("N0") + " articles per run. ");
        if (p.Wikipedia && !File.Exists(wikipediaPath)) throw new Exception("No wikipedia article file at " + wikipediaPath + ". ");
        var wikipedia = p.Wikipedia;
        var job = FileScanJobs.Start(p.StoreId, jobKind, j => generate(store, count, wikipedia, j));
        return new { JobId = job.Id };
    }

    object progress(DemoJobPayload p) {
        var job = FileScanJobs.Get(p.JobId);
        return new {
            job.State,
            job.Description,
            job.Percent,
            job.Error,
            Result = job.Result as DemoResult,
        };
    }

    object cancel(DemoJobPayload p) {
        FileScanJobs.Get(p.JobId).Cancellation.Cancel();
        return new { Cancelled = true };
    }

    // ---- the run ----

    /// <summary>
    /// Inserts <paramref name="count"/> generated articles in chunks. The generator is moved past the
    /// articles already in the database first, so a second run adds new ones rather than repeating the
    /// first: with the random generator that is a reseed, with the wikipedia file it is a read forward,
    /// which on a database with many articles already is itself worth showing progress for.
    /// A cancelled run keeps what it has inserted; the panel reads the new count afterwards.
    /// </summary>
    static async Task<object> generate(NodeStore store, int count, bool wikipedia, FileScanJob job) {
        var watch = Stopwatch.StartNew();
        var existing = (int)Math.Min(int.MaxValue, store.Count<DemoArticle>());
        using IArticleGenerator generator = wikipedia ? new WikipediaArticleGenerator(wikipediaPath) : new RandomArticleGenerator(0);
        if (existing > 0) {
            job.SetProgress("Skipping the " + existing.ToString("N0") + " articles already stored…", 0);
            generator.Move(existing);
        }
        var created = 0;
        while (created < count) {
            job.Cancellation.Token.ThrowIfCancellationRequested();
            var take = Math.Min(chunkSize, count - created);
            var articles = generator.Many(take);
            // bulk: the articles leave the node cache once written, so a large run does not end with
            // the whole data set resident in memory
            await store.BulkInsertAsync(articles);
            created += take;
            job.SetProgress(created.ToString("N0") + " of " + count.ToString("N0") + " articles", (int)(100L * created / count));
        }
        return new DemoResult(created, watch.Elapsed.TotalMilliseconds);
    }

    // ---- pieces ----

    const string noDemoTypeMessage = "The datamodel of this database has no Relatude.DB.Demo.Models.DemoArticle node type, so there is nothing to fill in. "
        + "Add the demo datamodel source (assembly reference Relatude.DB.NodeStore, namespace Relatude.DB.Demo.Models) to use demo content. ";

    static bool hasDemoType(NodeStore store) {
        var fullName = typeof(DemoArticle).FullName;
        return store.Datamodel.NodeTypes.Values.Any(t => t.FullName == fullName);
    }

    NodeStoreContainer container(Guid storeId) {
        if (!_server.Containers.TryGetValue(storeId, out var c)) throw new Exception("Database not found. ");
        return c;
    }

    NodeStore openStore(Guid storeId) {
        var c = container(storeId);
        if (!c.IsOpen()) throw new Exception("The database must be open. ");
        return c.Store!;
    }

    sealed record DemoResult(int Created, double ElapsedMs);
    sealed record DemoInfoPayload(Guid StoreId);
    sealed record DemoStartPayload(Guid StoreId, int Count, bool Wikipedia);
    sealed record DemoJobPayload(Guid JobId);
}
