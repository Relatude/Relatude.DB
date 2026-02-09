using Microsoft.Extensions.Caching.Memory;
using Relatude.DB.NodeServer.Settings;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
namespace Relatude.DB.NodeServer;
/// <summary>
/// A temporary simple authentication system, for a single master user.
/// Based on encrypted tokens stored in cookies.
/// Will be replaced by a more complete authentication system in the future.
/// </summary>
/// <param name="server"></param>
public class SimpleAuthentication(RelatudeDBServer server) {

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
        if (settings.TokenCookieName == null) return false;
        var requestIP = context.Connection.RemoteIpAddress + "";
        var token = context.Request.Cookies[settings.TokenCookieName];
        if (token == null) return false;

        if (isTokenValid(token, requestIP, out var userName, out var userTokenId)) {
            // user Token ID is used to reset all users saved logins by changing the token ID stored on the server
            if (userTokenId != Guid.Empty) return false; // user token ID not implemented, only one master user
            if (userName != settings.MasterUserName) return false; // only the master user is supported
            //Console.WriteLine($"Token validated for in {sw.ElapsedMilliseconds} ms");
            return true;
        }
        return false;
    }
    string createToken(string userId, Guid userTokenId, string userIP) {
        var values = new Dictionary<string, string> {
                { "CreatedUtcTicks", DateTime.UtcNow.Ticks.ToString() },
                { "UserId", userId.ToString() },
                { "UserTokenId", userTokenId.ToString() },
                { "UserIP", userIP }
            };
        var json = JsonSerializer.Serialize(values);
        return StringEncryption.Encrypt(json, settings.TokenEncryptionSecret);
    }
    bool isTokenValid(string? token, string requestIP, [MaybeNullWhen(false)] out string userName, [MaybeNullWhen(false)] out Guid? userTokenId) {
        userName = null;
        userTokenId = null;
        if (token == null || token.Length < 30) return false; // token cannot be valid
        if (_tokenValidationCache.TryGet(token, out var isValid, out userName, out userTokenId)) {
            if (!isValid) return false; // cached result is invalid
            return true;
        }
        try {

            // decrypting
            if (!StringEncryption.TryDecrypt(token, settings.TokenEncryptionSecret, out var json)) return false; // decryption failed

            // parsing json
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json); // may throw exception
            if (values == null) return false; // invalid json

            // expired?
            if (!values.TryGetValue("CreatedUtcTicks", out var createdUtcTicksString)) return false; // no createdUtcTicks
            if (!long.TryParse(createdUtcTicksString, out var createdUtcTicks)) return false; // invalid createdUtcTicks
            var createdUtc = new DateTime(createdUtcTicks, DateTimeKind.Utc);
            var age = DateTime.UtcNow.Subtract(createdUtc);
            if (age > TimeSpan.FromSeconds(settings.TokenCookieMaxAgeInSec)) return false; // token expired

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

            // all ok!
            userTokenId = tokenId; // userTokenId can be used to reset all saved logins

            userName = tempUserName;
            _tokenValidationCache.Add(token, true, userName, userTokenId);
            return true;

        } catch (Exception err) {
            RelatudeDBServer.Trace("Token validation error: " + err?.Message);
        }
        // any other outcome is a failure
        userName = null;
        userTokenId = null;
        return false;
    }

    public async Task<bool> AreCredentialsValid(string username, string password, string requestIP) {
        await Task.Delay(new Random().Next(300, 400)); // time delay to slow down brute force attacks and random to not hint valid usernames by response time        
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
    public bool IsLoggedIn(HttpContext context) {
        return authenticationIsValid(context);
    }
    public void LogIn(HttpContext context, bool remember) {
        var requestIP = context.Connection.RemoteIpAddress + "";
        if (settings.MasterUserName == null) throw new Exception("No master user configured on the server.");
        var userId = Guid.Empty; // user ID not implemented, only one master user
        var token = createToken(settings.MasterUserName, userId, requestIP);
        TimeSpan? maxAge = remember ? TimeSpan.FromSeconds(settings.TokenCookieMaxAgeInSec) : null;
        context.Response.Cookies.Append(settings.TokenCookieName, token, getTokenCookieOptions(maxAge));
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
        if (!server.AnyRemaingToAutoOpenIncludingFailed) {
            await next();
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

    // Internal record to group the results cleanly
    private record CacheEntry(bool IsValid, string UserName, Guid? UserTokenId);

    public void Add(string token, bool isValid, string userName, Guid? userTokenId) {
        _cache.Set(token, new CacheEntry(isValid, userName, userTokenId), cacheDuration);
    }

    public bool TryGet(string token, out bool isValid, [MaybeNullWhen(false)] out string userName, [MaybeNullWhen(false)] out Guid? userTokenId) {
        if (_cache.TryGetValue(token, out CacheEntry? entry) && entry is not null) {
            isValid = entry.IsValid;
            userName = entry.UserName;
            userTokenId = entry.UserTokenId;
            return true;
        }

        (isValid, userName, userTokenId) = (false, null, null);
        return false;
    }

    public void Dispose() => _cache.Dispose();
}