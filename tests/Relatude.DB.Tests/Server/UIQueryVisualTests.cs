using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.FileConversion.ImageEncoders;
using Relatude.DB.NodeServer.Settings;
using Relatude.DB.Demo.Models;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Json;
using Relatude.DB.Nodes;

namespace Relatude.Server;

/// <summary>
/// The visual pivot's half of the admin query page, driven through the command endpoint the browser
/// uses: every node of a result as a card, with the group each card falls in per property - sent as
/// bytes - and the int id of a card turned back into the guid the form opens on.
/// </summary>
[TestClass]
public class UIQueryVisualTests {
    static readonly string[] _titles = ["Alpha", "Beta", "Gamma"];

    static (TestServerHost host, Guid storeId, List<DemoArticle> articles) start(string root, bool files = false) {
        var host = TestServerHost.Start(root, configure: files ? withFileStore : null);
        typeof(RelatudeDBServer).GetMethod("MapAdminAPI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Server, [host.App]);
        var storeId = host.Settings.Settings.ContainerSettings![0].Id;
        var store = host.Server.Containers[storeId].Store!;
        var articles = new List<DemoArticle>();
        for (var i = 1; i <= 30; i++) articles.Add(new DemoArticle { Id = Guid.NewGuid(), Title = _titles[i % 3], Content = "c" + i, Size = i });
        store.Insert(articles);
        return (host, storeId, articles);
    }

    // a file store on the container's memory io, so a test can upload a picture onto a node
    static void withFileStore(RelatudeDBServerSettings settings) {
        var container = settings.ContainerSettings![0];
        container.FileStoreSettings = [new FileStoreSettings { Id = Guid.NewGuid(), IoProviderId = container.IOSettings![0].Id, StoreType = FileStoreEngine.MultiFile, MultiFileFolderDepth = 2 }];
    }

    static async Task<JsonElement> command(TestServerHost host, string type, object payload) {
        var http = new DefaultHttpContext();
        var body = JsonSerializer.Serialize(new { type, payload }, RelatudeDBJsonOptions.Default);
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var result = await host.Server.UI!.Commands.Execute(http);
        var value = ((IValueHttpResult)result).Value;
        var status = ((IStatusCodeHttpResult)result).StatusCode ?? 200;
        var json = JsonSerializer.SerializeToElement(value, RelatudeDBJsonOptions.Default);
        Assert.AreEqual(200, status, "command " + type + " failed: " + json);
        return json;
    }
    static JsonElement prop(JsonElement e, string name) {
        foreach (var p in e.EnumerateObject()) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        throw new AssertFailedException("No property \"" + name + "\" in " + e);
    }
    static JsonElement typeOf(JsonElement model, string name) => prop(model, "types").EnumerateArray().First(t => prop(t, "name").GetString() == name);
    // the wire form of the per-card arrays: base64 of little-endian int32 ids / uint16 group indexes
    static int[] ids(JsonElement e, int count) {
        var bytes = e.GetBytesFromBase64();
        Assert.AreEqual(count * 4, bytes.Length);
        var result = new int[count];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        return result;
    }
    static ushort[] groups(JsonElement e, int count) {
        var bytes = e.GetBytesFromBase64();
        Assert.AreEqual(count * 2, bytes.Length);
        var result = new ushort[count];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        return result;
    }

    [TestMethod]
    public async Task Visual_EveryCardInItsGroups() {
        var root = Path.Combine(Path.GetTempPath(), "relatude-visual-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var (host, storeId, articles) = start(root);
        try {
            var model = await command(host, "query-model", new { storeId });
            var typeId = prop(typeOf(model, nameof(DemoArticle)), "id").GetGuid();
            var pivotModel = await command(host, "query-pivot-model", new { storeId, typeId });
            var properties = prop(pivotModel, "properties").EnumerateArray().ToDictionary(p => prop(p, "name").GetString()!, p => prop(p, "id").GetGuid());

            var visual = await command(host, "query-visual", new {
                storeId, typeId, text = "", semanticRatio = (double?)null, minimumSimilarity = (double?)null, selections = Array.Empty<object>(),
                properties = new[] {
                    new { propertyId = properties["Title"], mode = "values" },
                    new { propertyId = properties["Size"], mode = "ranges" },
                },
            });
            var count = prop(visual, "count").GetInt32();
            Assert.AreEqual(articles.Count, count);
            Assert.AreEqual(articles.Count, prop(visual, "total").GetInt32());
            var cardIds = ids(prop(visual, "ids"), count);
            Assert.AreEqual(count, cardIds.Distinct().Count(), "every card is a different node");
            var byName = prop(visual, "properties").EnumerateArray().ToDictionary(p => prop(p, "name").GetString()!, p => p);

            // by title: one group per value, in value order, every card in the group of its title
            var title = byName["Title"];
            Assert.IsFalse(prop(title, "isRange").GetBoolean());
            Assert.AreEqual(0, prop(title, "unassigned").GetInt32());
            var titleGroups = prop(title, "groups").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(_titles.OrderBy(t => t).ToArray(), titleGroups.Select(g => prop(g, "label").GetString()).ToArray());
            foreach (var g in titleGroups) Assert.AreEqual(articles.Count(a => a.Title == prop(g, "label").GetString()), prop(g, "count").GetInt32());
            var titleOf = groups(prop(title, "assignment"), count);
            var byGuid = articles.ToDictionary(a => a.Id);
            for (var i = 0; i < count; i++) {
                // a card's int id is what the picture carries; the guid is what the form needs
                var guid = prop(await command(host, "query-node-id", new { storeId, id = cardIds[i] }), "id").GetGuid();
                Assert.AreEqual(byGuid[guid].Title, prop(titleGroups[titleOf[i]], "label").GetString(), "card " + i + " is in the group of its own title");
            }

            // by size, as ranges: a scalar with many values is bucketed into ranges that together hold every card
            var size = byName["Size"];
            Assert.IsTrue(prop(size, "isRange").GetBoolean());
            var sizeGroups = prop(size, "groups").EnumerateArray().ToArray();
            Assert.IsTrue(sizeGroups.Length >= 2, "30 distinct sizes make several range buckets");
            Assert.AreEqual(count, sizeGroups.Sum(g => prop(g, "count").GetInt32()));
            var sizeOf = groups(prop(size, "assignment"), count);
            Assert.IsTrue(sizeOf.All(g => g < sizeGroups.Length), "no card outside the range buckets");
            // the ranges carry selection tokens (value and value2), so a legend entry can filter by them
            Assert.IsNotNull(prop(sizeGroups[0], "value").GetString());
            Assert.IsNotNull(prop(sizeGroups[0], "value2").GetString());

            // sorted by size: the order is the cards' positions, smallest size first, and every card is placed once
            var sortedVisual = await command(host, "query-visual", new {
                storeId, typeId, text = "", selections = Array.Empty<object>(),
                properties = new[] { new { propertyId = properties["Title"], mode = "values" } },
                sortBy = properties["Size"], sortDescending = false,
            });
            var order = ids(prop(sortedVisual, "order"), count); // the same wire form: int32 positions
            CollectionAssert.AreEquivalent(Enumerable.Range(0, count).ToArray(), order, "a permutation of the cards");
            var sortedIds = ids(prop(sortedVisual, "ids"), count);
            var sizes = new List<int>();
            foreach (var at in order) {
                var guid = prop(await command(host, "query-node-id", new { storeId, id = sortedIds[at] }), "id").GetGuid();
                sizes.Add(byGuid[guid].Size);
            }
            CollectionAssert.AreEqual(sizes.OrderBy(x => x).ToArray(), sizes.ToArray(), "cards in ascending size order");
            var descending = await command(host, "query-visual", new {
                storeId, typeId, text = "", selections = Array.Empty<object>(),
                properties = Array.Empty<object>(), sortBy = properties["Size"], sortDescending = true,
            });
            var firstDescending = ids(prop(descending, "order"), count)[0];
            var largest = prop(await command(host, "query-node-id", new { storeId, id = ids(prop(descending, "ids"), count)[firstDescending] }), "id").GetGuid();
            Assert.AreEqual(articles.Max(a => a.Size), byGuid[largest].Size, "descending puts the largest first");
            // no sort asked for: no order is sent, the result's own stands
            Assert.AreEqual(JsonValueKind.Null, prop(visual, "order").ValueKind);

            // a facet selection narrows the picture to the cards behind it, like every other view
            var token = prop(titleGroups[0], "value").GetString();
            var narrowed = await command(host, "query-visual", new {
                storeId, typeId, text = "",
                selections = new[] { new { propertyId = properties["Title"], values = new[] { new { value = token, value2 = (string?)null } } } },
                properties = new[] { new { propertyId = properties["Size"], mode = "values" } },
            });
            Assert.AreEqual(prop(titleGroups[0], "count").GetInt32(), prop(narrowed, "count").GetInt32());
            var narrowedSize = prop(narrowed, "properties")[0];
            Assert.IsFalse(prop(narrowedSize, "isRange").GetBoolean(), "asked for values, not ranges");
            Assert.AreEqual(prop(narrowed, "count").GetInt32(), prop(narrowedSize, "groups").GetArrayLength(), "every size is its own group");
        } finally {
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// The cards' names and pictures: query-cards names every card and points at the first file
    /// property holding a convertible image, and the card-images route streams the picture of a
    /// card at a level's width, as a record per card, in the shape the browser parses.
    /// </summary>
    [TestMethod]
    public async Task Visual_CardsCarryNamesAndPictures() {
        var root = Path.Combine(Path.GetTempPath(), "relatude-visual-cards-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var (host, storeId, articles) = start(root, files: true);
        try {
            var store = host.Server.Containers[storeId].Store!;
            var fileProperty = store.Datastore.Datamodel.NodeTypesByFullName[typeof(DemoArticle).FullName!].AllPropertiesByName[nameof(DemoArticle.File)];
            // one article gets a picture, one a clip (not a picture), the rest nothing
            var withPicture = articles[0];
            var withClip = articles[1];
            byte[] png;
            using (var image = NativeImage.Create(320, 240)) png = image.Encode(FileFormat.Png);
            await store.Datastore.FileUploadAsync(new PropertyPath(withPicture.Id, fileProperty.Id), new MemoryStream(png), "picture.png");
            await store.Datastore.FileUploadAsync(new PropertyPath(withClip.Id, fileProperty.Id), new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]), "clip.mp4");

            var model = await command(host, "query-model", new { storeId });
            var typeId = prop(typeOf(model, nameof(DemoArticle)), "id").GetGuid();
            var visual = await command(host, "query-visual", new { storeId, typeId, text = "", selections = Array.Empty<object>(), properties = Array.Empty<object>() });
            var count = prop(visual, "count").GetInt32();
            var cardIds = ids(prop(visual, "ids"), count);
            var byGuid = articles.ToDictionary(a => a.Id);
            var guidOf = new Dictionary<int, Guid>();
            foreach (var id in cardIds) guidOf[id] = prop(await command(host, "query-node-id", new { storeId, id }), "id").GetGuid();

            // every card is named, and only the one with a picture points at the file property
            var cards = prop(await command(host, "query-cards", new { storeId, ids = cardIds }), "cards").EnumerateArray().ToDictionary(c => prop(c, "id").GetInt32(), c => c);
            Assert.AreEqual(count, cards.Count, "one answer per card");
            int pictureCardId = -1;
            int clipCardId = -1;
            foreach (var (id, card) in cards) {
                var article = byGuid[guidOf[id]];
                Assert.AreEqual(article.Title, prop(card, "name").GetString(), "a card is named by the node's display name");
                var image = prop(card, "image");
                if (article.Id == withPicture.Id) {
                    Assert.AreEqual(fileProperty.Id, image.GetGuid(), "the picture is the file property holding the image");
                    Assert.IsFalse(string.IsNullOrEmpty(prop(card, "version").GetString()), "a picture carries the file's version");
                    pictureCardId = id;
                } else {
                    Assert.AreEqual(JsonValueKind.Null, image.ValueKind, "a clip or no file is no picture: " + article.Title);
                    if (article.Id == withClip.Id) clipCardId = id;
                }
            }
            Assert.IsTrue(pictureCardId >= 0 && clipCardId >= 0);
            // ids the store does not know are left out rather than answered
            var unknown = prop(await command(host, "query-cards", new { storeId, ids = new[] { 1_000_000_000 } }), "cards");
            Assert.AreEqual(0, unknown.GetArrayLength());

            // the pictures: one record per card asked for, the picture at the level's width and
            // three quarters of it in height, and a card without one answered as such
            var records = await cardImages(host, storeId, 128, new object[] {
                new { id = pictureCardId, p = fileProperty.Id },
                new { id = clipCardId, p = fileProperty.Id },
            });
            Assert.AreEqual(2, records.Count);
            var picture = records.Single(r => r.Id == pictureCardId);
            Assert.AreEqual(0, picture.Status, "the picture is ready");
            Assert.IsNull(picture.Region, "a whole picture carries no region");
            using (var decoded = NativeImage.Load(new MemoryStream(picture.Bytes))) {
                Assert.AreEqual(128, decoded.Width);
                Assert.AreEqual(96, decoded.Height);
            }
            var clip = records.Single(r => r.Id == clipCardId);
            Assert.AreNotEqual(0, clip.Status, "a clip has no picture at this route");
            Assert.AreEqual(0, clip.Bytes.Length);

            // a tile: the lower right quarter of the picture, made a level's width, with the region
            // it shows reported back - which is what was asked for, give or take the encoder's pixels
            Assert.AreEqual(320, prop(cards[pictureCardId], "width").GetInt32(), "the original's size is told");
            var tiles = await cardImages(host, storeId, 128, new object[] { new { id = pictureCardId, p = fileProperty.Id, tile = new { x = 0.5, y = 0.5, size = 0.5, width = 1024 } } });
            var tileRecord = tiles.Single();
            Assert.AreEqual(0, tileRecord.Status, "the tile is ready");
            Assert.IsNotNull(tileRecord.Region);
            var region = tileRecord.Region!;
            Assert.IsTrue(Math.Abs(region[0] - 0.5) < 0.02 && Math.Abs(region[1] - 0.5) < 0.02 && Math.Abs(region[2] - 1.0) < 0.02 && Math.Abs(region[3] - 1.0) < 0.02,
                "the region is the quarter asked for: " + string.Join(", ", region));
            using (var decoded = NativeImage.Load(new MemoryStream(tileRecord.Bytes))) {
                Assert.AreEqual(1024, decoded.Width);
                Assert.AreEqual(768, decoded.Height);
            }
        } finally {
            await host.DisposeAsync();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    sealed record ImageRecord(int Id, byte Status, float[]? Region, byte[] Bytes);
    sealed class BodyPresent : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature {
        public bool CanHaveBody => true;
    }

    // the card-images route, driven through its endpoint the way the browser reaches it, and its
    // binary answer parsed record by record: int32 id, status byte, flags byte, int32 length, a
    // region of four floats when the flags say so, the bytes
    static async Task<List<ImageRecord>> cardImages(TestServerHost host, Guid storeId, int level, object[] items) {
        var endpoint = ((IEndpointRouteBuilder)host.App).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText != null && e.RoutePattern.RawText.EndsWith("/ui/card-images", StringComparison.Ordinal));
        var http = new DefaultHttpContext { RequestServices = host.App.Services };
        http.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new BodyPresent()); // a bare context has no body detection, and the binder reads no body without it
        http.Request.Method = "POST";
        http.Request.ContentType = "application/json";
        var json = JsonSerializer.Serialize(new { storeId, level, items }, RelatudeDBJsonOptions.Default);
        // the same deserialization the endpoint does, so a payload that cannot bind says why
        var payloadType = typeof(RelatudeDBServer).Assembly.GetType("Relatude.DB.NodeServer.UI.UIQuery+CardImagesPayload")!;
        try {
            Assert.IsNotNull(JsonSerializer.Deserialize(json, payloadType, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        } catch (Exception e) {
            Assert.Fail("the payload does not bind: " + e.Message + " json: " + json);
        }
        var body = Encoding.UTF8.GetBytes(json);
        http.Request.Body = new MemoryStream(body);
        http.Request.ContentLength = body.Length;
        var response = new MemoryStream();
        http.Response.Body = response;
        await endpoint.RequestDelegate!(http);
        Assert.AreEqual(200, http.Response.StatusCode, "card-images answered " + http.Response.StatusCode + ": " + Encoding.UTF8.GetString(response.ToArray()));
        var bytes = response.ToArray();
        var records = new List<ImageRecord>();
        var at = 0;
        while (at + 10 <= bytes.Length) {
            var id = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
            var status = bytes[at + 4];
            var flags = bytes[at + 5];
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 6));
            at += 10;
            float[]? region = null;
            if ((flags & 1) != 0) {
                region = new float[4];
                for (var i = 0; i < 4; i++) region[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at + i * 4));
                at += 16;
            }
            records.Add(new ImageRecord(id, status, region, bytes.AsSpan(at, length).ToArray()));
            at += length;
        }
        Assert.AreEqual(bytes.Length, at, "the stream is whole records");
        return records;
    }
}
