using Relatude.DB.NodeServer;
using Relatude.DB.Web;
using System.Text;

namespace Backend.Middleware;

public class RelatudeDBMiddleware {
    private readonly RequestDelegate _next;

    public RelatudeDBMiddleware(RequestDelegate next) {
        _next = next;
    }
    public async Task Invoke(HttpContext http, RelatudeDBContext ctx) {
        if (RelatudeDBRuntime.IsReady && http.Request.Path != "/") {
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
        return Results.Json(c);
    }
}
