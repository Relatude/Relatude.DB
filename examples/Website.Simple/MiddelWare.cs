using Relatude.DB.NodeServer;
using Relatude.DB.Web;

namespace Website.Simple;

public class RelatudeDBMiddleware {
    private readonly RequestDelegate _next;
    public RelatudeDBMiddleware(RequestDelegate next) {
        _next = next;
    }
    public async Task Invoke(HttpContext http, RelatudeDBContext ctx) {
        //if (RelatudeDBRuntime.IsReady) {
        //    var url = http.Request.Path.Value + http.Request.QueryString;
        //    if (ctx.Database.TryParseUrlForContent(url, out var content)) {
        //        var result = await handleRequest(http, content);
        //        if (result != null) {
        //            await result.ExecuteAsync(http);
        //            return;
        //        }
        //    }
        //}
        await _next.Invoke(http);
    }
    async Task<IResult?> handleRequest(HttpContext http, UrlContent content) {
        return content.Id.Target switch {
            UrlTarget.Property or UrlTarget.PropertyAdjusted => await handleFile(http, content),
            UrlTarget.Node or UrlTarget.EmbeddedNode => await handlePage(http, content),
            _ => null,
        };
    }
    async Task<IResult?> handleFile(HttpContext http, UrlContent c) {
        return await FileHandler.HandleFileAsync(http, c.Stream, c.FileName, c.Attachment, c.ContentType, c.Cacheable);
    }
    async Task<IResult?> handlePage(HttpContext http, UrlContent c) {
        return Results.Json(c);
    }
}