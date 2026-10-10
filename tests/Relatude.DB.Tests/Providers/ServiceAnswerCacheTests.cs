using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.NodeServer;
using Relatude.DB.Translation;
using Relatude.Utils;

namespace Relatude.Providers;

/// <summary>
/// The caches the services' answers are kept in on this machine: the Native KV file beside the AI
/// embedding cache, and the one in memory. What they keep must outlive a restart, go stale after the
/// age limit, never answer one key with another's value, and come back whole however long it was.
/// </summary>
[TestClass]
public class ServiceAnswerCacheTests {
    string _folder = "";

    [TestInitialize]
    public void Init() => _folder = Path.Combine(Path.GetTempPath(), "relatude-service-cache-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup() {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    string file => ServiceCaches.FilePath(_folder, "translation")!;

    [TestMethod]
    public void AnAnswerOutlivesTheProcessThatKeptIt() {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using (var cache = new NativeKvServiceAnswerCache(file)) {
            cache.SetMany([new("translate|nb|Good morning", "God morgen"), new("translate|de|Good morning", "Guten Morgen")]);
            Assert.IsTrue(cache.TryGet("translate|nb|Good morning", out var nb));
            Assert.AreEqual("God morgen", nb);
        }
        using (var reopened = new NativeKvServiceAnswerCache(file)) {
            Assert.IsTrue(reopened.TryGet("translate|de|Good morning", out var de), "kept in the file, not in the process");
            Assert.AreEqual("Guten Morgen", de);
            Assert.IsFalse(reopened.TryGet("translate|sv|Good morning", out _));

            reopened.Remove("translate|de|Good morning");
            Assert.IsFalse(reopened.TryGet("translate|de|Good morning", out _));
            reopened.ClearAll();
            Assert.IsFalse(reopened.TryGet("translate|nb|Good morning", out _));
            reopened.Set("after", "a clear");
            Assert.IsTrue(reopened.TryGet("after", out var after), "the cache is in use again after a clear");
            Assert.AreEqual("a clear", after);
        }
    }

    [TestMethod]
    public void AnAnswerPastTheAgeLimitIsAMiss() {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using (var cache = new NativeKvServiceAnswerCache(file)) cache.Set("k", "v");
        using (var strict = new NativeKvServiceAnswerCache(file, maxAge: TimeSpan.Zero)) Assert.IsFalse(strict.TryGet("k", out _));
        using (var usual = new NativeKvServiceAnswerCache(file)) Assert.IsTrue(usual.TryGet("k", out _), "still there for a cache with the usual limit");

        var memory = new MemoryServiceAnswerCache(maxAge: TimeSpan.Zero);
        memory.Set("k", "v");
        Assert.IsFalse(memory.TryGet("k", out _));
    }

    [TestMethod]
    public void ALongTextIsKeptDeflatedAndComesBackWhole() {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var text = string.Concat(Enumerable.Range(0, 20_000).Select(i => "Line " + (i % 97) + " of the contract, ærøå.\n"));
        using (var cache = new NativeKvServiceAnswerCache(file)) cache.Set("filetotext|abc", text);
        Assert.IsTrue(new FileInfo(file).Length < text.Length, "a text of " + text.Length + " characters takes less in the file");
        using var reopened = new NativeKvServiceAnswerCache(file);
        Assert.IsTrue(reopened.TryGet("filetotext|abc", out var back));
        Assert.AreEqual(text, back);
    }

    [TestMethod]
    public void TheCacheFileIsBesideTheAiEmbeddingCache() {
        List<string> warnings = [];
        using (var cache = ServiceCaches.Create(ServiceCacheType.Native, _folder, "translation", warnings)) {
            Assert.IsInstanceOfType<NativeKvServiceAnswerCache>(cache);
            cache!.Set("k", "v");
        }
        var ai = Path.Combine([_folder, .. FileKeyUtility.GetAiCacheFileKey(AIProviderCacheType.Native)!]);
        Assert.AreEqual(Path.GetDirectoryName(ai), Path.GetDirectoryName(file));
        Assert.AreEqual("native.translation.cache.bin", Path.GetFileName(file));
        Assert.IsTrue(File.Exists(file));
        Assert.IsNull(ServiceCaches.Create(ServiceCacheType.None, _folder, "translation", warnings));
        Assert.IsInstanceOfType<MemoryServiceAnswerCache>(ServiceCaches.Create(ServiceCacheType.Memory, _folder, "translation", warnings));
        Assert.AreEqual(0, warnings.Count);
    }

    [TestMethod]
    public void ACacheThatCannotBeOpenedLeavesTheProviderWithoutOne() {
        // a file where the cache's folder should be: the folder cannot be made
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, FileKeyUtility.IndexStoreFolderKey), "not a folder");
        List<string> warnings = [];
        Assert.IsNull(ServiceCaches.Create(ServiceCacheType.Native, _folder, "translation", warnings));
        StringAssert.Contains(warnings.Single(), "translation answer cache");
    }

    /// <summary>The service caches go with the AI embedding cache when it is cleared.</summary>
    [TestMethod]
    public async Task ClearingTheAiCacheClearsTheServiceCaches() {
        var cache = new MemoryServiceAnswerCache();
        var translator = new CachingTranslationProvider(new CachingServiceProviderTests.FakeTranslator(), cache);
        using var store = new NodeStore(DataStoreLocal.Open(Helper.GetDatamodel(), null, new IOProviderMemory()), translation: translator);
        await store.Services.Translation.TranslateAsync("Hello", "nb");
        Assert.IsTrue(cache.TryGet("translate|v1|Text|found|nb|Hello", out _));

        await store.MaintenanceAsync(MaintenanceAction.ClearAiCache);

        Assert.IsFalse(cache.TryGet("translate|v1|Text|found|nb|Hello", out _));
    }
}
