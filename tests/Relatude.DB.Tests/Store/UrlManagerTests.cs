using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.Web;

namespace Relatude.Store;

#region test datamodel
[Node]
public class UrlPage {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(DisplayName = true)]
    public string Title { get; set; } = "";
    [AddressProperty]
    public string Slug { get; set; } = "";
    [HtmlProperty]
    public string Body { get; set; } = "";
    public UrlPageTree.Parent Parent { get; set; } = new();
    public UrlPageTree.Children Children { get; set; } = new();
}
public class UrlPageTree : OneToMany<UrlPage, UrlPage> {
    public class Parent : One { }
    public class Children : Many { }
}
#endregion

[TestClass]
public class UrlManagerTests {

    static NodeStore open(bool withTreeManager, IIOProvider? io = null) {
        var dm = new Datamodel();
        dm.Add<UrlPage>();
        dm.Add<UrlPageTree>();
        IUrlManager? manager = withTreeManager ? new TreeUrlManager(new TreeUrlManagerOptions() {
            ParentRelationName = "UrlPageTree",
        }) : null;
        var data = new DataStoreLocal(dm, new SettingsLocal(), io ?? new IOProviderMemory(), urlManager: manager);
        data.Open(true, true);
        return new NodeStore(data);
    }
    static Guid insert(NodeStore db, string title, string slug, Guid? parentId = null) {
        var page = new UrlPage() { Id = Guid.NewGuid(), Title = title, Slug = slug };
        db.Insert(page);
        if (parentId.HasValue) db.Execute(new Transaction(db).Relation.Relate<UrlPageTree>(parentId.Value, page.Id));
        return page.Id;
    }
    static string? storedBody(NodeStore db, Guid pageId) {
        var bodyPropId = db.Datastore.Datamodel.NodeTypesByFullName[typeof(UrlPage).FullName!]
            .AllProperties.Values.First(p => p.CodeName == nameof(UrlPage.Body)).Id;
        Assert.IsTrue(db.Datastore.TryGet(pageId, out var raw));
        return raw.TryGetValue(bodyPropId, out var value) ? (string)value : null;
    }
    /// <summary>tv / sony-x90 / info + mobile / pixel / info, both leaves sharing the segment "info".</summary>
    static (Guid tv, Guid sony, Guid sonyInfo, Guid mobile, Guid pixel, Guid pixelInfo) seedCatalog(NodeStore db) {
        var tv = insert(db, "TV", "tv");
        var sony = insert(db, "Sony X90", "sony-x90", tv);
        var sonyInfo = insert(db, "Sony info", "info", sony);
        var mobile = insert(db, "Mobile", "mobile");
        var pixel = insert(db, "Pixel", "pixel", mobile);
        var pixelInfo = insert(db, "Pixel info", "info", pixel);
        return (tv, sony, sonyInfo, mobile, pixel, pixelInfo);
    }

    // ------------------------------------------------------------------ classic behavior (no manager)

    [TestMethod]
    public void AddressMarkerMember_IsStoredOnInsert() {
        using var db = open(withTreeManager: false);
        var id = insert(db, "Hello", "hello");
        Assert.IsTrue(db.Datastore.TryGetAddress(id, out var address));
        Assert.AreEqual("hello", address);
        Assert.IsTrue(db.Datastore.TryGetNodeIdFromAddress("hello", out Guid foundId));
        Assert.AreEqual(id, foundId);
        Assert.AreEqual("hello", db.Get<UrlPage>(id).Slug);
    }

    [TestMethod]
    public void WithoutManager_AddressesStayGloballyUnique_WithSuffixOnCollision() {
        using var db = open(withTreeManager: false);
        var first = insert(db, "First", "same");
        var second = insert(db, "Second", "same");
        Assert.AreEqual("same", db.Get<UrlPage>(first).Slug);
        Assert.AreEqual("same-2", db.Get<UrlPage>(second).Slug); // the classic suffix loop
        Assert.IsFalse(db.WillAddressResultInUniqueUrl(new NodeKey(first), "same-2"));
        Assert.IsTrue(db.WillAddressResultInUniqueUrl(new NodeKey(first), "same")); // its own address
        Assert.IsTrue(db.WillAddressResultInUniqueUrl(new NodeKey(first), "unused"));
    }

    [TestMethod]
    public void WithoutManager_UrlsAndParsing_WorkAsBefore() {
        using var db = open(withTreeManager: false);
        var id = insert(db, "Hello", "hello");
        var url = db.GetUrl(id);
        StringAssert.Contains(url, "hello");
        Assert.IsTrue(db.TryParseUrl(url, out var keys));
        Assert.AreEqual(UrlTarget.Node, keys.Target);
        Assert.AreEqual(id, db.Get<UrlPage>(keys.NodeKey).Id);
    }

    // ------------------------------------------------------------------ tree manager

    [TestMethod]
    public void TreeManager_SharedSegments_GetUniqueUrlsFromTheParentChain() {
        using var db = open(withTreeManager: true);
        var (_, _, sonyInfo, _, _, pixelInfo) = seedCatalog(db);

        Assert.AreEqual("/tv/sony-x90/info", db.GetUrl(sonyInfo));
        Assert.AreEqual("/mobile/pixel/info", db.GetUrl(pixelInfo));

        // both nodes hold the plain segment "info", unmangled:
        Assert.AreEqual("info", db.Get<UrlPage>(sonyInfo).Slug);
        Assert.AreEqual("info", db.Get<UrlPage>(pixelInfo).Slug);
        Assert.AreEqual(2, db.GetNodeIdsFromAddress("info").Length);

        // and each URL resolves back to the right node:
        Assert.IsTrue(db.TryParseUrl("/tv/sony-x90/info", out var keys1));
        Assert.AreEqual(sonyInfo, db.Get<UrlPage>(keys1.NodeKey).Id);
        Assert.IsTrue(db.TryParseUrl("/mobile/pixel/info", out var keys2));
        Assert.AreEqual(pixelInfo, db.Get<UrlPage>(keys2.NodeKey).Id);
        Assert.IsFalse(db.TryParseUrl("/tv/pixel/info", out _)); // a path that matches no chain
    }

    [TestMethod]
    public void TreeManager_RenamingASection_IsOneWrite_AndUrlsFollow() {
        using var db = open(withTreeManager: true);
        var (tv, sony, sonyInfo, _, _, _) = seedCatalog(db);

        db.UpdateAddress(new NodeKey(tv), "television"); // one write, nothing else stored changes

        Assert.AreEqual("/television/sony-x90/info", db.GetUrl(sonyInfo));
        Assert.AreEqual("sony-x90", db.Get<UrlPage>(sony).Slug); // descendants untouched
        Assert.IsTrue(db.TryParseUrl("/television/sony-x90/info", out var keys));
        Assert.AreEqual(sonyInfo, db.Get<UrlPage>(keys.NodeKey).Id);
        Assert.IsFalse(db.TryParseUrl("/tv/sony-x90/info", out _)); // the old URL is gone
    }

    [TestMethod]
    public void TreeManager_WillAddressResultInUniqueUrl_ComparesCompleteUrls() {
        using var db = open(withTreeManager: true);
        var (_, sony, sonyInfo, _, pixel, pixelInfo) = seedCatalog(db);

        // the same segment under another parent is fine, under the same parent it is not:
        Assert.IsTrue(db.WillAddressResultInUniqueUrl(new NodeKey(pixelInfo), "info"));
        var sibling = insert(db, "Sibling of sony info", "specs", sony);
        Assert.IsFalse(db.WillAddressResultInUniqueUrl(new NodeKey(sibling), "info"));
        Assert.IsTrue(db.WillAddressResultInUniqueUrl(new NodeKey(sibling), "specs"));

        // the commit-time backstop suffixes an update that would collide:
        db.UpdateAddress(new NodeKey(sibling), "info");
        Assert.AreEqual("info-2", db.Get<UrlPage>(sibling).Slug);
        Assert.AreEqual("/tv/sony-x90/info-2", db.GetUrl(sibling));
        Assert.AreEqual("/tv/sony-x90/info", db.GetUrl(sonyInfo)); // the original keeps its URL
    }

    [TestMethod]
    public void TreeManager_Domains_RouteTheSamePathToDifferentNodes() {
        var dm = new Datamodel();
        dm.Add<UrlPage>();
        dm.Add<UrlPageTree>();
        var rootOne = Guid.NewGuid();
        var rootTwo = Guid.NewGuid();
        var manager = new TreeUrlManager(new TreeUrlManagerOptions() {
            ParentRelationName = "UrlPageTree",
            Domains = [
                new UrlDomain() { Host = "one.example", RootId = rootOne },
                new UrlDomain() { Host = "two.example", RootId = rootTwo },
            ],
        });
        var data = new DataStoreLocal(dm, new SettingsLocal(), new IOProviderMemory(), urlManager: manager);
        data.Open(true, true);
        using var db = new NodeStore(data);

        db.Insert(new UrlPage() { Id = rootOne, Title = "Site one", Slug = "site-one" });
        db.Insert(new UrlPage() { Id = rootTwo, Title = "Site two", Slug = "site-two" });
        var contactOne = insert(db, "Contact one", "contact-us", rootOne);
        var contactTwo = insert(db, "Contact two", "contact-us", rootTwo); // same path, other domain - no suffixing

        Assert.AreEqual("contact-us", db.Get<UrlPage>(contactOne).Slug);
        Assert.AreEqual("contact-us", db.Get<UrlPage>(contactTwo).Slug);
        Assert.AreEqual("https://one.example/contact-us", db.GetUrl(contactOne, absolute: true));
        Assert.AreEqual("https://two.example/contact-us", db.GetUrl(contactTwo, absolute: true));

        Assert.IsTrue(db.TryParseUrl("https://one.example/contact-us", out var keys1));
        Assert.AreEqual(contactOne, db.Get<UrlPage>(keys1.NodeKey).Id);
        Assert.IsTrue(db.TryParseUrl("https://two.example/contact-us", out var keys2));
        Assert.AreEqual(contactTwo, db.Get<UrlPage>(keys2.NodeKey).Id);
        // an unknown host falls back to the first domain:
        Assert.IsTrue(db.TryParseUrl("http://localhost:5000/contact-us", out var keys3));
        Assert.AreEqual(contactOne, db.Get<UrlPage>(keys3.NodeKey).Id);
        // the root node itself is served at "/":
        Assert.IsTrue(db.TryParseUrl("https://two.example/", out var rootKeys));
        Assert.AreEqual(rootTwo, db.Get<UrlPage>(rootKeys.NodeKey).Id);
    }

    // ------------------------------------------------------------------ rename proof links in HTML

    [TestMethod]
    public void HtmlLinks_AreStoredAsTokens_AndServedAsCurrentUrls() {
        using var db = open(withTreeManager: true);
        var (tv, _, sonyInfo, _, _, pixelInfo) = seedCatalog(db);

        var page = db.Get<UrlPage>(pixelInfo);
        page.Body = "<p>Compare with <a href=\"/tv/sony-x90/info\">the Sony X90</a> or <a href=\"https://external.example/x\">elsewhere</a>.</p>";
        db.Update(page);

        var stored = storedBody(db, pixelInfo)!;
        StringAssert.Contains(stored, "rdb:"); // the internal link survives renames because it is id based
        Assert.IsFalse(stored.Contains("/tv/sony-x90/info"), "The stored value should not contain the public URL. ");
        StringAssert.Contains(stored, "https://external.example/x"); // external links pass through untouched

        Assert.IsTrue(db.Get<UrlPage>(pixelInfo).Body.Contains("/tv/sony-x90/info"), "Reads should emit the current public URL. ");

        // the rename that motivated the redesign: one write, and the stored HTML is untouched
        db.UpdateAddress(new NodeKey(tv), "television");
        Assert.AreEqual(stored, storedBody(db, pixelInfo));
        Assert.IsTrue(db.Get<UrlPage>(pixelInfo).Body.Contains("/television/sony-x90/info"), "Reads should follow the rename. ");
    }

    [TestMethod]
    public void HtmlLinks_InternalizationIsIdempotent_AndUnresolvableLinksPassThrough() {
        using var db = open(withTreeManager: true);
        var (_, _, sonyInfo, _, _, pixelInfo) = seedCatalog(db);

        var page = db.Get<UrlPage>(pixelInfo);
        page.Body = "<p><a href=\"/tv/sony-x90/info\">a</a> <a href=\"/no/such/page\">b</a> <a href=\"#anchor\">c</a> <a href=\"mailto:x@y.z\">d</a></p>";
        db.Update(page);
        var storedOnce = storedBody(db, pixelInfo)!;
        StringAssert.Contains(storedOnce, "/no/such/page"); // unresolvable: left as-is
        StringAssert.Contains(storedOnce, "#anchor");
        StringAssert.Contains(storedOnce, "mailto:x@y.z");

        // saving the externalized form back produces the same stored value:
        var served = db.Get<UrlPage>(pixelInfo);
        db.Update(served);
        Assert.AreEqual(storedOnce, storedBody(db, pixelInfo));

        // a deleted target becomes a dead link, not an error:
        db.Delete(sonyInfo);
        StringAssert.Contains(db.Get<UrlPage>(pixelInfo).Body, "href=\"#\"");
    }

    // ------------------------------------------------------------------ typed managers

    // A manager written against the typed object model, like the Website.Simple sample: it
    // materializes node objects whose HTML properties externalize during mapping, which calls back
    // into the manager. The nested-externalization guard is what keeps that from recursing forever
    // when pages link to each other.
    class TypedTestManager(Func<NodeStore> db) : IUrlManager {
        public void Initialize(IDataStore store) { }
        public string? TryGetUrl(NodeMeta meta, bool absolute) {
            var page = db().Get<UrlPage>(meta.Id);
            var segments = new List<string>();
            while (true) {
                if (page.Slug.Length == 0 || segments.Count > 32) return null;
                segments.Insert(0, page.Slug);
                if (!page.Parent.TryGet(out page!)) break;
            }
            return "/" + string.Join('/', segments);
        }
        public IdKeyWithCultureId[] GetMatches(string completeUrl) {
            var last = UrlUtil.GetLastSegment(completeUrl);
            if (last == null) return [];
            var path = UrlUtil.GetPath(completeUrl);
            return db().GetNodeIdsFromAddress(last)
                .Where(c => db().Datastore.TryGetNodeMeta(c.IdKey, out var m) && TryGetUrl(m, false) == path)
                .ToArray();
        }
        public bool WillAddressResultInUniqueUrl(NodeKey node, Guid cultureId, string address) => true;
    }

    [TestMethod]
    public void TypedManager_MutuallyLinkedPages_DoNotRecurse() {
        NodeStore db = null!;
        var manager = new TypedTestManager(() => db);
        var dm = new Datamodel();
        dm.Add<UrlPage>();
        dm.Add<UrlPageTree>();
        var data = new DataStoreLocal(dm, new SettingsLocal(), new IOProviderMemory(), urlManager: manager);
        data.Open(true, true);
        db = new NodeStore(data);
        try {
            var a = insert(db, "A", "a");
            var b = insert(db, "B", "b");
            var pageA = db.Get<UrlPage>(a); pageA.Body = "<a href=\"/b\">b</a>"; db.Update(pageA);
            var pageB = db.Get<UrlPage>(b); pageB.Body = "<a href=\"/a\">a</a>"; db.Update(pageB);
            // both bodies internalized to tokens, and reading either page terminates and resolves the link:
            StringAssert.Contains(storedBody(db, a)!, "rdb:");
            StringAssert.Contains(db.Get<UrlPage>(a).Body, "href=\"/b\"");
            StringAssert.Contains(db.Get<UrlPage>(b).Body, "href=\"/a\"");
        } finally {
            db.Dispose();
        }
    }

    // ------------------------------------------------------------------ persistence

    [TestMethod]
    public void SharedSegments_SurviveStateSaveAndReopen() {
        var io = new IOProviderMemory();
        Guid sonyInfo, pixelInfo, tv;
        using (var db = open(withTreeManager: true, io)) {
            (tv, _, sonyInfo, _, _, pixelInfo) = seedCatalog(db);
            var page = db.Get<UrlPage>(pixelInfo);
            page.Body = "<p><a href=\"/tv/sony-x90/info\">sony</a></p>";
            db.Update(page);
            db.Datastore.SaveIndexStates();
        }
        using (var db = open(withTreeManager: true, io)) {
            Assert.AreEqual(2, db.GetNodeIdsFromAddress("info").Length);
            Assert.AreEqual("/tv/sony-x90/info", db.GetUrl(sonyInfo));
            Assert.AreEqual("/mobile/pixel/info", db.GetUrl(pixelInfo));
            Assert.IsTrue(db.TryParseUrl("/mobile/pixel/info", out var keys));
            Assert.AreEqual(pixelInfo, db.Get<UrlPage>(keys.NodeKey).Id);
            StringAssert.Contains(db.Get<UrlPage>(pixelInfo).Body, "/tv/sony-x90/info");
        }
    }
}
