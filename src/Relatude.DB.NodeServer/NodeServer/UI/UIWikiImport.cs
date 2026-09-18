using Relatude.DB.Demo.Models;
using Relatude.DB.Demo.Wikipedia;
using Relatude.DB.NodeServer.API;
using Relatude.DB.Nodes;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// Imports a real Wikipedia corpus into the demo datamodel: the JSONL the Wikipedia Corpus Builder
/// writes, with the picture bytes taken from the .wikimg image bundle it writes beside it.
///
/// This is the other half of <see cref="UIDemo"/>. Generated articles make an empty database
/// searchable; a real corpus makes it interesting - seven million articles with sections,
/// categories, predicted topics, coordinates, popularity and photographs, which is a data set that
/// exercises full text search, semantic search, facets, relations, geo queries and the file store
/// at the same time, and at a scale worth measuring.
///
/// Neither file ships with anything and the corpus runs to tens of gigabytes, so the panel asks the
/// server what is on disk before offering anything, and the paths are the user's to give. The run is a
/// polled background job for the same reason the file scans are: importing even a hundred thousand
/// articles outlives any request, and the caller wants progress and a way out.
/// </summary>
sealed class UIWikiImport {
    const string jobKind = "wikipedia import";
    const int maxCount = 10_000_000;

    // Where a corpus tends to end up. Only offered when the files are actually there; the panel
    // takes a typed path just as happily.
    static string[] likelyFolders => [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "wikicorpus"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop", "wikicorpus"),
        @"C:\wikicorpus",
        "/mnt/c/wikicorpus",
        @"C:\WAF_Sources\wikipedia",
    ];

    readonly RelatudeDBServer _server;
    internal UIWikiImport(RelatudeDBServer server) => _server = server;

    internal void Register(UICommands commands) {
        commands.Register("wiki-info", ctx => info(ctx.Payload<StorePayload>()));
        commands.Register("wiki-browse", ctx => browse(ctx.Payload<BrowsePayload>()));
        commands.Register("wiki-start", ctx => start(ctx.Payload<StartPayload>()));
        commands.Register("wiki-progress", ctx => progress(ctx.Payload<JobPayload>()));
        commands.Register("wiki-cancel", ctx => cancel(ctx.Payload<JobPayload>()));
    }

    object info(StorePayload p) {
        var c = container(p.StoreId);
        var store = c.IsOpen() ? c.Store : null;
        var available = store != null && WikiCorpusImporter.IsSupportedBy(store);
        var found = discover();
        return new {
            Open = store != null,
            Available = available,
            NodeType = typeof(IWikiArticle).FullName,
            Articles = available ? store!.Count<IWikiArticle>() : 0,
            Categories = available ? store!.Count<IWikiCategory>() : 0,
            Topics = available ? store!.Count<IWikiTopic>() : 0,
            Images = available ? store!.Count<IWikiImage>() : 0,
            CorpusPath = found.Corpus,
            CorpusBytes = found.Corpus == null ? 0 : size(found.Corpus),
            BundlePath = found.Bundle,
            BundleBytes = found.Bundle == null ? 0 : size(found.Bundle),
        };
    }

    /// <summary>
    /// What a typed path points at, so the panel can say "1.2 GB corpus" or "no such file" while
    /// the user is still typing rather than only when the run fails.
    /// </summary>
    object browse(BrowsePayload p) {
        var path = (p.Path ?? string.Empty).Trim().Trim('"');
        if (path.Length == 0) return new { Exists = false, Bytes = 0L, Message = string.Empty };
        if (Directory.Exists(path)) {
            var corpus = corpusIn(path);
            var bundle = bundleIn(path);
            return new {
                Exists = corpus != null || bundle != null,
                Bytes = 0L,
                CorpusPath = corpus,
                BundlePath = bundle,
                Message = corpus == null && bundle == null
                    ? "That folder has no .jsonl corpus and no .wikimg image bundle. "
                    : "Folder: " + (corpus == null ? "no corpus" : Path.GetFileName(corpus)) + ", " + (bundle == null ? "no image bundle" : Path.GetFileName(bundle)),
            };
        }
        if (!File.Exists(path)) return new { Exists = false, Bytes = 0L, Message = "No such file. " };
        return new { Exists = true, Bytes = size(path), Message = string.Empty };
    }

    object start(StartPayload p) {
        var store = openStore(p.StoreId);
        if (!WikiCorpusImporter.IsSupportedBy(store)) throw new Exception(noWikiTypeMessage);

        var corpus = (p.CorpusPath ?? string.Empty).Trim().Trim('"');
        if (corpus.Length == 0) throw new Exception("Give the path of the corpus .jsonl file. ");
        if (Directory.Exists(corpus)) corpus = corpusIn(corpus) ?? throw new Exception("That folder has no .jsonl corpus in it. ");
        if (!File.Exists(corpus)) throw new Exception("No corpus file at " + corpus + ". ");

        var bundle = (p.BundlePath ?? string.Empty).Trim().Trim('"');
        if (Directory.Exists(bundle)) bundle = bundleIn(bundle) ?? string.Empty;
        if (p.ImportImages && bundle.Length == 0)
            throw new Exception("Importing pictures needs a .wikimg image bundle to read them from. Build one with the corpus builder's \"bundle\" command. ");
        if (bundle.Length > 0 && !File.Exists(bundle)) throw new Exception("No image bundle at " + bundle + ". ");

        if (p.Count < 1) throw new Exception("Nothing to import. ");
        if (p.Count > maxCount) throw new Exception("At most " + maxCount.ToString("N0") + " articles per run. ");

        var options = new WikiImportOptions {
            CorpusPath = corpus,
            ImageBundlePath = p.ImportImages ? bundle : string.Empty,
            MaxArticles = p.Count,
            ImportImageFiles = p.ImportImages,
            LeadImageOnly = p.LeadImageOnly,
            ImportSections = p.ImportSections,
            ImportLinks = p.ImportLinks,
        };
        var job = FileScanJobs.Start(p.StoreId, jobKind, j => run(store, options, j));
        return new { JobId = job.Id };
    }

    object progress(JobPayload p) {
        var job = FileScanJobs.Get(p.JobId);
        return new {
            job.State,
            job.Description,
            job.Percent,
            job.Error,
            Result = job.Result as WikiImportResult,
        };
    }

    object cancel(JobPayload p) {
        FileScanJobs.Get(p.JobId).Cancellation.Cancel();
        return new { Cancelled = true };
    }

    static async Task<object> run(NodeStore store, WikiImportOptions options, FileScanJob job) {
        job.SetProgress("Opening the corpus…", 0);
        using var importer = new WikiCorpusImporter(store, options);
        return await importer.RunAsync(job.SetProgress, job.Cancellation.Token);
    }

    // ---- finding the files ----

    /// <summary>The first folder that looks like a corpus workspace, and what is in it.</summary>
    static (string? Corpus, string? Bundle) discover() {
        foreach (var folder in likelyFolders) {
            if (!safeExists(folder)) continue;
            var corpus = corpusIn(folder);
            var bundle = bundleIn(folder);
            if (corpus != null || bundle != null) return (corpus, bundle);
        }
        return (null, null);
    }

    /// <summary>The largest .jsonl in a folder - the corpus, rather than a test slice beside it.</summary>
    static string? corpusIn(string folder) => largest(folder, ["*.jsonl", "*.jsonl.gz"]);

    /// <summary>The largest .wikimg bundle in a folder or its "images" subfolder. Largest because a
    /// bundle built with a limit sits beside a fuller one often enough to be worth preferring.</summary>
    static string? bundleIn(string folder) {
        return largest(folder, ["*.wikimg"]) ?? largest(Path.Combine(folder, "images"), ["*.wikimg"]);
    }

    static string? largest(string folder, string[] patterns) {
        try {
            return patterns
                .SelectMany(pattern => Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly))
                .Select(path => new FileInfo(path))
                .OrderByDescending(f => f.Length)
                .FirstOrDefault()?.FullName;
        } catch (Exception) {
            return null; // an unreadable folder is simply not a candidate
        }
    }

    static bool safeExists(string folder) {
        try { return Directory.Exists(folder); } catch (Exception) { return false; }
    }

    static long size(string path) {
        try { return new FileInfo(path).Length; } catch (Exception) { return 0; }
    }

    // ---- pieces ----

    const string noWikiTypeMessage = "The datamodel of this database has no Relatude.DB.Demo.Models.IWikiArticle node type, so there is nothing to import into. "
        + "Add the demo datamodel source (assembly reference Relatude.DB.NodeStore, namespace Relatude.DB.Demo.Models) to use the Wikipedia corpus. ";

    NodeStoreContainer container(Guid storeId) {
        if (!_server.Containers.TryGetValue(storeId, out var c)) throw new Exception("Database not found. ");
        return c;
    }

    NodeStore openStore(Guid storeId) {
        var c = container(storeId);
        if (!c.IsOpen()) throw new Exception("The database must be open. ");
        return c.Store!;
    }

    sealed record StorePayload(Guid StoreId);
    sealed record BrowsePayload(string? Path);
    sealed record StartPayload(Guid StoreId, string? CorpusPath, string? BundlePath, int Count,
                               bool ImportImages, bool LeadImageOnly, bool ImportSections, bool ImportLinks);
    sealed record JobPayload(Guid JobId);
}
