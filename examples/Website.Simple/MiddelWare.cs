using Microsoft.AspNetCore.Http.Extensions;
using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.NodeServer;
using Relatude.DB.Web;

namespace Website.Simple;

public class RelatudeDBMiddleware {
    private readonly RequestDelegate _next;
    IHostApplicationLifetime _appLifetime;
    public RelatudeDBMiddleware(RequestDelegate next, IHostApplicationLifetime appLifetime) {
        _next = next;
        _appLifetime = appLifetime;
    }
    public async Task Invoke(HttpContext context, RelatudeDBContext ctx) {
        var url = context.Request.GetEncodedPathAndQuery();
        var db = ctx?.Database;
        if (db != null && db.State == DataStoreState.Open) {
            if (db.Datastore.IsUrlRelevant(url)) {
                if (db.Datastore.TryParseUrlType(url, out var urlType)) {
                    await handleRequest(context, url, urlType, db.Datastore, ctx);
                    return;
                }
            }
        }
        await _next.Invoke(context);
    }
    async Task handleRequest(HttpContext context, string url, UrlType urlType, IDataStore store, RelatudeDBContext ctx) {
        switch (urlType) {
            case UrlType.LocalProperty: {
                    if (store.TryParseUrlPropertyPath(url, out var propertyPath)) {
                        await (await FileHandler.HandleFileAsync(ctx, context, propertyPath)).ExecuteAsync(context);
                        return;
                    }
                }
                break;
            case UrlType.LocalAdjusted: {
                    if (store.TryParseUrlAdjustments(url, out var propertyPath, out var adjusted)) {
                        await (await FileHandler.HandleFileAsync(ctx, context, propertyPath, adjusted)).ExecuteAsync(context);
                        return;
                    }
                }
                break;
            case UrlType.LocalUrl:
            case UrlType.LocalNode:
            case UrlType.LocalEmbeddedNode:
            case UrlType.RemoteUrl:
            case UrlType.Email:
            default: break;
        }
        await _next.Invoke(context);
    }
}