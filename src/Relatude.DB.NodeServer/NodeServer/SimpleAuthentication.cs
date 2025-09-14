using System.Net;


namespace Relatude.DB.NodeServer;
public class SimpleAuthentication(RelatudeDBServer server) {
    
    public bool TokenLockedToIP { get; set; } = server.Settings.TokenLockedToIP;
    public bool TokenCookieHttpOnly { get; set; } = true;
    public bool TokenCookieSecure { get; set; } = server.Settings.TokenCookieSecure;
    public bool TokenCookieSameSite { get; set; } = server.Settings.TokenCookieSameSite;
    public double TokenCookieMaxAgeInSec { get; set; } = server.Settings.TokenCookieMaxAgeInSec;

    public string TokenCookieName => server.Settings.TokenCookieName == null ? "RelatudeDBToken" : server.Settings.TokenCookieName;
    public string TokenEncryptionSalt => server.Settings.TokenEncryptionSalt == null ? SecureGuid.New().ToString() : server.Settings.TokenEncryptionSalt;
    public string TokenEncryptionSecret => server.Settings.TokenEncryptionSecret == null ? SecureGuid.New().ToString() : server.Settings.TokenEncryptionSecret;

    CookieOptions getCookieOptions(TimeSpan? maxAge) {
#if DEBUG
        return new CookieOptions {
            HttpOnly = TokenCookieHttpOnly,
            //SameSite = SameSiteMode.None,
            Secure = false,
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
    bool authenticationIsValid(HttpContext context) {
        if (TokenCookieName == null) return false;
        var requestIP = context.Connection.RemoteIpAddress + "";
        var token = context.Request.Cookies[TokenCookieName];
        if (token == null) return false;
        if (TokenUtil.IsTokenValid(token, requestIP, out var userId, (userId) => server.Settings.UserTokenId)) {
            return userId == server.Settings.MasterUserName;
        }
        return false;
    }
    static Random rnd = new();
    public bool CredentialsAreValid(string username, string password) {
        Task.Delay(rnd.Next(50, 300)).Wait(); // random time delay to slow down brute force attacks:
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return false;
        return username.ToLower() == server.Settings.MasterUserName && password == server.Settings.MasterPassword;
    }
    public bool IsLoggedIn(HttpContext context) {
        return authenticationIsValid(context);
    }
    public void LogIn(HttpContext context, bool remember) {
        var requestIP = context.Connection.RemoteIpAddress + "";
        var token = TokenUtil.CreateToken(server.Settings.MasterUserName!, server.Settings.UserTokenId, requestIP);
        TimeSpan? maxAge = remember ? TimeSpan.FromSeconds(TokenCookieMaxAgeInSec) : null;
        context.Response.Cookies.Append(TokenCookieName, token, getCookieOptions(maxAge));
    }
    public void LogOut(HttpContext context) {
        context.Response.Cookies.Delete(TokenCookieName, getCookieOptions(null));
    }
    public Task Authorize(HttpContext context, Func<Task> next) {
        if (requireAuthentication(context) && !authenticationIsValid(context)) {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized; //  401
            return Task.CompletedTask;
        }
        return next();
    }
    bool requireAuthentication(HttpContext context) {
        if (requestIsUnderUrl(context, server.ApiUrlRoot)) {
            if (requestIsUnderUrl(context, server.ApiUrlPublic)) {
                //Console.WriteLine("Public, url: " + context.Request.Path.Value);
                return false; // except for the login page:
            } else if (requestIsOnUrl(context, server.ApiUrlRoot)) {
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

