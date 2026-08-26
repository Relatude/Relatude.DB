using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.FileConversion;
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
    public FileValue File { get; set; } = FileValue.Empty;
    public UrlPageTree.Parent Parent { get; set; } = new();
    public UrlPageTree.Children Children { get; set; } = new();
}
public class UrlPageTree : OneToMany<UrlPage, UrlPage> {
    public class Parent : One { }
    public class Children : Many { }
}
// A second node type that hangs under a page through a different relation, so URL building has to
// pick the parent relation that applies to the node's own type. Only the child side is declared as
// a property, so the shared UrlPage model above stays usable without this relation.
[Node]
public class UrlDoc {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(DisplayName = true)]
    public string Title { get; set; } = "";
    [AddressProperty]
    public string Slug { get; set; } = "";
    public UrlPageDocs.Page Page { get; set; } = new();
}
public class UrlPageDocs : OneToMany<UrlPage, UrlDoc> {
    public class Page : One { }   // declared as a property on UrlDoc, relates to UrlPage
    public class Docs : Many { }  // the other end; no property for it, so UrlPage stays independent of this relation
}
#endregion

[TestClass]
public class UrlManagerTests {

    static NodeStore open(bool withTreeManager, IIOProvider? io = null) {
        var dm = new Datamodel();
        dm.Add<UrlPage>();
        dm.Add<UrlPageTree>();
        IUrlManager? manager = withTreeManager ? new DefaultUrlManager(new DefaultUrlManagerOptions() {
            Parents = [new UrlParentRelation() { ParentRelationName = "UrlPageTree" }],
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

    // ------------------------------------------------------------------ the flat default manager
    // (no manager configured: the store falls back to a TreeUrlManager without a parent relation,
    // so every node is top level and addresses behave like complete, globally unique paths)

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
    public void FlatDefault_AddressesStayGloballyUnique_WithSuffixOnCollision() {
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
    public void FlatDefault_UrlsAndParsing() {
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
        var manager = new DefaultUrlManager(new DefaultUrlManagerOptions() {
            Parents = [new UrlParentRelation() { ParentRelationName = "UrlPageTree" }],
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

    // ------------------------------------------------------------------ several parent relations

    [TestMethod]
    public void SeveralParentRelations_EachNodeTypeUsesTheFirstThatApplies() {
        var dm = new Datamodel();
        dm.Add<UrlPage>();
        dm.Add<UrlPageTree>();
        dm.Add<UrlDoc>();
        dm.Add<UrlPageDocs>();
        var manager = new DefaultUrlManager(new DefaultUrlManagerOptions() {
            Parents = [
                new UrlParentRelation() { ParentRelationName = "UrlPageTree" }, // applies to UrlPage
                new UrlParentRelation() { ParentRelationName = "UrlPageDocs" }, // applies to UrlDoc
            ],
        });
        var data = new DataStoreLocal(dm, new SettingsLocal(), new IOProviderMemory(), urlManager: manager);
        data.Open(true, true);
        using var db = new NodeStore(data);

        var section = insert(db, "Docs section", "docs");
        var page = insert(db, "Guide", "guide", section);
        var doc = new UrlDoc() { Id = Guid.NewGuid(), Title = "Readme", Slug = "readme" };
        db.Insert(doc);
        db.Execute(new Transaction(db).Relation.Relate<UrlPageDocs>(page, doc.Id));

        // the page walks the page tree, the doc walks the doc relation - the chain crosses both:
        Assert.AreEqual("/docs/guide", db.GetUrl(page));
        Assert.AreEqual("/docs/guide/readme", db.GetUrl(doc.Id));
        Assert.IsTrue(db.TryParseUrl("/docs/guide/readme", out var keys));
        Assert.AreEqual(doc.Id, db.Get<UrlDoc>(keys.NodeKey).Id);

        // a doc without a page is top level, since no configured relation gives it a parent:
        var orphan = new UrlDoc() { Id = Guid.NewGuid(), Title = "Loose", Slug = "loose" };
        db.Insert(orphan);
        Assert.AreEqual("/loose", db.GetUrl(orphan.Id));

        // uniqueness is still judged on the complete URL, across the relation kinds:
        var sameSlugElsewhere = new UrlDoc() { Id = Guid.NewGuid(), Title = "Readme 2", Slug = "readme" };
        db.Insert(sameSlugElsewhere);
        db.Execute(new Transaction(db).Relation.Relate<UrlPageDocs>(section, sameSlugElsewhere.Id));
        Assert.AreEqual("readme", db.Get<UrlDoc>(sameSlugElsewhere.Id).Slug); // another parent, no suffix
        Assert.AreEqual("/docs/readme", db.GetUrl(sameSlugElsewhere.Id));
    }

    // ------------------------------------------------------------------ url formats, roots, asset styles

    static NodeStore openWithOptions(DefaultUrlManagerOptions options, IIOProvider? io = null) {
        var dm = new Datamodel();
        dm.Add<UrlPage>();
        dm.Add<UrlPageTree>();
        var data = new DataStoreLocal(dm, new SettingsLocal(), io ?? new IOProviderMemory(), urlManager: new DefaultUrlManager(options));
        data.Open(true, true);
        return new NodeStore(data);
    }

    [TestMethod]
    public void UrlFormat_AddressOrIntId_GivesNodesWithoutAnAddressAnIdUrl() {
        using var db = openWithOptions(new DefaultUrlManagerOptions() { UrlFormat = NodeUrlFormat.AddressOrIntId });
        var withAddress = insert(db, "Hello", "hello");
        var withoutAddress = insert(db, "No address", "");
        Assert.AreEqual("/hello", db.GetUrl(withAddress));
        Assert.IsTrue(db.Datastore.TryGetNodeMeta(withoutAddress, out var meta));
        Assert.AreEqual("/" + meta.InternalId, db.GetUrl(withoutAddress));
        // both forms parse back:
        Assert.IsTrue(db.TryParseUrl("/hello", out var byAddress));
        Assert.AreEqual(withAddress, db.Get<UrlPage>(byAddress.NodeKey).Id);
        Assert.IsTrue(db.TryParseUrl("/" + meta.InternalId, out var byId));
        Assert.AreEqual(withoutAddress, db.Get<UrlPage>(byId.NodeKey).Id);
    }

    [TestMethod]
    public void UrlFormat_IntIdAndAddress_IsResolvedByIdAlone_SoOldUrlsSurviveRenames() {
        using var db = openWithOptions(new DefaultUrlManagerOptions() { UrlFormat = NodeUrlFormat.IntIdAndAddress, Parents = [new UrlParentRelation() { ParentRelationName = "UrlPageTree" }] });
        var section = insert(db, "Docs", "docs");
        var page = insert(db, "Intro", "intro", section);
        Assert.IsTrue(db.Datastore.TryGetNodeMeta(page, out var meta));
        var url = db.GetUrl(page);
        Assert.AreEqual("/" + meta.InternalId + "/docs/intro", url);
        Assert.IsTrue(db.TryParseUrl(url, out var keys));
        Assert.AreEqual(page, db.Get<UrlPage>(keys.NodeKey).Id);
        // the readable part is cosmetic: a stale path still resolves through the id
        db.UpdateAddress(new NodeKey(section), "documentation");
        Assert.AreEqual("/" + meta.InternalId + "/documentation/intro", db.GetUrl(page));
        Assert.IsTrue(db.TryParseUrl(url, out var stale), "The old URL should still resolve by its id. ");
        Assert.AreEqual(page, db.Get<UrlPage>(stale.NodeKey).Id);
        // duplicate addresses are fine in id formats, no suffixing:
        var page2 = insert(db, "Intro 2", "intro", section);
        Assert.AreEqual("intro", db.Get<UrlPage>(page2).Slug);
    }

    [TestMethod]
    public void UrlFormat_GuidIdOnly() {
        using var db = openWithOptions(new DefaultUrlManagerOptions() { UrlFormat = NodeUrlFormat.GuidIdOnly });
        var page = insert(db, "Hello", "hello");
        Assert.AreEqual("/" + page, db.GetUrl(page));
        Assert.IsTrue(db.TryParseUrl("/" + page, out var keys));
        Assert.AreEqual(page, db.Get<UrlPage>(keys.NodeKey).Id);
        Assert.IsFalse(db.TryParseUrl("/hello", out _)); // addresses are not part of URL space in this format
    }

    [TestMethod]
    public void PrimaryBaseAddress_PrefixesEveryPageUrl() {
        using var db = openWithOptions(new DefaultUrlManagerOptions() { PrimaryBaseAddress = "/content", IncludeTrailingSlash = true });
        var page = insert(db, "Hello", "hello");
        Assert.AreEqual("/content/hello/", db.GetUrl(page));
        Assert.IsTrue(db.TryParseUrl("/content/hello/", out var keys));
        Assert.AreEqual(page, db.Get<UrlPage>(keys.NodeKey).Id);
        Assert.IsFalse(db.TryParseUrl("/hello", out _)); // outside the root
    }

    [TestMethod]
    public void PrimaryBaseAddress_AppliesToAssetUrlsToo() {
        using var db = openWithOptions(new DefaultUrlManagerOptions() { PrimaryBaseAddress = "/app" });
        var page = insert(db, "No address", ""); // unroutable: GetUrl falls back to an asset token url
        var url = db.GetUrl(page);
        StringAssert.StartsWith(url, "/app/assets/");
        Assert.IsTrue(db.TryParseUrl(url, out var keys));
        Assert.AreEqual(UrlTarget.Node, keys.Target);
        Assert.AreEqual(page, db.Get<UrlPage>(keys.NodeKey).Id);
        Assert.IsFalse(db.TryParseUrl(url["/app".Length..], out _)); // without the primary base
    }

    [TestMethod]
    public void PrimaryBaseAddress_WithHost_MakesEveryUrlAbsolute() {
        using var db = openWithOptions(new DefaultUrlManagerOptions() { PrimaryBaseAddress = "https://www.example.com" });
        var page = insert(db, "Hello", "hello");
        Assert.AreEqual("https://www.example.com/hello", db.GetUrl(page));
        var unroutable = insert(db, "No address", "");
        StringAssert.StartsWith(db.GetUrl(unroutable), "https://www.example.com/assets/");
        Assert.IsTrue(db.TryParseUrl("https://www.example.com/hello", out var keys));
        Assert.AreEqual(page, db.Get<UrlPage>(keys.NodeKey).Id);
        Assert.IsTrue(db.TryParseUrl("/hello", out _)); // matched by path, so relative requests resolve too
    }

    [TestMethod]
    public void BaseAddressPages_PrefixesEveryPageUrl() {
        using var db = openWithOptions(new DefaultUrlManagerOptions() { BaseAddressPages = "/app" });
        var page = insert(db, "Hello", "hello");
        Assert.AreEqual("/app/hello", db.GetUrl(page));
        Assert.IsTrue(db.TryParseUrl("/app/hello", out var keys));
        Assert.AreEqual(page, db.Get<UrlPage>(keys.NodeKey).Id);
        Assert.IsFalse(db.TryParseUrl("/hello", out _)); // outside the base
        Assert.IsFalse(db.TryParseUrl("/apphello", out _)); // the base must end on a segment boundary
    }

    [TestMethod]
    public void PrimaryBaseAddress_ComesBeforeTheLaneBases() {
        using var db = openWithOptions(new DefaultUrlManagerOptions() {
            PrimaryBaseAddress = "/app",
            BaseAddressPages = "/content",
            BaseAddressAssets = "/files",
        });
        var page = insert(db, "Hello", "hello");
        Assert.AreEqual("/app/content/hello", db.GetUrl(page)); // primary, then the page base
        Assert.IsTrue(db.TryParseUrl("/app/content/hello", out var keys));
        Assert.AreEqual(page, db.Get<UrlPage>(keys.NodeKey).Id);

        var unroutable = insert(db, "No address", "");
        var assetUrl = db.GetUrl(unroutable);
        StringAssert.StartsWith(assetUrl, "/app/files/assets/"); // primary, then the asset base
        Assert.IsTrue(db.TryParseUrl(assetUrl, out var assetKeys));
        Assert.AreEqual(unroutable, db.Get<UrlPage>(assetKeys.NodeKey).Id);
    }

    [TestMethod]
    public void LaneBaseWithHost_ReplacesThePrimaryBase() {
        const string cdn = "https://cdn.example.com";
        using var db = openWithOptions(new DefaultUrlManagerOptions() {
            PrimaryBaseAddress = "/app",
            BaseAddressAssets = cdn,
        });
        var page = insert(db, "Hello", "hello");
        Assert.AreEqual("/app/hello", db.GetUrl(page)); // pages keep the primary base
        var unroutable = insert(db, "No address", "");
        var assetUrl = db.GetUrl(unroutable);
        StringAssert.StartsWith(assetUrl, cdn + "/assets/"); // the CDN origin stands alone, no "/app"
        Assert.IsTrue(db.TryParseUrl(assetUrl, out var keys));
        Assert.AreEqual(unroutable, db.Get<UrlPage>(keys.NodeKey).Id);
    }

    [TestMethod]
    public void BaseAddressPages_WithHost_MakesPageUrlsAbsolute() {
        using var db = openWithOptions(new DefaultUrlManagerOptions() { BaseAddressPages = "https://www.example.com" });
        var page = insert(db, "Hello", "hello");
        Assert.AreEqual("https://www.example.com/hello", db.GetUrl(page)); // absolute even without asking
        Assert.IsTrue(db.TryParseUrl("https://www.example.com/hello", out var keys));
        Assert.AreEqual(page, db.Get<UrlPage>(keys.NodeKey).Id);
        Assert.IsTrue(db.TryParseUrl("/hello", out _)); // matched by path, so relative requests resolve too
    }

    [TestMethod]
    public void BaseAddressAssets_PrefixesEveryAssetUrl() {
        const string cdn = "https://cdn.example.com/files";
        using var db = openWithOptions(new DefaultUrlManagerOptions() { BaseAddressAssets = cdn });
        var page = insert(db, "No address", ""); // unroutable: GetUrl falls back to an asset token url
        var url = db.GetUrl(page);
        StringAssert.StartsWith(url, cdn + "/assets/");
        Assert.IsTrue(db.TryParseUrl(url, out var keys));
        Assert.AreEqual(UrlTarget.Node, keys.Target);
        Assert.AreEqual(page, db.Get<UrlPage>(keys.NodeKey).Id);
        var relative = url[cdn.Length..]; // "/assets/{token}"
        Assert.IsTrue(db.TryParseUrl("/files" + relative, out _)); // matched by path, so relative requests resolve too
        Assert.IsFalse(db.TryParseUrl(relative, out _)); // without the base path
    }

    [TestMethod]
    public void AssetUrls_SignedTokens_RejectTampering() {
        var key = Guid.NewGuid();
        using var db = openWithOptions(new DefaultUrlManagerOptions() { AssetUrlSignatureKey = key });
        var page = insert(db, "No address", ""); // unroutable: GetUrl falls back to a signed asset token url
        var url = db.GetUrl(page);
        StringAssert.StartsWith(url, "/assets/");
        Assert.IsTrue(db.TryParseUrl(url, out var keys));
        Assert.AreEqual(UrlTarget.Node, keys.Target);
        Assert.AreEqual(page, db.Get<UrlPage>(keys.NodeKey).Id);
        // flip the last character of the signature: the URL must stop resolving
        var tampered = url[..^1] + (url[^1] == 'A' ? 'B' : 'A');
        Assert.IsFalse(db.TryParseUrl(tampered, out _));
        // an unsigned token is rejected too:
        var unsigned = url[..url.LastIndexOf('.')];
        Assert.IsFalse(db.TryParseUrl(unsigned, out _));
    }

    [TestMethod]
    public void AssetUrls_UnderPageUrl_BuildOnTheOwnersPageUrl() {
        var options = new DefaultUrlManagerOptions() { AssetUrlStyle = AssetUrlStyle.UnderPageUrl };
        var manager = new DefaultUrlManager(options);
        var dm = new Datamodel();
        dm.Add<UrlPage>();
        dm.Add<UrlPageTree>();
        var data = new DataStoreLocal(dm, new SettingsLocal(), new IOProviderMemory(), urlManager: manager);
        data.Open(true, true);
        using var db = new NodeStore(data);
        var page = insert(db, "Docs", "docs");
        Assert.IsTrue(db.Datastore.TryGetNodeMeta(page, out var meta));

        // asset placement is the manager's job; the token is opaque to it
        var url = manager.GetAssetUrl(new AssetUrl {
            Token = "p123abc",
            Target = UrlTarget.Property,
            Owner = new NodeKey(meta.InternalId),
            FileName = "pic.jpg",
        }, absolute: false);
        Assert.AreEqual("/docs/pic.jpg?asset=p123abc", url);
        Assert.AreEqual("p123abc", manager.TryGetAssetToken(url)?.Token);
        Assert.AreEqual("p123abc", manager.TryGetAssetToken("https://example.com/docs/pic.jpg?asset=p123abc")?.Token);
        Assert.IsNull(manager.TryGetAssetToken("/docs/pic.jpg")); // no token, a page URL
        // and the default placement still parses (an owner without a page URL falls back to it):
        Assert.AreEqual("t0ken", manager.TryGetAssetToken("/assets/t0ken/pic.jpg")?.Token);
    }

    // ------------------------------------------------------------------ readable asset url formats

    static FileAdjustmentImage exampleImageAdjustment() => new() {
        RequestedFormat = FileFormat.Jpeg,
        Width = 100,
        Height = 200,
        Quality = 80,
        CropMode = ImageCropMode.Fill,
        Saturation = -50,
        Zoom = 1.5,
        FocusX = 10,
        InvertLuminance = true,
        BackgroundColor = "#aabbcc",
        TimeOffsetMs = 4000,
    };
    static void assertEqualAdjustments(FileAdjustmentBase expected, FileAdjustmentBase? actual) {
        Assert.IsNotNull(actual);
        CollectionAssert.AreEqual(expected.ToBytes(), actual.ToBytes(), "The adjustment should round trip losslessly. ");
    }

    [TestMethod]
    public void AdjustmentCodec_QueryString_RoundTripsLosslessly() {
        var image = exampleImageAdjustment();
        Assert.IsTrue(FileAdjustmentUrlCodec.TryToQueryString(image, out var query));
        StringAssert.Contains(query, "w=100");
        StringAssert.Contains(query, "sat=-50");
        assertEqualAdjustments(image, FileAdjustmentUrlCodec.TryParseQuery("/x?" + query));

        var video = new FileAdjustmentVideo() { RequestedFormat = FileFormat.Mp4, Width = 240, Height = 200, TargetBitRateInMbps = 2.5, CropNotZoom = true };
        Assert.IsTrue(FileAdjustmentUrlCodec.TryToQueryString(video, out var videoQuery));
        assertEqualAdjustments(video, FileAdjustmentUrlCodec.TryParseQuery("/x?" + videoQuery));

        var meta = new FileAdjustmentMeta();
        Assert.IsTrue(FileAdjustmentUrlCodec.TryToQueryString(meta, out var metaQuery));
        assertEqualAdjustments(meta, FileAdjustmentUrlCodec.TryParseQuery("/x?" + metaQuery));

        Assert.IsNull(FileAdjustmentUrlCodec.TryParseQuery("/x?utm_source=mail")); // no adjustment keys present
    }

    [TestMethod]
    public void AdjustmentCodec_ShortString_RoundTripsLosslessly() {
        var image = exampleImageAdjustment();
        Assert.IsTrue(FileAdjustmentUrlCodec.TryToShortString(image, out var shortString));
        assertEqualAdjustments(image, FileAdjustmentUrlCodec.TryParseShortString(shortString));
        // the user facing aesthetics: no separators, keys run into values
        assertEqualAdjustments(
            new FileAdjustmentImage() { RequestedFormat = FileFormat.Jpeg, Width = 100, Height = 200 },
            FileAdjustmentUrlCodec.TryParseShortString("w100h200fjpeg"));
        // format names running into the next key are resolved by backtracking:
        assertEqualAdjustments(
            new FileAdjustmentVideo() { RequestedFormat = FileFormat.Mp4, Width = 100 },
            FileAdjustmentUrlCodec.TryParseShortString("kvfmp4w100"));
        Assert.IsNull(FileAdjustmentUrlCodec.TryParseShortString("photo.jpg")); // a file name, not an adjustment
        Assert.IsNull(FileAdjustmentUrlCodec.TryParseShortString("hello"));
    }

    [TestMethod]
    public void AdjustmentCodec_OmitsValuesEqualToTheDefaults() {
        // Jpeg is the default format of an image adjustment, so it is not part of the URL:
        Assert.IsTrue(FileAdjustmentUrlCodec.TryToQueryString(new FileAdjustmentImage() { Width = 100 }, out var query));
        Assert.AreEqual("w=100", query);
        Assert.IsTrue(FileAdjustmentUrlCodec.TryToShortString(new FileAdjustmentImage() { Width = 100, Height = 200 }, out var shortString));
        Assert.AreEqual("w100h200", shortString);
        // and a meta adjustment with its constructor defaults is just its kind:
        Assert.IsTrue(FileAdjustmentUrlCodec.TryToQueryString(new FileAdjustmentMeta(), out var metaQuery));
        Assert.AreEqual("k=m", metaQuery);
        assertEqualAdjustments(new FileAdjustmentImage() { Width = 100 }, FileAdjustmentUrlCodec.TryParseQuery("/x?w=100"));
    }

    [TestMethod]
    public void AssetUrlFormat_QueryParameters_KeepsTheAdjustmentReadable() {
        var manager = new DefaultUrlManager(new DefaultUrlManagerOptions() { AssetUrlFormat = AssetUrlFormat.QueryParameters });
        var adjustment = exampleImageAdjustment();
        var url = manager.GetAssetUrl(new AssetUrl {
            Token = "aFULLTOKEN",
            BaseToken = "pBASETOKEN",
            Target = UrlTarget.PropertyAdjusted,
            Owner = new NodeKey(1),
            FileName = "pic.jpg",
            Adjustment = adjustment,
        }, absolute: false);
        StringAssert.StartsWith(url, "/assets/pBASETOKEN/pic.jpg?"); // the token addresses the original file
        StringAssert.Contains(url, "w=100");
        var match = manager.TryGetAssetToken(url);
        Assert.IsNotNull(match);
        Assert.AreEqual("pBASETOKEN", match.Token);
        assertEqualAdjustments(adjustment, match.Adjustment);
    }

    [TestMethod]
    public void AssetUrlFormat_FriendlyShortString_PutsTheAdjustmentInThePath() {
        var manager = new DefaultUrlManager(new DefaultUrlManagerOptions() { AssetUrlFormat = AssetUrlFormat.FriendlyShortString });
        var adjustment = new FileAdjustmentImage() { RequestedFormat = FileFormat.Jpeg, Width = 100, Height = 200 };
        var url = manager.GetAssetUrl(new AssetUrl {
            Token = "aFULLTOKEN",
            BaseToken = "pBASETOKEN",
            Target = UrlTarget.PropertyAdjusted,
            Owner = new NodeKey(1),
            FileName = "pic.jpg",
            Adjustment = adjustment,
        }, absolute: false);
        Assert.AreEqual("/assets/pBASETOKEN/fjpegw100h200/pic.jpg", url); // Jpeg is explicit (the default is the adaptive Image format)
        var match = manager.TryGetAssetToken(url);
        Assert.IsNotNull(match);
        Assert.AreEqual("pBASETOKEN", match.Token);
        assertEqualAdjustments(adjustment, match.Adjustment);
        // a plain file URL in the same format has no adjustment segment:
        var plain = manager.TryGetAssetToken("/assets/pBASETOKEN/pic.jpg");
        Assert.IsNotNull(plain);
        Assert.IsNull(plain.Adjustment);
    }

    [TestMethod]
    public void AdaptiveImageFormat_ResolvesAgainstOriginalAndDefaults() {
        // the adaptive format is the default for image adjustments, and URLs without f imply it:
        Assert.AreEqual(FileFormat.Image, new FileAdjustmentImage().RequestedFormat);
        Assert.IsTrue(FileAdjustmentUrlCodec.TryToQueryString(new FileAdjustmentImage() { Width = 100 }, out var query));
        Assert.AreEqual("w=100", query);
        Assert.AreEqual(FileFormat.Image, ((FileAdjustmentImage)FileAdjustmentUrlCodec.TryParseQuery("/x?w=100")!).RequestedFormat);

        // resized: the default format and quality apply
        var resized = new FileAdjustmentImage() { Width = 100 };
        var resolved = resized.ResolveAdaptiveFormat(FileFormat.Png, 800, 600, FileFormat.Webp, 85);
        Assert.AreEqual(FileFormat.Webp, resolved.RequestedFormat);
        Assert.AreEqual(85, resolved.Quality);
        Assert.AreEqual(FileFormat.Image, resized.RequestedFormat); // the request itself is never changed
        // a given quality wins over the default:
        Assert.AreEqual(60, new FileAdjustmentImage() { Width = 100, Quality = 60 }.ResolveAdaptiveFormat(FileFormat.Png, 800, 600, FileFormat.Webp, 85).Quality);

        // a gif that keeps its dimensions stays a gif, preserving animations and palette:
        var sameSize = new FileAdjustmentImage() { Width = 800, Height = 600 };
        Assert.AreEqual(FileFormat.Gif, sameSize.ResolveAdaptiveFormat(FileFormat.Gif, 800, 600, FileFormat.Webp, 85).RequestedFormat);
        Assert.IsNull(sameSize.ResolveAdaptiveFormat(FileFormat.Gif, 800, 600, FileFormat.Webp, 85).Quality);
        // a resized or edited gif becomes the default format:
        Assert.AreEqual(FileFormat.Webp, new FileAdjustmentImage() { Width = 400 }.ResolveAdaptiveFormat(FileFormat.Gif, 800, 600, FileFormat.Webp, 85).RequestedFormat);
        Assert.AreEqual(FileFormat.Webp, new FileAdjustmentImage() { Width = 800, Height = 600, Saturation = -50 }.ResolveAdaptiveFormat(FileFormat.Gif, 800, 600, FileFormat.Webp, 85).RequestedFormat);

        // explicit formats pass through unresolved:
        var explicitJpeg = new FileAdjustmentImage() { RequestedFormat = FileFormat.Jpeg, Width = 100 };
        Assert.AreSame(explicitJpeg, explicitJpeg.ResolveAdaptiveFormat(FileFormat.Png, 800, 600, FileFormat.Webp, 85));

        // no adjustments at all means: serve the original file untouched
        Assert.IsTrue(new FileAdjustmentImage().IsPlainRequest());
        Assert.IsFalse(new FileAdjustmentImage() { Width = 100 }.IsPlainRequest());
        Assert.IsFalse(new FileAdjustmentImage() { RequestedFormat = FileFormat.Jpeg }.IsPlainRequest());
    }

    [TestMethod]
    public async Task AdaptiveImageFormat_PlainRequest_ServesTheOriginalFile() {
        using var db = open(withTreeManager: false);
        var pageId = insert(db, "Docs", "docs");
        var page = db.Get<UrlPage>(pageId);
        var data = new byte[500];
        new Random(42).NextBytes(data);
        await db.FileUploadAsync(page, p => p.File, data, "photo.png");
        page = db.Get<UrlPage>(pageId);

        var state = await db.Datastore.GetFileStreamAndState(page.File.PropertyPath!, new FileAdjustmentImage());
        Assert.IsTrue(state.IsReady);
        Assert.AreEqual(page.File.Format, state.RequestedFormat); // the original's format: nothing was converted
        using var ms = new MemoryStream();
        await state.Stream.CopyToAsync(ms);
        Assert.IsTrue(data.SequenceEqual(ms.ToArray()), "A plain adaptive request should serve the original bytes. ");
        Assert.IsTrue(db.Datastore.IsFileReady(page.File.PropertyPath!, new FileAdjustmentImage(), requestIfNot: false));
    }

    [TestMethod]
    public void PropertyPathFormat_QueryParameters_AddressesThePropertyByNameAndId() {
        var manager = new DefaultUrlManager(new DefaultUrlManagerOptions() {
            PropertyPathFormat = PropertyPathUrlFormat.QueryParameters,
            AssetUrlFormat = AssetUrlFormat.QueryParameters,
        });
        var dm = new Datamodel();
        dm.Add<UrlPage>();
        dm.Add<UrlPageTree>();
        var data = new DataStoreLocal(dm, new SettingsLocal(), new IOProviderMemory(), urlManager: manager);
        data.Open(true, true);
        using var db = new NodeStore(data);
        var pageId = insert(db, "Docs", "docs");
        Assert.IsTrue(db.Datastore.TryGetNodeMeta(pageId, out var meta));
        var bodyPropId = db.Datastore.Datamodel.NodeTypesByFullName[typeof(UrlPage).FullName!]
            .AllProperties.Values.First(p => p.CodeName == nameof(UrlPage.Body)).Id;
        var propertyPath = new NodePath(new NodeKey(meta.InternalId)).CreatePropertyPath(bodyPropId);

        // outbound: the property is addressed by name and id, the version rides as a cache buster
        var url = manager.GetAssetUrl(new AssetUrl {
            Token = "pTOKEN",
            Target = UrlTarget.Property,
            Owner = propertyPath.NodePath.NodeKey,
            FileName = "pic.jpg",
            PropertyPath = propertyPath,
            ContentVersionId = "abc123",
        }, absolute: false);
        Assert.AreEqual($"/assets/pic.jpg?pn=Body&pid={meta.InternalId}&v=abc123", url);

        // inbound, through the store, with a readable adjustment on top:
        Assert.IsTrue(db.TryParseUrl(url + "&w=100", out var keys));
        Assert.AreEqual(UrlTarget.PropertyAdjusted, keys.Target);
        Assert.AreEqual(bodyPropId, keys.PropertyPath!.PropertyId);
        Assert.AreEqual(meta.InternalId, keys.NodeKey.Int);
        Assert.AreEqual(100, ((FileAdjustmentImage)keys.Adjustment!).Width);
        // without adjustment parameters it is the plain file:
        Assert.IsTrue(db.TryParseUrl(url, out var plain));
        Assert.AreEqual(UrlTarget.Property, plain.Target);
        // unknown property or node: no match
        Assert.IsFalse(db.TryParseUrl($"/assets/pic.jpg?pn=Nope&pid={meta.InternalId}", out _));
        Assert.IsFalse(db.TryParseUrl("/assets/pic.jpg?pn=Body&pid=99999", out _));
    }

    [TestMethod]
    public void PropertyPathFormat_FriendlyShortString_PutsThePropertyInThePath() {
        var manager = new DefaultUrlManager(new DefaultUrlManagerOptions() {
            PropertyPathFormat = PropertyPathUrlFormat.FriendlyShortString,
            AssetUrlFormat = AssetUrlFormat.FriendlyShortString,
        });
        var dm = new Datamodel();
        dm.Add<UrlPage>();
        dm.Add<UrlPageTree>();
        var data = new DataStoreLocal(dm, new SettingsLocal(), new IOProviderMemory(), urlManager: manager);
        data.Open(true, true);
        using var db = new NodeStore(data);
        var pageId = insert(db, "Docs", "docs");
        Assert.IsTrue(db.Datastore.TryGetNodeMeta(pageId, out var meta));
        var bodyPropId = db.Datastore.Datamodel.NodeTypesByFullName[typeof(UrlPage).FullName!]
            .AllProperties.Values.First(p => p.CodeName == nameof(UrlPage.Body)).Id;
        var propertyPath = new NodePath(new NodeKey(meta.InternalId)).CreatePropertyPath(bodyPropId);

        var url = manager.GetAssetUrl(new AssetUrl {
            Token = "aTOKEN",
            BaseToken = "pTOKEN",
            Target = UrlTarget.PropertyAdjusted,
            Owner = propertyPath.NodePath.NodeKey,
            FileName = "pic.jpg",
            PropertyPath = propertyPath,
            Adjustment = new FileAdjustmentImage() { Width = 100, Height = 200 },
        }, absolute: false);
        Assert.AreEqual($"/assets/Body-{meta.InternalId}/w100h200/pic.jpg", url);

        Assert.IsTrue(db.TryParseUrl(url, out var keys));
        Assert.AreEqual(UrlTarget.PropertyAdjusted, keys.Target);
        Assert.AreEqual(bodyPropId, keys.PropertyPath!.PropertyId);
        Assert.AreEqual(100, ((FileAdjustmentImage)keys.Adjustment!).Width);
        // guid ids still resolve on the way in:
        Assert.IsTrue(db.TryParseUrl($"/assets/Body-{pageId}/pic.jpg", out var byGuid));
        Assert.AreEqual(UrlTarget.Property, byGuid.Target);
        Assert.AreEqual(pageId, byGuid.NodeKey.Guid);
        // but outbound always renders the short internal int id, also for guid-addressed properties:
        var guidPath = new NodePath(pageId).CreatePropertyPath(bodyPropId);
        var guidUrl = manager.GetAssetUrl(new AssetUrl {
            Token = "pTOKEN",
            Target = UrlTarget.Property,
            Owner = guidPath.NodePath.NodeKey,
            PropertyPath = guidPath,
        }, absolute: false);
        Assert.AreEqual($"/assets/Body-{meta.InternalId}", guidUrl);
    }

    [TestMethod]
    public void SignedReadableAdjustment_RejectsTamperingButAllowsCosmeticEdits() {
        var manager = new DefaultUrlManager(new DefaultUrlManagerOptions() {
            AssetUrlFormat = AssetUrlFormat.QueryParameters,
            AssetUrlSignatureKey = Guid.NewGuid(),
        });
        var adjustment = new FileAdjustmentImage() { Width = 100, Height = 200 };
        var url = manager.GetAssetUrl(new AssetUrl {
            Token = "aFULLTOKEN",
            BaseToken = "pBASETOKEN",
            Target = UrlTarget.PropertyAdjusted,
            Owner = new NodeKey(1),
            FileName = "pic.jpg",
            Adjustment = adjustment,
        }, absolute: false);
        StringAssert.Contains(url, "sig=");

        var match = manager.TryGetAssetToken(url);
        Assert.IsNotNull(match);
        Assert.AreEqual("pBASETOKEN", match.Token);
        Assert.AreEqual(100, ((FileAdjustmentImage)match.Adjustment!).Width);

        // editing the adjustment, or dropping the signature, stops the URL from resolving:
        Assert.IsNull(manager.TryGetAssetToken(url.Replace("w=100", "w=5000")));
        Assert.IsNull(manager.TryGetAssetToken(url.Replace("h=200", "h=201")));
        Assert.IsNull(manager.TryGetAssetToken(url[..url.IndexOf("&sig=")]));

        // the signature covers the canonical request only, so cosmetic edits still resolve:
        Assert.IsNotNull(manager.TryGetAssetToken(url.Replace("pic.jpg", "other-name.jpg")));
        Assert.IsNotNull(manager.TryGetAssetToken("https://www.example.com" + url)); // relative or absolute
        Assert.IsNotNull(manager.TryGetAssetToken(url + "&utm_source=mail"));
    }

    [TestMethod]
    public void SignedReadableTarget_RejectsGuessedUrls() {
        var manager = new DefaultUrlManager(new DefaultUrlManagerOptions() {
            PropertyPathFormat = PropertyPathUrlFormat.QueryParameters,
            AssetUrlFormat = AssetUrlFormat.QueryParameters,
            AssetUrlSignatureKey = Guid.NewGuid(),
        });
        var dm = new Datamodel();
        dm.Add<UrlPage>();
        dm.Add<UrlPageTree>();
        var data = new DataStoreLocal(dm, new SettingsLocal(), new IOProviderMemory(), urlManager: manager);
        data.Open(true, true);
        using var db = new NodeStore(data);
        var firstId = insert(db, "First", "first");
        var secondId = insert(db, "Second", "second");
        Assert.IsTrue(db.Datastore.TryGetNodeMeta(firstId, out var first));
        Assert.IsTrue(db.Datastore.TryGetNodeMeta(secondId, out var second));
        var bodyPropId = db.Datastore.Datamodel.NodeTypesByFullName[typeof(UrlPage).FullName!]
            .AllProperties.Values.First(p => p.CodeName == nameof(UrlPage.Body)).Id;
        var propertyPath = new NodePath(new NodeKey(first.InternalId)).CreatePropertyPath(bodyPropId);

        var url = manager.GetAssetUrl(new AssetUrl {
            Token = "pTOKEN",
            Target = UrlTarget.Property,
            Owner = propertyPath.NodePath.NodeKey,
            FileName = "pic.jpg",
            PropertyPath = propertyPath,
            ContentVersionId = "abc123",
        }, absolute: false);
        StringAssert.Contains(url, "sig=");
        Assert.IsTrue(db.TryParseUrl(url, out var keys));
        Assert.AreEqual(bodyPropId, keys.PropertyPath!.PropertyId);
        Assert.AreEqual(first.InternalId, keys.NodeKey.Int);

        // pointing the same signature at another node, or dropping it, no longer resolves:
        Assert.IsFalse(db.TryParseUrl(url.Replace("pid=" + first.InternalId, "pid=" + second.InternalId), out _));
        Assert.IsFalse(db.TryParseUrl(url[..url.IndexOf("&sig=")], out _));
        Assert.IsFalse(db.TryParseUrl($"/assets/pic.jpg?pn=Body&pid={second.InternalId}", out _)); // a guessed URL
        // the version is a cache buster, not part of the request:
        Assert.IsTrue(db.TryParseUrl(url.Replace("v=abc123", "v=zzz999"), out _));
    }

    [TestMethod]
    public void SignedReadableTarget_InThePath_RejectsTampering() {
        var manager = new DefaultUrlManager(new DefaultUrlManagerOptions() {
            PropertyPathFormat = PropertyPathUrlFormat.FriendlyShortString,
            AssetUrlFormat = AssetUrlFormat.FriendlyShortString,
            AssetUrlSignatureKey = Guid.NewGuid(),
        });
        var dm = new Datamodel();
        dm.Add<UrlPage>();
        dm.Add<UrlPageTree>();
        var data = new DataStoreLocal(dm, new SettingsLocal(), new IOProviderMemory(), urlManager: manager);
        data.Open(true, true);
        using var db = new NodeStore(data);
        var pageId = insert(db, "Docs", "docs");
        Assert.IsTrue(db.Datastore.TryGetNodeMeta(pageId, out var meta));
        var bodyPropId = db.Datastore.Datamodel.NodeTypesByFullName[typeof(UrlPage).FullName!]
            .AllProperties.Values.First(p => p.CodeName == nameof(UrlPage.Body)).Id;
        var propertyPath = new NodePath(new NodeKey(meta.InternalId)).CreatePropertyPath(bodyPropId);

        var url = manager.GetAssetUrl(new AssetUrl {
            Token = "aTOKEN",
            BaseToken = "pTOKEN",
            Target = UrlTarget.PropertyAdjusted,
            Owner = propertyPath.NodePath.NodeKey,
            FileName = "pic.jpg",
            PropertyPath = propertyPath,
            Adjustment = new FileAdjustmentImage() { Width = 100, Height = 200 },
        }, absolute: false);
        Assert.AreEqual($"/assets/Body-{meta.InternalId}/w100h200/pic.jpg?sig=" + UrlUtil.GetQueryParameter(url, "sig"), url);
        Assert.IsTrue(db.TryParseUrl(url, out var keys));
        Assert.AreEqual(100, ((FileAdjustmentImage)keys.Adjustment!).Width);
        // both readable parts sit in the path, and both are covered:
        Assert.IsFalse(db.TryParseUrl(url.Replace("w100h200", "w5000h5000"), out _));
        Assert.IsFalse(db.TryParseUrl(url.Replace($"Body-{meta.InternalId}", "Slug-" + meta.InternalId), out _));
        Assert.IsFalse(db.TryParseUrl($"/assets/Body-{meta.InternalId}/w100h200/pic.jpg", out _)); // unsigned
    }

    [TestMethod]
    public void AssetUrlFormat_ReadableAdjustments_ResolveThroughTheStore() {
        using var db = openWithOptions(new DefaultUrlManagerOptions() { AssetUrlFormat = AssetUrlFormat.QueryParameters });
        // a real file token, crafted directly - the adjustment rides readably next to it:
        var propertyPath = new NodePath(Guid.NewGuid()).CreatePropertyPath(Guid.NewGuid());
        var token = new InternalUrlProvider().GetUrl(propertyPath, null, false);
        Assert.IsTrue(db.TryParseUrl("/assets/" + token + "?w=100&h=200&f=jpeg", out var keys));
        Assert.AreEqual(UrlTarget.PropertyAdjusted, keys.Target);
        Assert.IsNotNull(keys.Adjustment);
        Assert.AreEqual(100, ((FileAdjustmentImage)keys.Adjustment).Width);
        Assert.AreEqual(FileFormat.Jpeg, keys.Adjustment.RequestedFormat);
        // without adjustment parameters the same token is the plain file:
        Assert.IsTrue(db.TryParseUrl("/assets/" + token, out var plain));
        Assert.AreEqual(UrlTarget.Property, plain.Target);
    }

    // ------------------------------------------------------------------ typed managers

    // A manager written against the typed object model, like the Website.Simple sample: it
    // materializes node objects whose HTML properties externalize during mapping, which calls back
    // into the manager. The nested-externalization guard is what keeps that from recursing forever
    // when pages link to each other.
    class TypedTestManager(Func<NodeStore> db) : UrlManagerBase {
        public override void Initialize(IDataStore store) { }
        public override string? TryGetUrl(NodeMeta meta, bool absolute) {
            var page = db().Get<UrlPage>(meta.Id);
            var segments = new List<string>();
            while (true) {
                if (page.Slug.Length == 0 || segments.Count > 32) return null;
                segments.Insert(0, page.Slug);
                if (!page.Parent.TryGet(out page!)) break;
            }
            return "/" + string.Join('/', segments);
        }
        public override NodeKeyWithCulture[] GetMatches(string completeUrl) {
            var last = UrlUtil.GetLastSegment(completeUrl);
            if (last == null) return [];
            var path = UrlUtil.GetPath(completeUrl);
            return db().GetNodeIdsFromAddress(last)
                .Where(c => db().Datastore.TryGetNodeMeta(c.IdKey, out var m) && TryGetUrl(m, false) == path)
                .ToArray();
        }
        public override bool WillAddressResultInUniqueUrl(NodeKey node, Guid cultureId, string address) => true;
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
