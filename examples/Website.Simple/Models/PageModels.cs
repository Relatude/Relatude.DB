using Relatude.DB.Nodes;

namespace Website.Simple.Models;

// Datamodel for the dynamic URL example (see the /pages* endpoints and RelatudeDBMiddleware).
//
// The Slug is only a LOCAL url segment: the complete URL is computed on demand by the
// TreeUrlManager (configured in Program.cs) from the chain of parents, so several pages can
// share the slug "info", and renaming a section is a single write - no URLs are stored anywhere.
//
// The Body is an HTML property: links to other pages and files are stored as internal id-based
// "rdb:" tokens that survive renames, and are rewritten to current public URLs on every read.

[Node]
public class SitePage {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(DisplayName = true)]
    public string Title { get; set; } = "";
    [AddressProperty]
    public string Slug { get; set; } = "";
    [HtmlProperty]
    public string Body { get; set; } = "";
    public PageTree.Parent Parent { get; set; } = new();
    public PageTree.Children Children { get; set; } = new();
}

public class PageTree : OneToMany<SitePage, SitePage> {
    public class Parent : One { }
    public class Children : Many { }
}
