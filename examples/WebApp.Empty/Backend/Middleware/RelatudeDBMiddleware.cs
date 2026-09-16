using Relatude.DB.NodeServer;
using Relatude.DB.Web;

namespace WebAppEmpty.Middleware;

/// <summary>
/// Serves files stored in FileValue properties by the URLs Relatude.DB generates for them
/// (file.GetUrl(), also with a FileAdjustment for resized or cropped images). Every other request,
/// including node URLs, passes through to the API endpoints and the client.
/// </summary>
public class RelatudeDBMiddleware(RequestDelegate next) {
    public async Task Invoke(HttpContext http, RelatudeDBContext ctx) {
        if (RelatudeDBRuntime.IsReady && http.Request.Path != "/") {
            var url = http.Request.Scheme + "://" + http.Request.Host + http.Request.Path.Value + http.Request.QueryString;
            if (ctx.Database.TryParseUrlForContent(url, out var content)
                && content.Id.Target is UrlTarget.Property or UrlTarget.PropertyAdjusted) {
                var result = await FileHandler.HandleFileAsync(http, content.Stream, content.FileName, content.Attachment, content.ContentType, content.Cacheable);
                await result.ExecuteAsync(http);
                return;
            }
        }
        await next(http);
    }
}
