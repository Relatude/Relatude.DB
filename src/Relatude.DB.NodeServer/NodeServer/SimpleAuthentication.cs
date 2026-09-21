using Microsoft.Extensions.Caching.Memory;
using Relatude.DB.NodeServer.API;
using Relatude.DB.NodeServer.Settings;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
namespace Relatude.DB.NodeServer;
/// <summary>
/// A temporary simple authentication system, for a single master user, plus the sessions that a
/// sign-in through Relatude.License opens (see <see cref="LicenseLogin"/>). Based on encrypted
/// tokens stored in cookies. Will be replaced by a more complete authentication system in the future.
/// </summary>
/// <param name="server"></param>
public class SimpleAuthentication(RelatudeDBServer server) {
    /// <summary>Who a token stands for. Via is <see cref="ViaMaster"/> for the master user and
    /// <see cref="ViaLicense"/> for a user the license server vouched for.</summary>
    public sealed record TokenSession(string UserName, Guid UserTokenId, string Via, string DisplayName, DateTime? ExpiresUtc);
    public const string ViaMaster = "master";
    public const string ViaLicense = "license";

    // block IPs against brute force attacks. max 30 attempts per minute:
    FailedIpTracker _ipWall = new(TimeSpan.FromMinutes(1), 30);
    TokenValidationCache _tokenValidationCache = new(TimeSpan.FromMinutes(5)); // decrypting tokens takes time

    RelatudeDBServerSettings settings => server.Settings; // retrieve settings each time, in case they change
    CookieOptions getTokenCookieOptions(TimeSpan? maxAge) {
#if DEBUG
        return new CookieOptions {
            HttpOnly = true,
            SameSite = SameSiteMode.None,
            Secure = true,
            MaxAge = maxAge,
        };
#else
        return new CookieOptions {
            HttpOnly = true,
            SameSite = settings.TokenCookieSameSite ? SameSiteMode.Strict : SameSiteMode.None,
            Secure = settings.TokenCookieSecure,
            MaxAge = maxAge,
        };
#endif
    }
    // Authentication
    bool authenticationIsValid(HttpContext context) {
        //Stopwatch sw = Stopwatch.StartNew();
        var isLocal = LocalRequest.IsLocalhost(context);
        if (settings.NoLoginRequiredForLocalhost && isLocal) {
            return true; // allow localhost access without login
        }
        if (settings.TokenCookieName == null) return false;
        var requestIP = context.Connection.RemoteIpAddress + "";
        var token = context.Request.Cookies[settings.TokenCookieName];
        if (token == null) {
            // no session and, unless one of the two remote ways in is open, no way to ever get one
            if (!settings.AllowMasterLoginOutsideLocalhost && !settings.AllowLicenseeAdminLogin && !isLocal) warnOnceIfLockedOut();
            return false;
        }
        if (!isTokenValid(token, requestIP, out var session)) return false;
        //Console.WriteLine($"Token validated for in {sw.ElapsedMilliseconds} ms");
        return sessionIsAllowed(session, isLocal);
    }
    /// <summary>
    /// Whether a token that decrypts and has not expired is still honoured under the settings as
    /// they are now. Checked on every request on purpose: turning AllowLicenseeAdminLogin off ends
    /// every license session at once, and the master session is local unless remote login is allowed.
    /// </summary>
    bool sessionIsAllowed(TokenSession session, bool isLocal) {
        if (session.Via == ViaLicense) return settings.AllowLicenseeAdminLogin;
        if (session.UserTokenId != Guid.Empty) return false; // user token ID not implemented, only one master user
        if (session.UserName != settings.MasterUserName) return false; // only the master user is supported
        if (!settings.AllowMasterLoginOutsideLocalhost && !isLocal) {
            warnOnceIfLockedOut();
            return false; // the master session is not honoured from outside localhost
        }
        return true;
    }
    string createToken(string userId, Guid userTokenId, string userIP, string via, string displayName, DateTime? expiresUtc) {
        var values = new Dictionary<string, string> {
                { "CreatedUtcTicks", DateTime.UtcNow.Ticks.ToString() },
                { "UserId", userId.ToString() },
                { "UserTokenId", userTokenId.ToString() },
                { "UserIP", userIP },
                { "Via", via },
                { "DisplayName", displayName },
            };
        if (expiresUtc.HasValue) values["ExpiresUtcTicks"] = expiresUtc.Value.Ticks.ToString();
        var json = JsonSerializer.Serialize(values);
        var encryptionKey = string.IsNullOrWhiteSpace(settings.TokenEncryptionSecret) ? _transientEncryptionFallbackKey : settings.TokenEncryptionSecret;
        return StringEncryption.Encrypt(json, encryptionKey);
    }
    static string _transientEncryptionFallbackKey = SecureGuid.New().ToString();
    bool isTokenValid(string? token, string requestIP, [MaybeNullWhen(false)] out TokenSession session) {
        session = null;
        if (token == null || token.Length < 30) return false; // token cannot be valid
        if (_tokenValidationCache.TryGet(token, out var cached)) {
            // a cached session can still run out while it is cached
            if (cached.ExpiresUtc is { } cachedExpiry && DateTime.UtcNow > cachedExpiry) return false;
            session = cached;
            return true;
        }
        try {

            // decrypting
            var decryptionKey = string.IsNullOrWhiteSpace(settings.TokenEncryptionSecret) ? _transientEncryptionFallbackKey : settings.TokenEncryptionSecret;
            if (!StringEncryption.TryDecrypt(token, decryptionKey, out var json)) return false; // decryption failed

            // parsing json
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json); // may throw exception
            if (values == null) return false; // invalid json

            // expired?
            if (!values.TryGetValue("CreatedUtcTicks", out var createdUtcTicksString)) return false; // no createdUtcTicks
            if (!long.TryParse(createdUtcTicksString, out var createdUtcTicks)) return false; // invalid createdUtcTicks
            var createdUtc = new DateTime(createdUtcTicks, DateTimeKind.Utc);
            var age = DateTime.UtcNow.Subtract(createdUtc);
            if (age > TimeSpan.FromSeconds(settings.TokenCookieMaxAgeInSec)) return false; // token expired

            // a session that carries its own end (a license sign-in does) ends there, even before the max age
            DateTime? expiresUtc = null;
            if (values.TryGetValue("ExpiresUtcTicks", out var expiresUtcTicksString)) {
                if (!long.TryParse(expiresUtcTicksString, out var expiresUtcTicks)) return false; // invalid expiresUtcTicks
                expiresUtc = new DateTime(expiresUtcTicks, DateTimeKind.Utc);
                if (DateTime.UtcNow > expiresUtc) return false; // session over
            }

            // getting the user Id, but store it in a temporary variable
            if (!values.TryGetValue("UserId", out var tempUserName)) return false; // no userId

            // user tokenId
            if (!values.TryGetValue("UserTokenId", out var userTokenIdString)) return false; // no userTokenId
            if (!Guid.TryParse(userTokenIdString, out var tokenId)) return false; // invalid userTokenId

            // user IP?
            if (settings.TokenLockedToIP) {
                if (!values.TryGetValue("UserIP", out var userIP)) return false; // no userIP
                if (userIP != requestIP) return false; // IP mismatch
            }

            // who the token stands for; a token minted before these fields existed is a master token
            var via = values.TryGetValue("Via", out var viaValue) && !string.IsNullOrEmpty(viaValue) ? viaValue : ViaMaster;
            var displayName = values.TryGetValue("DisplayName", out var displayNameValue) && !string.IsNullOrEmpty(displayNameValue) ? displayNameValue : tempUserName;

            // all ok!
            session = new TokenSession(tempUserName, tokenId, via, displayName, expiresUtc);
            _tokenValidationCache.Add(token, session);
            return true;

        } catch (Exception err) {
            RelatudeDBServer.Trace("Token validation error: " + err?.Message);
        }
        // any other outcome is a failure
        session = null;
        return false;
    }

    public async Task<bool> AreCredentialsValid(string username, string password, string requestIP, bool isLocal) {
        await Task.Delay(new Random().Next(300, 400)); // time delay to slow down brute force attacks and random to not hint valid usernames by response time
        if (!settings.AllowMasterLoginOutsideLocalhost && !isLocal) {
            warnOnceIfLockedOut();
            return false; // block login attempts from outside localhost
        }
        if (_ipWall.IsBlocked(requestIP)) {
            RelatudeDBServer.Trace($"Login attempt from blocked IP {requestIP}, blocked attempts: {_ipWall.GetFailedAttemptCount(requestIP)}");
            _ipWall.RegisterFailedAttempt(requestIP);
            return false; // block login attempts from this IP
        }
        if (string.IsNullOrEmpty(username) // empty username is not allowed
            || string.IsNullOrEmpty(password) // empty password is not allowed
            || username.ToLower() != settings.MasterUserName // case insensitive, only one master user supported
            || password != settings.MasterPassword // password must match 100%
            ) {
            _ipWall.RegisterFailedAttempt(requestIP); // register failed attempt for this IP
            return false;
        } else {
            return true;
        }
    }
    // Every admin request is being refused because none of them look local and remote logins are
    // off. The usual cause is a reverse proxy in front of the server: the peer is then the proxy,
    // so nothing is ever "localhost" and there is no way in at all. Said once, with the fix, because
    // the alternative is an admin UI that answers 401 forever without explaining why.
    static int _lockoutWarned;
    void warnOnceIfLockedOut() {
        if (Interlocked.Exchange(ref _lockoutWarned, 1) != 0) return;
        RelatudeDBServer.Trace("Admin access refused: the request did not come from this machine, and"
            + " AllowMasterLoginOutsideLocalhost is false, so no one can log in. If this server is behind a"
            + " reverse proxy, set \"AllowMasterLoginOutsideLocalhost\": true and a master user name and"
            + " password in " + Defaults.SettingsFileName + ", or set \"AllowLicenseeAdminLogin\": true with the"
            + " license and API keys from Relatude.License to sign in with a Relatude.License account instead. ");
    }
    public bool IsLoggedIn(HttpContext context) {
        return authenticationIsValid(context);
    }

    /// <summary>
    /// Who this request is, for the admin UI to say so: the user a token proves (the master user, or
    /// a Relatude.License user by display name, with Via telling which), and nobody at all where the
    /// localhost bypass is what let the request through. The difference is not cosmetic - with the
    /// bypass there is no session to end, so logging out would delete a cookie nothing is reading
    /// and leave the caller exactly as signed in as before.
    /// </summary>
    public (string? UserName, bool ViaLocalhost, string? Via) Describe(HttpContext context) {
        var isLocal = LocalRequest.IsLocalhost(context);
        var token = settings.TokenCookieName == null ? null : context.Request.Cookies[settings.TokenCookieName];
        if (token != null
            && isTokenValid(token, context.Connection.RemoteIpAddress + "", out var session)
            && sessionIsAllowed(session, isLocal)) {
            return (session.DisplayName, false, session.Via);
        }
        return (null, settings.NoLoginRequiredForLocalhost && isLocal, null);
    }
    public void LogIn(HttpContext context, bool remember) {
        var requestIP = context.Connection.RemoteIpAddress + "";
        if (settings.MasterUserName == null) throw new Exception("No master user configured on the server.");
        var userId = Guid.Empty; // user ID not implemented, only one master user
        var token = createToken(settings.MasterUserName, userId, requestIP, ViaMaster, settings.MasterUserName, null);
        TimeSpan? maxAge = remember ? TimeSpan.FromSeconds(settings.TokenCookieMaxAgeInSec) : null;
        context.Response.Cookies.Append(settings.TokenCookieName, token, getTokenCookieOptions(maxAge));
    }
    /// <summary>
    /// Opens a session for a user the Relatude.License server vouched for (see <see cref="LicenseLogin"/>).
    /// The session ends when the license server said it should, or after TokenCookieMaxAgeInSec,
    /// whichever comes first, and it is honoured only while AllowLicenseeAdminLogin stays on.
    /// </summary>
    public void LogInLicensee(HttpContext context, string subject, string displayName, DateTime expiresUtc) {
        var requestIP = context.Connection.RemoteIpAddress + "";
        var now = DateTime.UtcNow;
        var max = TimeSpan.FromSeconds(settings.TokenCookieMaxAgeInSec);
        if (expiresUtc - now > max) expiresUtc = now + max;
        var lifetime = expiresUtc - now;
        if (lifetime <= TimeSpan.Zero) throw new Exception("The sign-in has already expired.");
        var token = createToken(ViaLicense + ":" + subject, Guid.Empty, requestIP, ViaLicense, displayName, expiresUtc);
        context.Response.Cookies.Append(settings.TokenCookieName, token, getTokenCookieOptions(lifetime));
    }
    public void LogOut(HttpContext context) {
        context.Response.Cookies.Delete(settings.TokenCookieName, getTokenCookieOptions(null));
    }

    // Authorization middleware
    public Task AuthorizationMiddleware(HttpContext context, Func<Task> next) {
        if (requireAuthentication(context) && !authenticationIsValid(context)) {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized; //  401
            return Task.CompletedTask;
        }
        return next();
    }
    public async Task StartupProgressBarMiddleware(HttpContext ctx, Func<Task> next) {
        if (server.IsShuttingDown) {
            // requests that arrive after the host began stopping (typically on a connection that was
            // already open) must not start new work on databases that are about to close
            ctx.Response.StatusCode = 503; // Service Unavailable
            ctx.Response.Headers.RetryAfter = "10";
            ctx.Response.Headers.Connection = "close";
            await ctx.Response.WriteAsync("The server is shutting down.");
            return;
        }
        var isAdminRequest = RequestIsUnderUrl(ctx, server.ApiUrlRoot);
        if (server.IsRestarting && !isAdminRequest) {
            // a soft restart is between closing the old databases and opening the new ones, so there is
            // nothing for an application request to reach. The admin API is let through, because the UI
            // that started the restart is watching it from there.
            ctx.Response.StatusCode = 503; // Service Unavailable
            ctx.Response.Headers.RetryAfter = "5";
            await ctx.Response.WriteAsync("The server is restarting.");
            return;
        }
        if (!server.AnyRemaingToAutoOpenIncludingFailed) {
            await runCounted(next, isAdminRequest);
            return;
        }
        if (RequestIsUnderUrl(ctx, ServerAPIMapper.GlobalPublicStatusUrl)) {
            // respond with status info directly, making sure other middlewares are not involved
            var result = ServerAPIMapper.StatusResponse(server);
            await ctx.Response.WriteAsJsonAsync(result);
            return;
        }
        if (RequestIsUnderUrl(ctx, server.ApiUrlRoot)) {
            // allow access to API urls, DB admin works as usual
            await next();
            return;
        }
        // any other request during startup will respond with startup progress page ( typically the root / )
        ctx.Response.StatusCode = 503; // Service Unavailable
        ctx.Response.ContentType = "text/html";
        ctx.Response.Headers.RetryAfter = "10"; // suggest retry after 5 seconds
        var html = ServerAPIMapper.GetResource("ClientStart.start.html");
        html = html.Replace("GLOBALSTATUSURL", ServerAPIMapper.GlobalPublicStatusUrl);
        await ctx.Response.WriteAsync(html);
    }
    /// <summary>
    /// Runs the rest of the pipeline, counting the request while it is in there so that a soft restart
    /// can wait for it before disposing the databases it is using. Admin requests are not counted: the
    /// restart is started and watched from the admin API, and the event stream the admin UI holds open
    /// never completes, so counting those would leave the count permanently above zero.
    /// </summary>
    async Task runCounted(Func<Task> next, bool isAdminRequest) {
        if (isAdminRequest) {
            await next();
            return;
        }
        server.EnterRequest();
        try {
            await next();
        } finally {
            server.ExitRequest();
        }
    }

    bool requireAuthentication(HttpContext context) {
        if (RequestIsUnderUrl(context, server.ApiUrlRoot)) {
            if (RequestIsUnderUrl(context, server.ApiUrlPublic)) {
                return false; // except for the login page:
            } else if (RequestIsOnUrl(context, server.ApiUrlRoot)) {
                return false; // root, no authentication required ( index html, css, js )
            } else {
                return true;
            }
        }
        //Console.WriteLine("Not relevant , url: " + context.Request.Path.Value);
        return false; // no authentication required for other URLs
    }
    public static bool RequestIsOnUrl(HttpContext context, string rootPath) {
        var path = context.Request.Path.Value;
        if (path == null) return false;
        if (path.StartsWith('/')) path = path[1..];
        if (path.EndsWith('/')) path = path[0..^1];
        if (rootPath.StartsWith('/')) rootPath = rootPath[1..];
        return string.Compare(path, rootPath, true) == 0;
    }
    public static bool RequestIsUnderUrl(HttpContext context, string rootPath) {
        var path = context.Request.Path.Value;
        if (path == null) return false;
        var rootPaths = rootPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pathParts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (rootPaths.Length > pathParts.Length) return false;
        for (int i = 0; i < rootPaths.Length; i++) {
            if (!string.Equals(rootPaths[i], pathParts[i], StringComparison.InvariantCultureIgnoreCase)) {
                return false;
            }
        }
        return true;
    }

}

// Counts failed login attempts by IP within a sliding time window, thread-safe
sealed class FailedIpTracker(TimeSpan window, int maxAttemptsPerIp) {
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _attempts = new();
    public void RegisterFailedAttempt(string ip) {
        var queue = _attempts.GetOrAdd(ip, _ => new Queue<DateTime>());
        lock (queue) {
            prune(queue);
            queue.Enqueue(DateTime.UtcNow);
        }
    }
    public int GetFailedAttemptCount(string ip) {
        if (!_attempts.TryGetValue(ip, out var queue)) return 0;
        lock (queue) {
            prune(queue);
            return queue.Count;
        }
    }
    public bool IsBlocked(string ip) => GetFailedAttemptCount(ip) >= maxAttemptsPerIp;
    void prune(Queue<DateTime> queue) {
        var cutoff = DateTime.UtcNow - window;
        while (queue.Count > 0 && queue.Peek() < cutoff) queue.Dequeue();
    }
}

// timelimited cache for token decryption results:

public sealed class TokenValidationCache(TimeSpan cacheDuration) : IDisposable {
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public void Add(string token, SimpleAuthentication.TokenSession session) {
        _cache.Set(token, session, cacheDuration);
    }

    public bool TryGet(string token, [MaybeNullWhen(false)] out SimpleAuthentication.TokenSession session) {
        if (_cache.TryGetValue(token, out SimpleAuthentication.TokenSession? entry) && entry is not null) {
            session = entry;
            return true;
        }
        session = null;
        return false;
    }

    public void Dispose() => _cache.Dispose();
}
