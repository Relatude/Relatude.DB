using System.Net;
namespace Relatude.DB.NodeServer;
/// <summary>
/// Decides whether a request really came from a browser on this machine.
/// <para>The honest limit first: a server can prove that the TCP peer is loopback, but it cannot
/// prove that the user is at the console. Put any reverse proxy in front (IIS ARR, nginx, the Azure
/// App Service front end) and every request arrives from loopback, so the two cases are identical
/// at the socket level. Treating loopback as "trusted local user" is therefore only safe when
/// nothing is proxying, and that is what the checks below try to establish.</para>
/// <para>The shape of the test: loopback is a NECESSARY condition, checked against the connection
/// itself, which cannot be faked by a client. Everything after it is evidence that a proxy IS in
/// the path, and is only ever used to say no. That direction matters: the headers and environment
/// variables involved can be spoofed or stripped, but spoofing them can only force a login, never
/// grant one, so a client gains nothing by lying. A proxy that strips them leaves us exactly where
/// the loopback check alone would - which is why this is defence in depth, not a guarantee.</para>
/// </summary>
internal static class LocalRequest {
    /// <summary>True only for a request whose peer is loopback with no sign of a proxy in front.
    /// Fails closed: anything unknown or unusual counts as not local.</summary>
    public static bool IsLocalhost(HttpContext context) {
        if (!peerIsLoopback(context.Connection.RemoteIpAddress)) return false;
        if (_hostIsKnownToBeProxied) return false;
        if (hasProxyHeaders(context.Request.Headers)) return false;
        return true;
    }
    static bool peerIsLoopback(IPAddress? remote) {
        // no peer address at all: a non-TCP transport (in-memory test server, a unix socket that a
        // proxy is listening on). Nothing is known about the caller, so it is not local.
        if (remote == null) return false;
        // a v4 client on a dual mode socket arrives as ::ffff:127.0.0.1, which IsLoopback rejects
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        return IPAddress.IsLoopback(remote); // ::1 and the whole 127.0.0.0/8 block
    }
    // Headers a reverse proxy adds. Their presence means something is relaying for us and the
    // loopback peer is that relay, not a local browser. Never used to identify the caller - only
    // to refuse the bypass - so a forged header costs an attacker a login, and gains them nothing.
    static readonly string[] _proxyHeaders = [
        "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "Forwarded", // de facto and RFC 7239
        "X-Real-IP", "X-Client-IP", "X-Cluster-Client-IP", "True-Client-IP",     // common variants
        "X-ARR-LOG-ID",                                                          // IIS Application Request Routing
        "X-Azure-Ref", "X-Azure-ClientIP", "X-Azure-SocketIP",                   // Azure Front Door / App Service
        "CF-Connecting-IP", "Fastly-Client-IP",                                  // Cloudflare, Fastly
    ];
    static bool hasProxyHeaders(IHeaderDictionary headers) {
        foreach (var name in _proxyHeaders) if (headers.ContainsKey(name)) return true;
        return false;
    }
    // Hosting environments that always sit behind a front end. Read once: they are process wide.
    // Absence proves nothing (an unknown proxy leaves no trace here), so this only ever adds a no.
    static readonly bool _hostIsKnownToBeProxied = detectProxiedHost();
    static bool detectProxiedHost() {
        foreach (var name in (string[])[
            "WEBSITE_SITE_NAME",        // Azure App Service and Functions, always behind the platform front end
            "ASPNETCORE_ANCM_HTTPS_PORT", "ASPNETCORE_TOKEN", // ASP.NET Core Module: IIS proxies to our Kestrel
            "KUBERNETES_SERVICE_HOST",  // in a cluster, traffic arrives through a service or ingress
        ]) {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))) return true;
        }
        return false;
    }
}
