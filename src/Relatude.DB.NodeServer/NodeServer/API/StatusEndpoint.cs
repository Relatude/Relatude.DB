using System.Text.Json;
using Relatude.DB.Common;
namespace Relatude.DB.NodeServer.API;

/// <summary>
/// <c>/status.relatude.db</c>: whether the server's default database is open, and which Relatude.DB
/// the server runs, for uptime monitors (Relatude.License's among them) and load balancers. Every
/// server has it, and anyone may ask.
/// <para>It always answers, also while the databases are opening and while the server restarts or
/// stops: the startup middleware answers it first of all (<see cref="SimpleAuthentication.StartupProgressBarMiddleware"/>),
/// and it is mapped as an anonymous route as well, so that an application's own authorization policy
/// does not stand in front of it. It only reads states. It never opens a database, waits for one or
/// takes a lock.</para>
/// <para>The answer is 200 when the default database is open, else 503 with the same body, so a
/// monitor that reads only the status code reads it right. It is never cached. It says nothing more
/// than this: not the error that stopped a database, which can name paths, and not how much it holds.</para>
/// </summary>
public static class StatusEndpoint {
    public const string Url = "status.relatude.db";

    /// <summary>The default database is open: the only status answered with 200.</summary>
    public const string Ok = "ok";
    /// <summary>The default database is being opened.</summary>
    public const string Starting = "starting";
    /// <summary>The last attempt to open the default database failed.</summary>
    public const string Error = "error";
    /// <summary>The default database is closed, closing or disposed.</summary>
    public const string Closed = "closed";
    /// <summary>A soft restart is between closing the databases and opening them again.</summary>
    public const string Restarting = "restarting";
    /// <summary>The host is stopping.</summary>
    public const string ShuttingDown = "shutting-down";
    /// <summary>The server has no default database.</summary>
    public const string NoDatabase = "no-database";

    /// <summary>The answer. <c>Database</c> is the default database's state as the admin UI shows it: Open, Opening, Error, Closed, …; None without one.</summary>
    public sealed record Info(string Status, string Database, string Version, DateTime ServerTimeUtc);

    /// <summary>The Relatude.DB version the server runs, the same one its heartbeat reports to Relatude.License.</summary>
    public static readonly string Version = typeof(StatusEndpoint).Assembly.GetName().Version?.ToString() ?? "unknown";

    static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public static Info Read(RelatudeDBServer server) {
        var container = server.DefaultContainer;
        var database = container?.StateName ?? "None";
        var status = server.IsShuttingDown ? ShuttingDown
            : server.IsRestarting ? Restarting
            : container is null ? NoDatabase
            : database switch {
                nameof(DataStoreState.Open) => Ok,
                nameof(DataStoreState.Opening) => Starting,
                nameof(DataStoreState.Error) => Error,
                _ => Closed,
            };
        return new Info(status, database, Version, DateTime.UtcNow);
    }

    /// <summary>A GET or HEAD of the status url, whatever the case or a trailing slash.</summary>
    public static bool IsStatusRequest(HttpContext context) =>
        (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        && SimpleAuthentication.RequestIsOnUrl(context, Url);

    public static async Task WriteAsync(HttpContext context, RelatudeDBServer server) {
        var info = Read(server);
        var response = context.Response;
        response.StatusCode = info.Status == Ok ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
        response.Headers.CacheControl = "no-store";
        if (HttpMethods.IsHead(context.Request.Method)) {
            response.ContentType = "application/json; charset=utf-8";
            return;
        }
        await response.WriteAsJsonAsync(info, _json);
    }

    /// <summary>
    /// The route, so that endpoint routing selects it rather than an application's catch-all, and as
    /// anonymous. The startup middleware answers it before the route is reached; this is what carries
    /// it past an application's authorization in between.
    /// </summary>
    public static void Map(WebApplication app, RelatudeDBServer server) =>
        app.MapMethods("/" + Url, [HttpMethods.Get, HttpMethods.Head], (HttpContext context) => WriteAsync(context, server))
            .AllowAnonymous()
            .ExcludeFromDescription();
}
