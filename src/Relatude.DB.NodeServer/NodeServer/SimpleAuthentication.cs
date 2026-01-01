using Relatude.DB.NodeServer.Settings;
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
        if (settings.TokenCookieName == null) return false;
        var requestIP = context.Connection.RemoteIpAddress + "";
        var token = context.Request.Cookies[settings.TokenCookieName];
        if (token == null) return false;

        if (isTokenValid(token, requestIP, out var userId, out var userTokenId)) {
            if (userTokenId != Guid.Empty) return false; // user ID not implemented, only one master user
            if (userId != settings.MasterUserName) return false; // only the master user is supported
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
        return StringEncryption.Encrypt(json, settings.TokenEncryptionSecret, settings.TokenEncryptionSalt);
    }
    bool isTokenValid(string? token, string requestIP, [MaybeNullWhen(false)] out string userId, [MaybeNullWhen(false)] out Guid? userTokenId) {
        userId = null;
        userTokenId = null;
        try {

            // decrypting
            if (token == null || token.Length < 30) return false; // token cannot be valid
            if (!StringEncryption.TryDecrypt(token, settings.TokenEncryptionSecret, settings.TokenEncryptionSalt, out var json)) return false; // decryption failed

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
            if (!values.TryGetValue("UserId", out var tempUserIdString)) return false; // no userId            

            // user tokenId
            if (!values.TryGetValue("UserTokenId", out var userTokenIdString)) return false; // no userTokenId
            if (!Guid.TryParse(userTokenIdString, out var tokenId)) return false; // invalid userTokenId

            // user IP?
            if (settings.TokenLockedToIP) {
                if (!values.TryGetValue("UserIP", out var userIP)) return false; // no userIP
                if (userIP != requestIP) return false; // IP mismatch
            }

            // all ok!
            userTokenId = tokenId;
            userId = tempUserIdString;
            return true;

        } catch (Exception err) {
            RelatudeDBServer.Trace("Token validation error: " + err);
        }
        // any other outcome is a failure
        userId = null;
        userTokenId = null;
        return false;
    }

    public bool AreCredentialsValid(string username, string password) {
        Task.Delay(new Random().Next(50, 300)).Wait(); // random time delay to slow down brute force attacks. and not too fast to hint valid usernames
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return false;
        return username.ToLower() == settings.MasterUserName && password == settings.MasterPassword;
    }
    public bool IsLoggedIn(HttpContext context) {
        return authenticationIsValid(context);
    }
    public void LogIn(HttpContext context, bool remember) {
        var requestIP = context.Connection.RemoteIpAddress + "";
        if(settings.MasterUserName == null) throw new Exception("No master user configured on the server.");
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

