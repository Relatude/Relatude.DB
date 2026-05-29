
using Microsoft.Net.Http.Headers;
using Relatude.DB.Common;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Json;

// NO NAMESPACE ON PURPOSE
public static class AddUse {
    public static WebApplicationBuilder AddRelatudeDB(this WebApplicationBuilder builder, Action<ServerOptions>? configureOptions = null) {
        var options = new ServerOptions();
        configureOptions?.Invoke(options);
        builder.Services.ConfigureHttpJsonOptions(o => RelatudeDBJsonOptions.ConfigureDefault(o.SerializerOptions));
        builder.Services.AddSingleton<RelatudeDBContext>();
        builder.Services.AddSingleton(options);
        return builder;
    }
    public static IEndpointRouteBuilder UseRelatudeDB(this WebApplication app, string? urlPath = null) {
        return UseRelatudeDBAsync(app, urlPath).Result;
    }
    public static async Task<IEndpointRouteBuilder> UseRelatudeDBAsync(this WebApplication app, string? urlPath = null) {
        await app.StartRelatudeDBAsync(urlPath);
        app.MapRelatudeDBAdmin();
        app.MapRelatudeDBAdmin();
        return app;
    }
    public static IEndpointRouteBuilder StartRelatudeDB(this WebApplication app, string? urlPath = null) {
        return StartRelatudeDBAsync(app, urlPath).Result;
    }
    public static async Task<IEndpointRouteBuilder> StartRelatudeDBAsync(this WebApplication app, string? urlPath = null) {
        var server = new RelatudeDBServer(urlPath);
        RelatudeDBRuntime.Initialize(server);
        var options = app.Services.GetRequiredService<ServerOptions>();
        await server.StartAsync(app, options);
        app.Use(server.Authentication.StartupProgressBarMiddleware); // middleware to show opening progress page
        app.Use(server.Authentication.AuthorizationMiddleware); // authentication middleware for server admin UI and API
        return app;
    }
    public static IEndpointRouteBuilder MapRelatudeDBAdmin(this WebApplication app, string? urlPath = null) {
        var server = RelatudeDBRuntime.Server;
        server.MapSimpleAPI(app);
        return app;
    }
    public static IEndpointRouteBuilder MapRelatudeDBClient(this WebApplication app) {
        var options = app.Services.GetRequiredService<ServerOptions>();
        var urlPath = options.FileHandlerRootUrl == null ? ServerOptions.DefaultFileRootUrl : options.FileHandlerRootUrl;
        if (string.IsNullOrWhiteSpace(urlPath)) throw new ArgumentException("URL path cannot be null or whitespace.", nameof(urlPath));
        if (!urlPath.StartsWith("/")) urlPath = "/" + urlPath;
        if (urlPath.EndsWith("/")) urlPath = urlPath.TrimEnd('/');
        RelatudeDBServer.FileHandlerRootUrl = urlPath;
        app.MapGet(urlPath + "/{propPathAndAdj}", async (RelatudeDBContext ctx, HttpContext http, string propPathAndAdj) => {
            var db = ctx.Database;
            var fileInfo = await db.GetFileStreamAndState(propPathAndAdj, 100);
            if (fileInfo.IsTemporary) {
                http.Response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue { NoCache = true };
            } else {
                http.Response.GetTypedHeaders().CacheControl = new CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromDays(30) };
            }
            var contentType = FileFormatUtil.GetContentType(fileInfo.RequestedFormat);
            var stream = fileInfo.Stream;
            var totalLength = stream.CanSeek ? stream.Length : (long?)null;
            var rangeHeader = http.Request.Headers.Range.ToString();
            if (stream.CanSeek && !string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=")) {
                var range = rangeHeader["bytes=".Length..].Split('-');
                var start = long.TryParse(range[0], out var s) ? s : 0;
                var end = range.Length > 1 && long.TryParse(range[1], out var e) ? e : totalLength!.Value - 1;
                end = Math.Min(end, totalLength!.Value - 1);
                var length = end - start + 1;
                stream.Seek(start, SeekOrigin.Begin);
                http.Response.StatusCode = 206;
                http.Response.Headers.ContentRange = $"bytes {start}-{end}/{totalLength}";
                http.Response.Headers.AcceptRanges = "bytes";
                http.Response.ContentLength = length;
                http.Response.ContentType = contentType;
                await stream.CopyToAsync(http.Response.Body, (int)Math.Min(length, 81920));
                return Results.Empty;
            }
            if (totalLength.HasValue)
                http.Response.Headers.AcceptRanges = "bytes";
            return Results.Stream(stream, contentType);
        });
        return app;
    }

}
