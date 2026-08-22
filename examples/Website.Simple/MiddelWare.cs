using Relatude.DB.NodeServer;
using Relatude.DB.Web;
using System.Text;
using Website.Simple.Models;

namespace Website.Simple;

public class RelatudeDBMiddleware {
    private readonly RequestDelegate _next;

    public RelatudeDBMiddleware(RequestDelegate next) {
        _next = next;
    }
    public async Task Invoke(HttpContext http, RelatudeDBContext ctx) {
        // the demo start page owns "/", so the Site One root page is not served there
        if (RelatudeDBRuntime.IsReady && http.Request.Path != "/") {
            // the complete URL, host included, so the url manager can route per domain
            var url = http.Request.Scheme + "://" + http.Request.Host + http.Request.Path.Value + http.Request.QueryString;
            if (ctx.Database.TryParseUrlForContent(url, out var content)) {
                var result = await handleRequest(http, ctx, content);
                if (result != null) {
                    await result.ExecuteAsync(http);
                    return;
                }
            }
        }
        await _next.Invoke(http);
    }
    async Task<IResult?> handleRequest(HttpContext http, RelatudeDBContext ctx, UrlContent content) {
        return content.Id.Target switch {
            UrlTarget.Property or UrlTarget.PropertyAdjusted => await handleFile(http, content),
            UrlTarget.Node or UrlTarget.EmbeddedNode => handlePage(http, ctx, content),
            _ => null,
        };
    }
    async Task<IResult?> handleFile(HttpContext http, UrlContent c) {
        return await FileHandler.HandleFileAsync(http, c.Stream, c.FileName, c.Attachment, c.ContentType, c.Cacheable);
    }
    IResult? handlePage(HttpContext http, RelatudeDBContext ctx, UrlContent c) {
        // SitePage nodes (the dynamic URL example) render as HTML; any other node type is returned as JSON
        var db = ctx.Database;
        var pageType = db.Datastore.Datamodel.NodeTypesByFullName[typeof(SitePage).FullName!];
        if (c.NodeData != null && c.NodeData.NodeType == pageType.Id) {
            var page = db.Get<SitePage>(c.NodeData.Id); // the Body arrives with current public URLs, not the stored rdb: tokens
            var html = new StringBuilder();
            html.Append("<html><body>");
            html.Append("<h1>").Append(page.Title).Append("</h1>");
            html.Append(page.Body);
            var childIds = db.Datastore.GetRelatedNodeIdsFromRelationId(db.Mapper.GetRelationId<PageTree>(), page.Id, false);
            var children = childIds.Select(db.Get<SitePage>).ToList();
            if (children.Count > 0) {
                html.Append("<ul>");
                foreach (var child in children) html.Append($"<li><a href='{db.GetUrl(child)}'>{child.Title}</a></li>");
                html.Append("</ul>");
            }
            html.Append("<p><a href='/'>Demo start page</a></p>");
            html.Append("</body></html>");
            return Results.Content(html.ToString(), "text/html; charset=utf-8");
        }
        return Results.Json(c);
    }
}
