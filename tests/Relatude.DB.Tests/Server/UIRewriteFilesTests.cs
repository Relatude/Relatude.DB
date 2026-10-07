using Microsoft.AspNetCore.Http;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Files;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Json;
using Relatude.DB.NodeServer.Settings;
using Relatude.DB.Nodes;
using Relatude.Server.FileStoreDefaultsModel;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Relatude.Server;

/// <summary>
/// The rewrite dialog's commands: file-store-choices says which stores receive uploads and whether their
/// hash and one copy per content can be chosen, and a rewrite started with a hash and one copy per content
/// saves them as the store's settings, switches the open store over - so the next upload is written that
/// way too - and writes the files that belong in the store into it.
/// </summary>
[TestClass]
public class UIRewriteFilesTests {

    static async Task<JsonElement> send(TestServerHost host, string type, object payload) {
        var http = new DefaultHttpContext();
        var body = JsonSerializer.Serialize(new { type, payload }, RelatudeDBJsonOptions.Default);
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var result = await host.Server.UI!.Commands.Execute(http);
        var value = ((IValueHttpResult)result).Value;
        var status = ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        var json = JsonSerializer.SerializeToElement(value, RelatudeDBJsonOptions.Default);
        Assert.AreEqual(200, status, type + " failed: " + json);
        return json;
    }

    [TestMethod]
    public async Task ARewriteWithAnotherHash_SavesItAsTheStoresSettings_AndTheNextUploadIsWrittenThatWay() {
        var root = Path.Combine(Path.GetTempPath(), "relatude-rewrite-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var main = Guid.NewGuid();
        var spare = Guid.NewGuid();
        var host = TestServerHost.Start(root, configure: s => {
            var c = s.ContainerSettings![0];
            // a store set up before the two options existed: MD5, one copy per upload
            c.FileStoreSettings = [
                new FileStoreSettings { Id = main, IoProviderId = c.IoDatabase!.Value, StoreType = FileStoreEngine.MultiFile, MultiFileFolderDepth = 2, SameHashSameFile = false, HashAlgorithm = FileHashAlgorithm.MD5 },
                new FileStoreSettings { Id = spare, IoProviderId = c.IoDatabase.Value, StoreType = FileStoreEngine.MultiFile, MultiFileFolderDepth = 2 },
            ];
            c.LocalSettings!.DefaultFileStore = main;
            c.DatamodelSources = [new DatamodelSource {
                Id = Guid.NewGuid(), Name = "Compiled", Type = DatamodelSourceType.CompiledTypes,
                Reference = typeof(IFsdDoc).Assembly.GetName().Name, Namespace = typeof(IFsdDoc).Namespace,
            }];
        });
        typeof(RelatudeDBServer).GetMethod("MapAdminAPI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Server, [host.App]);
        try {
            var c = host.Server.Containers.Values.First();
            var store = c.Store!;
            var data = new byte[2500];
            new Random(3).NextBytes(data);
            var first = store.Create<IFsdDoc>();
            store.Insert(first);
            await store.FileUploadAsync(first, d => d.Attachment, data, "first.bin");
            var second = store.Create<IFsdDoc>();
            store.Insert(second);
            await store.FileUploadAsync(second, d => d.Attachment, data, "second.bin");
            Assert.AreEqual(32, store.Get<IFsdDoc>(first.Id).Attachment.Hash.Length);

            var choices = (await send(host, "file-store-choices", new { StoreId = c.Settings.Id })).EnumerateArray().ToList();
            JsonElement choice(Guid id) => choices.Single(x => x.GetProperty("id").GetGuid() == id);
            Assert.IsTrue(choice(main).GetProperty("receivesUploads").GetBoolean(), "the default store");
            Assert.IsFalse(choice(spare).GetProperty("receivesUploads").GetBoolean(), "neither the default nor named by a property");
            Assert.AreEqual(JsonValueKind.Null, choice(main).GetProperty("writeOptionsLockedBy").ValueKind);
            Assert.AreEqual("MD5", choice(main).GetProperty("hashAlgorithm").GetString());
            var writesBefore = host.Settings.Writes.Count;

            var start = await send(host, "files-scan-start", new {
                StoreId = c.Settings.Id, Scan = "rewrite", CountOnly = false, ToStore = main, HashAlgorithm = "SHA256", SameHashSameFile = true,
            });
            var jobId = start.GetProperty("jobId").GetGuid();
            JsonElement progress;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true) {
                progress = await send(host, "files-scan-progress", new { JobId = jobId });
                var state = progress.GetProperty("state").GetString();
                if (state != "running") break;
                Assert.IsTrue(DateTime.UtcNow < deadline, "the rewrite did not finish");
                await Task.Delay(20);
            }
            Assert.AreEqual("done", progress.GetProperty("state").GetString(), progress.ToString());
            var rewrite = progress.GetProperty("rewrite");
            Assert.AreEqual(2, rewrite.GetProperty("valuesRewritten").GetInt32());
            Assert.AreEqual(2, rewrite.GetProperty("filesCopied").GetInt32(), "two stored files read - and kept as one below");

            var saved = c.Settings.FileStoreSettings!.Single(f => f.Id == main);
            Assert.AreEqual(FileHashAlgorithm.SHA256, saved.HashAlgorithm, "saved as the store's settings");
            Assert.IsTrue(saved.SameHashSameFile);
            Assert.IsTrue(host.Settings.Writes.Count > writesBefore, "and written out");
            var a = store.Get<IFsdDoc>(first.Id).Attachment;
            var b = store.Get<IFsdDoc>(second.Id).Attachment;
            Assert.AreEqual(64, a.Hash.Length);
            Assert.AreEqual(a.FileId, b.FileId, "the same bytes, one copy now");

            // the open store writes that way now, without a reopen
            var third = store.Create<IFsdDoc>();
            store.Insert(third);
            await store.FileUploadAsync(third, d => d.Attachment, data, "third.bin");
            var t = store.Get<IFsdDoc>(third.Id).Attachment;
            Assert.AreEqual(64, t.Hash.Length);
            Assert.IsTrue(FileValue.IsKeptByHash(t));
            Assert.AreEqual(a.FileId, t.FileId, "shares the copy the rewrite made");
        } finally {
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
