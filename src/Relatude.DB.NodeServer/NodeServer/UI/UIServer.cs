using Relatude.DB.Common;
using Relatude.DB.NodeServer.API;
using Relatude.DB.NodeServer.Json;
using System.Text.Json;
namespace Relatude.DB.NodeServer.UI;
/// <summary>
/// The backend of the new admin UI. All communication runs over two routes:
///   GET  {ApiUrlRoot}/ui/stream   - one SSE connection carrying all server-to-client push (UIEventStream)
///   POST {ApiUrlRoot}/ui/command  - all client-to-server requests, dispatched on command type (UICommands)
/// Both live under ApiUrlRoot, so the standard admin authentication middleware covers them.
/// The UI itself (built from src/Relatude.DB.UI into the embedded ClientUI2 resources) is served
/// at {ApiUrlRoot}2, next to the old admin UI at {ApiUrlRoot}.
/// </summary>
public sealed class UIServer {
    const int containerWatchIntervalMs = 1000;
    readonly RelatudeDBServer _server;
    readonly Timer _containerWatch;
    string? _lastContainersJson;
    public UIEventStream Events { get; } = new();
    public UICommands Commands { get; }
    internal UIServer(RelatudeDBServer server) {
        _server = server;
        Commands = new UICommands(server);
        registerBuiltInCommands();
        _containerWatch = new Timer(_ => watchContainers(), null, containerWatchIntervalMs, Timeout.Infinite);
    }
    // broadcasts a "containers" event whenever the container list changes (state, node count, name),
    // so every connected UI stays live without polling. Does no work while nobody is connected.
    void watchContainers() {
        try {
            if (Events.ConnectionCount > 0) {
                var containers = buildContainers();
                var json = JsonSerializer.Serialize(containers, RelatudeDBJsonOptions.SSE);
                if (json != _lastContainersJson) {
                    _lastContainersJson = json;
                    Events.Broadcast("containers", containers);
                }
            }
        } catch (Exception error) {
            RelatudeDBServer.Trace("UI container watch error: " + error.Message);
        } finally {
            _containerWatch!.Change(containerWatchIntervalMs, Timeout.Infinite);
        }
    }
    object[] buildContainers() {
        return [.. _server.GetContainers().Select(c => {
            long? nodeCount = null;
            if (c.IsOpen()) {
                try { nodeCount = c.Store!.Count(); } catch { } // a container closing mid-request should not fail the snapshot
            }
            return (object)new {
                c.Settings.Id,
                Name = string.IsNullOrEmpty(c.Settings.Name) ? c.Settings.Id.ToString() : c.Settings.Name,
                State = c.HasFailed ? "Error" : c.Store?.State.ToString() ?? "Closed",
                NodeCount = nodeCount,
            };
        })];
    }
    internal void Map(WebApplication app) {
        var path = _server.ApiUrlRoot + "/ui/";
        app.MapGet(path + "stream", Events.Connect);
        app.MapPost(path + "command", (Delegate)Commands.Execute); // Delegate overload, so the returned IResult is written to the response
        mapStaticUI(app);
    }
    void mapStaticUI(WebApplication app) {
        string html, js, css;
        try {
            html = ServerAPIMapper.GetResource("ClientUI2.index.html");
            js = ServerAPIMapper.GetResource("ClientUI2.index.js");
            css = ServerAPIMapper.GetResource("ClientUI2.index.css");
        } catch (Exception error) {
            RelatudeDBServer.Trace("New admin UI not mapped, resources missing (build src/Relatude.DB.UI first): " + error.Message);
            return;
        }
        // urls are segment based in the authentication middleware, so {ApiUrlRoot}2 is public
        // while the api the UI calls ({ApiUrlRoot}/ui/...) still requires authentication
        var root = _server.ApiUrlRoot + "2";
        // a unique url per version: unchanged UI stays cached by the browser, new versions bypass the cache
        var hash = js.XXH64Hash() ^ css.XXH64Hash();
        html = html
            .Replace("./index.js", root + "/" + hash + ".js")
            .Replace("./index.css", root + "/" + hash + ".css");
        app.MapGet(root, (HttpContext ctx) => {
            ctx.Response.ContentType = "text/html";
            return html;
        });
        app.MapGet(root + "/" + hash + ".js", (HttpContext ctx) => {
            ctx.Response.ContentType = "text/javascript";
            ctx.Response.Headers.Append("Cache-Control", "public, max-age=315360000");
            return js;
        });
        app.MapGet(root + "/" + hash + ".css", (HttpContext ctx) => {
            ctx.Response.ContentType = "text/css";
            ctx.Response.Headers.Append("Cache-Control", "public, max-age=315360000");
            return css;
        });
    }
    void registerBuiltInCommands() {
        Commands.Register("ping", ctx => new { Pong = true, ServerTimeUtc = DateTime.UtcNow });
        Commands.Register("server-info", ctx => new {
            Version = typeof(UIServer).Assembly.GetName().Version?.ToString(),
            UpTimeMs = _server.UpTime.TotalMilliseconds,
            Containers = buildContainers(),
        });
    }
}
