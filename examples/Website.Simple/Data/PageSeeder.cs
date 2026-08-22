using Relatude.DB.Nodes;
using Website.Simple.Models;

namespace Website.Simple.Data;

// Seeds two page trees for the dynamic URL example:
//
//   Site one (root, "/")                    Site two (root, "/")
//   ├─ tv                                   └─ contact-us
//   │  └─ sony-x90
//   │     └─ info          <- same slug...
//   ├─ mobile
//   │  └─ pixel
//   │     └─ info          <- ...same slug: unique URLs come from the parent chain
//   └─ contact-us          <- same path as site two's contact page, on another domain
//
// The bodies are written with plain public URLs; the store internalizes every resolvable link to
// an id token at commit time, which is what makes them survive renames (try /pages/rename-demo).
public static class PageSeeder {

    // the url manager maps hosts to root nodes before any data exists, so the roots have fixed ids:
    public static readonly Guid SiteOneRootId = new("5175e001-0001-4a61-9a30-b1a6f3d10001");
    public static readonly Guid SiteTwoRootId = new("5175e002-0002-4a61-9a30-b1a6f3d10002");
    // fixed ids so the demo endpoints can find these pages without queries:
    public static readonly Guid TvSectionId = new("5175e003-0003-4a61-9a30-b1a6f3d10003");
    public static readonly Guid SonyInfoId = new("5175e004-0004-4a61-9a30-b1a6f3d10004");

    public static void SeedIfEmpty(NodeStore db) {
        if (db.Query<SitePage>().Count() > 0) return;

        db.Insert(new SitePage { Id = SiteOneRootId, Title = "Site One", Slug = "site-one" });
        db.Insert(new SitePage { Id = SiteTwoRootId, Title = "Site Two", Slug = "site-two" });

        var tv = insertUnder(db, SiteOneRootId, "TV", "tv", TvSectionId);
        var sony = insertUnder(db, tv, "Sony X90", "sony-x90");
        var sonyInfo = insertUnder(db, sony, "Sony X90 info", "info", SonyInfoId);
        var mobile = insertUnder(db, SiteOneRootId, "Mobile", "mobile");
        var pixel = insertUnder(db, mobile, "Pixel", "pixel");
        var pixelInfo = insertUnder(db, pixel, "Pixel info", "info"); // same slug as the Sony info page
        var contactOne = insertUnder(db, SiteOneRootId, "Contact us", "contact-us");
        var contactTwo = insertUnder(db, SiteTwoRootId, "Contact us", "contact-us"); // same path, other domain

        // bodies last, so every linked page exists and the links internalize to id tokens on commit:
        setBody(db, SiteOneRootId, "<p>Welcome to site one. Read about the "
            + "<a href=\"/tv/sony-x90/info\">Sony X90</a> or the <a href=\"/mobile/pixel/info\">Pixel</a>, "
            + "or <a href=\"/contact-us\">contact us</a>.</p>");
        setBody(db, sonyInfo, "<p>All about the Sony X90. Compare with <a href=\"/mobile/pixel/info\">the Pixel</a>.</p>");
        setBody(db, pixelInfo, "<p>All about the Pixel. Compare with <a href=\"/tv/sony-x90/info\">the Sony X90</a>.</p>");
        setBody(db, contactOne, "<p>Contact site one.</p>");
        setBody(db, SiteTwoRootId, "<p>Welcome to site two. <a href=\"/contact-us\">Contact us</a>.</p>");
        setBody(db, contactTwo, "<p>Contact site two.</p>");
    }

    static Guid insertUnder(NodeStore db, Guid parentId, string title, string slug, Guid? id = null) {
        var page = new SitePage { Id = id ?? Guid.NewGuid(), Title = title, Slug = slug };
        db.Insert(page);
        db.Execute(new Transaction(db).Relation.Relate<PageTree>(parentId, page.Id)); // parent -> child
        return page.Id;
    }
    static void setBody(NodeStore db, Guid pageId, string body) {
        var page = db.Get<SitePage>(pageId);
        page.Body = body;
        db.Update(page);
    }
}
