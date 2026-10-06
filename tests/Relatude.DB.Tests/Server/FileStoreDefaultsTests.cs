using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Files;
using Relatude.DB.IO;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Settings;
using Relatude.DB.Nodes;
using Relatude.Server.FileStoreDefaultsModel;

namespace Relatude.Server.FileStoreDefaultsModel {
    // a namespace of its own, so the compiled-types source below loads this type and nothing else
    [Node]
    public interface IFsdDoc {
        Guid Id { get; set; }
        FileValue Attachment { get; set; }
    }
}

namespace Relatude.Server {

/// <summary>
/// A new file store keeps one copy per content and hashes with SHA256, and a new installation (the
/// server's default settings, the CLI's template, a database added in the admin UI) gets such a MultiFile
/// store as its default. A store read from JSON that does not mention the two settings was configured
/// before they existed and keeps MD5 and one copy per upload.
/// </summary>
[TestClass]
public class FileStoreDefaultsTests {
    static FileStoreSettings read(string json) => JsonSerializer.Deserialize<FileStoreSettings>(json, LocalSettingsLoaderFile.JsonOptions)!;

    [TestMethod]
    public void ANewStore_KeepsOneCopyPerContent_HashedWithSha256() {
        var store = new FileStoreSettings();
        Assert.IsTrue(store.SameHashSameFile);
        Assert.AreEqual(FileHashAlgorithm.SHA256, store.HashAlgorithm);

        var io = Guid.NewGuid();
        var multi = FileStoreSettings.CreateMultiFile(io);
        Assert.AreNotEqual(Guid.Empty, multi.Id);
        Assert.AreEqual(io, multi.IoProviderId);
        Assert.AreEqual(FileStoreEngine.MultiFile, multi.StoreType);
        Assert.AreEqual(2, multi.MultiFileFolderDepth);
        Assert.IsTrue(multi.SameHashSameFile);
        Assert.AreEqual(FileHashAlgorithm.SHA256, multi.HashAlgorithm);
    }

    [TestMethod]
    public void AStoreReadWithoutTheSettings_KeepsMd5AndOneCopyPerUpload() {
        var old = read("""{ "Id": "c4d5e6f7-0000-0000-0000-000000000001", "IoProviderId": "1a2b3c4d-0000-0000-0000-000000000001", "StoreType": "MultiFile", "MultiFileFolderDepth": 2 }""");
        Assert.IsFalse(old.SameHashSameFile);
        Assert.AreEqual(FileHashAlgorithm.MD5, old.HashAlgorithm);

        // the same inside a whole settings file
        var file = JsonSerializer.Deserialize<RelatudeDBServerSettings>("""
            { "ContainerSettings": [ { "Id": "8f6b0000-0000-0000-0000-000000000001",
                "FileStoreSettings": [ { "Id": "c4d5e6f7-0000-0000-0000-000000000001", "StoreType": "MultiFile" } ] } ] }
            """, LocalSettingsLoaderFile.JsonOptions)!;
        var store = file.ContainerSettings![0].FileStoreSettings![0];
        Assert.IsFalse(store.SameHashSameFile);
        Assert.AreEqual(FileHashAlgorithm.MD5, store.HashAlgorithm);
    }

    [TestMethod]
    public void TheSettingsJsonNames_AreKept() {
        var on = read("""{ "StoreType": "MultiFile", "SameHashSameFile": true, "HashAlgorithm": "SHA256" }""");
        Assert.IsTrue(on.SameHashSameFile);
        Assert.AreEqual(FileHashAlgorithm.SHA256, on.HashAlgorithm);
        var off = read("""{ "StoreType": "MultiFile", "SameHashSameFile": false, "HashAlgorithm": "MD5" }""");
        Assert.IsFalse(off.SameHashSameFile);
        Assert.AreEqual(FileHashAlgorithm.MD5, off.HashAlgorithm);
        // one of the two named: the other keeps what a store from before had
        var hashOnly = read("""{ "StoreType": "MultiFile", "HashAlgorithm": "SHA256" }""");
        Assert.IsFalse(hashOnly.SameHashSameFile);
        Assert.AreEqual(FileHashAlgorithm.SHA256, hashOnly.HashAlgorithm);

        // a new store is written with both, so reading it back does not turn it into an old one
        var json = JsonSerializer.Serialize(FileStoreSettings.CreateMultiFile(Guid.NewGuid()), LocalSettingsLoaderFile.JsonOptions);
        StringAssert.Contains(json, "\"SameHashSameFile\"");
        StringAssert.Contains(json, "\"HashAlgorithm\"");
        var back = read(json);
        Assert.IsTrue(back.SameHashSameFile);
        Assert.AreEqual(FileHashAlgorithm.SHA256, back.HashAlgorithm);
    }

    /// <summary>A store an application defines in appsettings goes through the same JSON reading, so one
    /// set up there before the two settings existed is not switched over either.</summary>
    [TestMethod]
    public void AStoreFromConfiguration_WithoutTheSettings_KeepsMd5() {
        var file = RelatudeDBServerSettings.CreateDefault();
        var container = file.ContainerSettings![0];
        var configured = Guid.NewGuid();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["RelatudeDB:ContainerSettings:0:Id"] = container.Id.ToString(),
            ["RelatudeDB:ContainerSettings:0:FileStoreSettings:0:Id"] = configured.ToString(),
            ["RelatudeDB:ContainerSettings:0:FileStoreSettings:0:IoProviderId"] = container.IoDatabase.ToString(),
            ["RelatudeDB:ContainerSettings:0:FileStoreSettings:0:StoreType"] = "MultiFile",
        }).Build();
        var overlay = SettingsOverlay.Create(configuration, SettingsOverlay.DefaultSectionName, _ => { }, _ => { })!;
        var merged = overlay.Apply(file);

        var stores = merged.ContainerSettings![0].FileStoreSettings!;
        var fromConfiguration = stores.Single(s => s.Id == configured);
        Assert.IsFalse(fromConfiguration.SameHashSameFile);
        Assert.AreEqual(FileHashAlgorithm.MD5, fromConfiguration.HashAlgorithm);
        var fromFile = stores.Single(s => s.Id != configured);
        Assert.IsTrue(fromFile.SameHashSameFile, "the store of the file keeps the values the file has");
        Assert.AreEqual(FileHashAlgorithm.SHA256, fromFile.HashAlgorithm);
    }

    [TestMethod]
    public void ANewInstallation_HasAMultiFileStore_AsItsDefault() {
        var settings = RelatudeDBServerSettings.CreateDefault();
        var container = settings.ContainerSettings!.Single();
        var store = container.FileStoreSettings!.Single();
        Assert.AreEqual(FileStoreEngine.MultiFile, store.StoreType);
        Assert.IsTrue(store.SameHashSameFile);
        Assert.AreEqual(FileHashAlgorithm.SHA256, store.HashAlgorithm);
        Assert.AreEqual(container.IoDatabase, store.IoProviderId);
        Assert.AreEqual(store.Id, container.LocalSettings!.DefaultFileStore, "uploads go to it, not to the implicit store");

        // as it is written to relatude.db.json and read again
        var reread = TestServerHost.Copy(settings).ContainerSettings![0];
        var rereadStore = reread.FileStoreSettings!.Single();
        Assert.IsTrue(rereadStore.SameHashSameFile);
        Assert.AreEqual(FileHashAlgorithm.SHA256, rereadStore.HashAlgorithm);
        Assert.AreEqual(rereadStore.Id, reread.LocalSettings!.DefaultFileStore);
    }

    /// <summary>
    /// Through a running server: uploads land in the default MultiFile store, two of the same content
    /// share one SHA256-named copy, and a SingleFile store holding the new-store defaults (what the admin
    /// UI leaves behind when the layout is switched) still opens - it ignores them.
    /// </summary>
    [TestMethod]
    public async Task Uploads_ShareOneSha256Copy_AndASingleFileStoreWithTheDefaultsOpens() {
        var root = Path.Combine(Path.GetTempPath(), "relatude-filestore-defaults-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var multi = Guid.Empty;
        var single = Guid.NewGuid();
        var host = TestServerHost.Start(root, configure: s => {
            var c = s.ContainerSettings![0];
            var store = FileStoreSettings.CreateMultiFile(c.IoDatabase!.Value);
            multi = store.Id;
            c.FileStoreSettings = [store, new FileStoreSettings { Id = single, IoProviderId = c.IoDatabase.Value, StoreType = FileStoreEngine.SingleFile }];
            c.LocalSettings!.DefaultFileStore = store.Id;
            c.DatamodelSources = [new DatamodelSource {
                Id = Guid.NewGuid(), Name = "Compiled", Type = DatamodelSourceType.CompiledTypes,
                Reference = typeof(IFsdDoc).Assembly.GetName().Name, Namespace = typeof(IFsdDoc).Namespace,
            }];
        });
        try {
            var c = host.Server.Containers.Values.First();
            Assert.IsNotNull(c.Store, "the database opens with a SingleFile store holding the new-store defaults");
            var data = new byte[3000];
            new Random(7).NextBytes(data);
            var first = c.Store.Create<IFsdDoc>();
            c.Store.Insert(first);
            await c.Store.FileUploadAsync(first, d => d.Attachment, data, "first.bin");
            var second = c.Store.Create<IFsdDoc>();
            c.Store.Insert(second);
            await c.Store.FileUploadAsync(second, d => d.Attachment, data, "second.bin");

            var a = c.Store.Get<IFsdDoc>(first.Id).Attachment;
            var b = c.Store.Get<IFsdDoc>(second.Id).Attachment;
            Assert.AreEqual(multi, a.StorageId);
            Assert.AreEqual(64, a.Hash.Length, "a SHA256 hash, in hex");
            Assert.AreEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)), a.Hash.ToUpperInvariant());
            Assert.IsTrue(FileValue.IsKeptByHash(a));
            Assert.AreEqual(a.FileId, b.FileId, "the second upload points at the first one's copy");
            Assert.AreEqual("second.bin", b.Name);
        } finally {
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
}
