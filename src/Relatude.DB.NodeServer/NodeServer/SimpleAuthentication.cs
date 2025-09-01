using System.Net;
using Relatude.DB.NodeServer;

namespace Relatude.DB.NodeServer;
public static class SimpleAuthentication {

    public static bool TokenLockedToIP { get; set; } = RelatudeDBServer.Settings.TokenLockedToIP;
    public static bool TokenCookieHttpOnly { get; set; } = true;
    public static bool TokenCookieSecure { get; set; } = RelatudeDBServer.Settings.TokenCookieSecure;
    public static bool TokenCookieSameSite { get; set; } = RelatudeDBServer.Settings.TokenCookieSameSite;
    public static double TokenCookieMaxAgeInSec { get; set; } = RelatudeDBServer.Settings.TokenCookieMaxAgeInSec;

    public static string TokenCookieName => RelatudeDBServer.Settings.TokenCookieName == null ? "WAFAdminServerToken" : RelatudeDBServer.Settings.TokenCookieName;
    public static string TokenEncryptionSalt => RelatudeDBServer.Settings.TokenEncryptionSalt == null ? SecureGuid.New().ToString() : RelatudeDBServer.Settings.TokenEncryptionSalt;
    public static string TokenEncryptionSecret => RelatudeDBServer.Settings.TokenEncryptionSecret == null ? SecureGuid.New().ToString() : RelatudeDBServer.Settings.TokenEncryptionSecret;

    static CookieOptions getCookieOptions(TimeSpan? maxAge) {
#if DEBUG
        return new CookieOptions {
            HttpOnly = TokenCookieHttpOnly,
            SameSite = SameSiteMode.None,
            Secure = true,
            MaxAge = maxAge,
        };
#else
        return new CookieOptions {
            HttpOnly = TokenCookieHttpOnly,
            SameSite = TokenCookieSameSite ? SameSiteMode.Strict : SameSiteMode.None,
            Secure = TokenCookieSecure,
            MaxAge = maxAge,
        };
#endif
    }
    static bool authenticationIsValid(HttpContext context) {
        if (TokenCookieName == null) return false;
        var requestIP = context.Connection.RemoteIpAddress + "";
        var token = context.Request.Cookies[TokenCookieName];
        if (token == null) return false;
        if (TokenUtil.IsTokenValid(token, requestIP, out var userId, (userId) => RelatudeDBServer.Settings.UserTokenId)) {
            return userId == RelatudeDBServer.Settings.MasterUserName;
        }
        return false;
    }
    static Random rnd = new();
    public static bool CredentialsAreValid(string username, string password) {
        Task.Delay(rnd.Next(50, 300)).Wait(); // random time delay to slow down brute force attacks:
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return false;
        return username.ToLower() == RelatudeDBServer.Settings.MasterUserName && password == RelatudeDBServer.Settings.MasterPassword;
    }
    public static bool IsLoggedIn(HttpContext context) {
        return authenticationIsValid(context);
    }
    public static void LogIn(HttpContext context, bool remember) {
        var requestIP = context.Connection.RemoteIpAddress + "";
        var token = TokenUtil.CreateToken(RelatudeDBServer.Settings.MasterUserName!, RelatudeDBServer.Settings.UserTokenId, requestIP);
        TimeSpan? maxAge = remember ? TimeSpan.FromSeconds(TokenCookieMaxAgeInSec) : null;
        context.Response.Cookies.Append(TokenCookieName, token, getCookieOptions(maxAge));
    }
    public static void LogOut(HttpContext context) {
        context.Response.Cookies.Delete(TokenCookieName, getCookieOptions(null));
    }
    public static Task Authorize(HttpContext context, Func<Task> next) {
        if (requireAuthentication(context) && !authenticationIsValid(context)) {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized; //  401
            return Task.CompletedTask;
        }
        return next();
    }
    static bool requireAuthentication(HttpContext context) {
        if (requestIsUnderUrl(context, RelatudeDBServer.ApiUrlRoot)) {
            if (requestIsUnderUrl(context, RelatudeDBServer.ApiUrlPublic)) {
                //Console.WriteLine("Public, url: " + context.Request.Path.Value);
                return false; // except for the login page:
            } else if (requestIsOnUrl(context, RelatudeDBServer.ApiUrlRoot)) {
                //Console.WriteLine("Root , url: " + context.Request.Path.Value);
                return false; // root, no authentication required ( index html, css, js )
            } else {
                //Console.WriteLine("Authorizing!!!, url: " + context.Request.Path.Value);
                return true;
            }
        }
        //Console.WriteLine("Not relevant , url: " + context.Request.Path.Value);
        return false; // no authentication required for other URLs
    }
    static bool requestIsOnUrl(HttpContext context, string rootPath) {
        var path = context.Request.Path.Value;
        if (path == null) return false;
        if (path.StartsWith('/')) path = path[1..];
        if (path.EndsWith('/')) path = path[0..^1];
        if (rootPath.StartsWith('/')) rootPath = rootPath[1..];
        return string.Compare(path, rootPath, true) == 0;
    }
    static bool requestIsUnderUrl(HttpContext context, string rootPath) {
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

