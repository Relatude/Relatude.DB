using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Relatude.DB.GraphQL.Endpoints;

/// <summary>
/// The explorer page of an endpoint, served on the endpoint's own url: the html with the endpoint's settings written
/// into it, and its script and stylesheet as "?explorer-asset=" requests to the same url. The files are the
/// standalone build of the admin UI's explorer (src/Relatude.DB.UI, vite.explorer.config.ts), embedded in this
/// assembly, so a code-first endpoint has the page as well as the ones defined in the admin UI.
/// </summary>
public static partial class ExplorerPage {
    const string ResourcePrefix = "Relatude.DB.GraphQL.Explorer.";
    const string PageName = "explorer.html";

    sealed record Asset(byte[] Raw, byte[] Gzip, string ContentType, string ETag);

    static readonly ConcurrentDictionary<string, Asset?> _assets = new(StringComparer.Ordinal);

    /// <summary>False when this build carries no explorer files (the UI was not built before the library).</summary>
    public static bool Available => asset(PageName) != null;

    static Asset? asset(string name) => _assets.GetOrAdd(name, n => {
        if (n.Contains('/') || n.Contains('\\') || n.Contains("..")) return null;
        using var stream = typeof(ExplorerPage).Assembly.GetManifestResourceStream(ResourcePrefix + n);
        if (stream == null) return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var raw = memory.ToArray();
        using var zipped = new MemoryStream();
        using (var gzip = new GZipStream(zipped, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(raw);
        var hash = Convert.ToHexString(SHA256.HashData(raw))[..16].ToLowerInvariant();
        return new Asset(raw, zipped.ToArray(), contentTypeOf(n), "\"" + hash + "\"");
    });

    static string contentTypeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch {
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".html" => "text/html; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        _ => "application/octet-stream",
    };

    /// <summary>Whether a request is a browser asking for the url as a page, rather than a client asking a question.</summary>
    public static bool WantsPage(HttpRequest request) {
        if (!HttpMethods.IsGet(request.Method)) return false;
        var q = request.Query;
        if (q.ContainsKey("explorer")) return true;
        if (q.ContainsKey("query") || q.ContainsKey("sdl") || q.ContainsKey("explorer-data") || q.ContainsKey("explorer-asset") || q.ContainsKey("facets")) return false;
        return request.Headers.Accept.Any(a => a != null && a.Contains("text/html", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Writes a script or stylesheet of the page. They carry a version in their url, so they are cached for good.</summary>
    public static async Task WriteAssetAsync(HttpContext http, string name) {
        var file = name == PageName ? null : asset(name);
        if (file == null) {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        http.Response.Headers.ETag = file.ETag;
        http.Response.Headers.CacheControl = http.Request.Query.ContainsKey("v") ? "public, max-age=31536000, immutable" : "no-cache";
        if (http.Request.Headers.IfNoneMatch.Contains(file.ETag)) {
            http.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }
        await writeAsync(http, file.Raw, file.Gzip, file.ContentType);
    }

    /// <summary>
    /// Writes the page, with the endpoint's name, url and what it asks of a client written into it. With the facet search
    /// on, the page opens on it (a visual pivot of the exposed types) and the explorer, when that is on too, is a tab of it.
    /// </summary>
    public static async Task WritePageAsync(HttpContext http, GraphQLEndpointDefinition definition, bool facets = false) {
        var page = asset(PageName);
        if (page == null) {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            http.Response.ContentType = "text/plain; charset=utf-8";
            await http.Response.WriteAsync("The explorer is not part of this build of Relatude.DB.GraphQL.");
            return;
        }
        var path = (http.Request.PathBase + http.Request.Path).Value?.TrimEnd('/') ?? "";
        var name = string.IsNullOrWhiteSpace(definition.Name) ? "GraphQL" : definition.Name.Trim();
        var config = JsonSerializer.Serialize(new {
            name,
            description = definition.Description,
            url = path,
            apiKey = definition.RequiresApiKey,
            introspection = definition.EnableIntrospection,
            mutations = definition.AllowMutations,
            explorer = definition.EnableExplorer,
            facets,
        });
        var html = Encoding.UTF8.GetString(page.Raw);
        // the build refers to its files next to the page; here they are questions to the same url
        html = assetReference().Replace(html, m => {
            var file = m.Groups[2].Value;
            var version = asset(file)?.ETag.Trim('"') ?? "0";
            return $"{m.Groups[1].Value}=\"?explorer-asset={Uri.EscapeDataString(file)}&v={version}\"";
        });
        html = html.Replace("<title>GraphQL explorer</title>", "<title>" + System.Net.WebUtility.HtmlEncode(name) + (facets ? " · Facet search" : " · GraphQL explorer") + "</title>");
        // the settings travel as json inside the page; "<" is escaped so no value can close the script element
        html = html.Replace("</head>", "<script id=\"relatude-explorer-config\" type=\"application/json\">" + config.Replace("<", "\\u003c") + "</script></head>");
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        var raw = Encoding.UTF8.GetBytes(html);
        await writeAsync(http, raw, null, "text/html; charset=utf-8");
    }

    static async Task writeAsync(HttpContext http, byte[] raw, byte[]? gzip, string contentType) {
        http.Response.ContentType = contentType;
        var acceptsGzip = http.Request.Headers.AcceptEncoding.Any(e => e != null && e.Contains("gzip", StringComparison.OrdinalIgnoreCase));
        var body = gzip != null && acceptsGzip ? gzip : raw;
        if (body == gzip) http.Response.Headers.ContentEncoding = "gzip";
        http.Response.Headers.Vary = "Accept-Encoding";
        http.Response.ContentLength = body.Length;
        await http.Response.Body.WriteAsync(body);
    }

    [GeneratedRegex("(src|href)=\"\\./([A-Za-z0-9_.-]+)\"")]
    private static partial Regex assetReference();
}
