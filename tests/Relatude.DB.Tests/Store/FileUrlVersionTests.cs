using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Files;
using Relatude.DB.FileConversion;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.Web;

namespace Relatude.Store;

[Node]
public interface IUrlVersionDoc {
    Guid Id { get; set; }
    FileValue Picture { get; set; }
}

/// <summary>
/// The version in a file URL. A ready response may be cached for 30 days, so the version has to
/// change when what the URL serves may - another file, a cleared conversion cache, other image
/// defaults for an adaptive variant - and must not change otherwise. It used to change at every
/// start, so a restart sent every browser and CDN back for every picture.
/// </summary>
[TestClass]
public class FileUrlVersionTests {
    sealed class Site(SettingsLocal settings) {
        public readonly IOProviderMemory Io = new();
        public readonly IOProviderMemory FilesIo = new();
        public readonly SettingsLocal Settings = settings;
        public NodeStore Open() => new(DataStoreLocal.Open(datamodel(), Settings, Io, [new MultiFileStore(Settings.DefaultFileStore!.Value, FilesIo, 2)]));
    }
    static Datamodel datamodel() {
        var dm = new Datamodel();
        dm.Add<IUrlVersionDoc>();
        return dm;
    }
    static Guid pictureId(NodeStore store)
        => store.Datastore.Datamodel.NodeTypes.Values.Single(t => t.CodeName == nameof(IUrlVersionDoc)).AllProperties.Values.Single(p => p.CodeName == nameof(IUrlVersionDoc.Picture)).Id;
    static async Task<PropertyPath> addPicture(NodeStore store, string fileName, int seed) {
        var doc = store.Create<IUrlVersionDoc>();
        store.Insert(doc);
        var data = new byte[2000];
        new Random(seed).NextBytes(data);
        await store.FileUploadAsync(doc.Id, pictureId(store), new MemoryStream(data), fileName);
        return new PropertyPath(doc.Id, pictureId(store));
    }
    static readonly FileAdjustmentImage _adaptive = new() { Width = 200 };
    static readonly FileAdjustmentImage _webp = new() { Width = 200, RequestedFormat = FileFormat.Webp };
    static string[] urls(NodeStore store, PropertyPath path) => [
        store.Datastore.GetUrl(path, false),
        store.Datastore.GetUrl(path, _adaptive, false),
        store.Datastore.GetUrl(path, _webp, false),
    ];

    [TestMethod]
    public async Task FileUrlsAreTheSameAfterARestart() {
        var site = new Site(new SettingsLocal { DefaultFileStore = Guid.NewGuid() });
        var store = site.Open();
        var path = await addPicture(store, "photo.jpg", 1);
        var before = urls(store, path);
        store.Dispose();

        store = site.Open();
        try {
            CollectionAssert.AreEqual(before, urls(store, path));
        } finally {
            store.Dispose();
        }
    }

    [TestMethod]
    public async Task ClearingTheConvertedFilesGivesNewUrls() {
        var site = new Site(new SettingsLocal { DefaultFileStore = Guid.NewGuid() });
        var store = site.Open();
        try {
            var path = await addPicture(store, "photo.jpg", 1);
            var before = urls(store, path);
            store.Datastore.ClearAllCachedConversions();
            var after = urls(store, path);
            for (var i = 0; i < before.Length; i++) Assert.AreNotEqual(before[i], after[i], after[i]);
        } finally {
            store.Dispose();
        }
    }

    [TestMethod]
    public async Task NewImageDefaultsGiveAdaptiveVariantsNewUrls() {
        var site = new Site(new SettingsLocal { DefaultFileStore = Guid.NewGuid() });
        var store = site.Open();
        try {
            var path = await addPicture(store, "photo.jpg", 1);
            var before = urls(store, path);
            site.Settings.ImageDefaultFormat = ImageDefaultFormat.WebP; // a live setting
            var after = urls(store, path);
            Assert.AreEqual(before[0], after[0], "the original does not depend on the defaults");
            Assert.AreNotEqual(before[1], after[1], "the adaptive variant now resolves to another format");
            Assert.AreEqual(before[2], after[2], "a variant naming its format does not depend on the defaults");
            site.Settings.ImageDefaultQuality = 70;
            Assert.AreNotEqual(after[1], urls(store, path)[1]);
        } finally {
            store.Dispose();
        }
    }

    [TestMethod]
    public async Task AnotherFileGetsAnotherVersion() {
        var site = new Site(new SettingsLocal { DefaultFileStore = Guid.NewGuid() });
        var store = site.Open();
        try {
            var path = await addPicture(store, "photo.jpg", 1);
            var before = urls(store, path);
            var data = new byte[2000];
            new Random(2).NextBytes(data);
            await store.FileUploadAsync(path.NodePath.NodeKey.Guid, pictureId(store), new MemoryStream(data), "photo.jpg");
            var after = urls(store, path);
            for (var i = 0; i < before.Length; i++) Assert.AreNotEqual(before[i], after[i], after[i]);
        } finally {
            store.Dispose();
        }
    }
}
