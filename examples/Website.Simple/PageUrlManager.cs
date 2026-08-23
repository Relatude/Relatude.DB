using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.NodeServer;
using Relatude.DB.Nodes;
using Relatude.DB.Web;
using Website.Simple.Data;
using Website.Simple.Models;

namespace Website.Simple;

public class PageUrlManager : UrlManagerBase {

    readonly Dictionary<string, Guid> _rootByHost = new(StringComparer.OrdinalIgnoreCase) {
        ["www.site-one.local"] = PageSeeder.SiteOneRootId,
        ["www.site-two.local"] = PageSeeder.SiteTwoRootId,
    };
    readonly Guid _fallbackRoot = PageSeeder.SiteOneRootId; // localhost and other unknown hosts serve site one

    static NodeStore db => RelatudeDBRuntime.Database;
    public override void Initialize(IDataStore store) { } // the store is reached through the runtime instead

    // outbound: walk up the tree collecting slugs until a root is reached
    public override string? TryGetUrl(NodeMeta meta, bool absolute) {
        var page = db.Get<SitePage>(meta.Id);
        var segments = new List<string>();
        while (!isRoot(page.Id)) {
            if (page.Slug.Length == 0) return null; // a page without a slug has no URL
            if (segments.Count > 32) return null; // cycle guard
            segments.Insert(0, page.Slug);
            if (!page.Parent.TryGet(out page!)) return null; // above the tree without reaching a root
        }
        var path = "/" + string.Join('/', segments);
        if (!absolute) return path;
        return "https://" + _rootByHost.First(d => d.Value == page.Id).Key + path;
    }

    // inbound: descend from the host's root, one Traverse over the Children relation per segment
    public override IdKeyWithCultureId[] GetMatches(string completeUrl) {
        var host = UrlUtil.GetHost(completeUrl);
        var current = host != null && _rootByHost.TryGetValue(host, out var root) ? root : _fallbackRoot;
        foreach (var segment in UrlUtil.GetSegments(completeUrl)) {
            var child = db.Query<SitePage>().Where(current)
                .Traverse<SitePage>(p => p.Children, maxLevel: 1)
                .Execute().FirstOrDefault(p => p.Slug == segment);
            if (child == null) return [];
            current = child.Id;
        }
        return [new IdKeyWithCultureId(new NodeKey(current), Guid.Empty)]; // single-culture site
    }

    // an address gives a unique URL unless a sibling already uses it
    public override bool WillAddressResultInUniqueUrl(NodeKey node, Guid cultureId, string address) {
        if (!db.Datastore.TryGetNodeMeta(node, out var meta)) return true; // not created yet; checked again on its next update
        if (isRoot(meta.Id)) return true; // roots are served at "/", their slug is never part of a URL
        var page = db.Get<SitePage>(meta.Id);
        if (!page.Parent.TryGet(out var parent)) return true;
        return !parent.Children.Any(sibling => sibling.Slug == address && sibling.Id != page.Id);
    }

    bool isRoot(Guid id) => id == _fallbackRoot || _rootByHost.ContainsValue(id);
}
