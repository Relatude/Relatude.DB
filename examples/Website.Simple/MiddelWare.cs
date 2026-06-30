using Microsoft.AspNetCore.Http.Extensions;
using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.FileConversion;
using Relatude.DB.NodeServer;
using Relatude.DB.Web;
using System.Web;

namespace Website.Simple;

public class RelatudeDBMiddleware {
    private readonly RequestDelegate _next;
    IHostApplicationLifetime _appLifetime;
    public RelatudeDBMiddleware(RequestDelegate next, IHostApplicationLifetime appLifetime) {
        _next = next;
        _appLifetime = appLifetime;
    }
    public async Task Invoke(HttpContext context, RelatudeDBContext ctx) {
        if (RelatudeDBRuntime.IsReady) {
            var url = context.Request.Path.Value + context.Request.QueryString;
            Console.WriteLine(url);
            if (ctx.Database.Datastore.TryParseUrl(url, out var urlType, out var nodeKey, out var nodePath, out var propertyPath, out var adjustment)) {
                var result = await handleRequest(context, ctx.Database.Datastore, ctx, urlType, nodeKey, nodePath, propertyPath, adjustment);
                if (result != null) {
                    await result.ExecuteAsync(context);
                    return;
                }
            }
        }
        await _next.Invoke(context);
    }
    async Task<IResult?> handleRequest(HttpContext context, IDataStore store, RelatudeDBContext ctx, UrlType urlType, NodeKey nodeKey, NodePath? nodePath, PropertyPath? propertyPath, FileAdjustment? adjustment) {
        return urlType switch {
            UrlType.LocalProperty => await FileHandler.HandleFileAsync(ctx, context, propertyPath!),
            UrlType.LocalAdjusted => await FileHandler.HandleFileAsync(ctx, context, propertyPath!, adjustment!),
            UrlType.LocalNode => Results.Json(store.Get(nodeKey)),
            _ => null,
        };
    }
}