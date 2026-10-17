using System.Net;
using System.Text.Json;

namespace Relatude.DB.IPLookup;

/// <summary>
/// The bot check a page carries, served by the server the page comes from and reporting back to it,
/// so the browser never talks to any other server. The scripts themselves - the check and
/// Fingerprint's BotD - come from the Relatude IP lookup service (<see cref="BotCheckScripts"/>, fetched
/// and kept by <see cref="IIPLookupProvider.GetBotCheckScriptsAsync"/>), so an update there reaches every
/// installation. The routes are under the server's info path (<see cref="DefaultUrlPath"/>, <c>/-info/</c>,
/// unless the server's <c>InfoUrlPath</c> says otherwise):
/// <code>
///   GET  /-info/bot.js                  the bot check, as a file a page loads
///   GET  /-info/botd.js?v=…             Fingerprint's BotD, which it imports; kept by browsers for a year
///   POST /-info/report?isBot=…          the answer, passed on to the Relatude IP lookup service for the browser's address
/// </code>
/// A page carries it as a script element that loads bot.js (<see cref="ScriptTag"/>), or as the bot
/// check written into the page (<see cref="InlineScriptTag"/>), which saves the request for bot.js:
/// after the first page a browser has botd.js from its cache, and the report is the only request.
/// Both are what <see cref="IIPLookupProvider.GetBotScriptTag"/> and <see cref="IIPLookupProvider.GetInlineScriptTag"/> give.
/// </summary>
public static class BotCheck {
    /// <summary>Where the routes are when the server's settings say nothing else.</summary>
    public const string DefaultUrlPath = "/-info/";
    /// <summary>The bot check as a file, under the info path.</summary>
    public const string LoaderFile = "bot.js";
    /// <summary>BotD, under the info path.</summary>
    public const string BotDFile = "botd.js";
    /// <summary>Where the answer is posted, under the info path.</summary>
    public const string ReportRoute = "report";

    /// <summary>The info path as a server setting gives it, or the default: always starting and ending with a slash.</summary>
    public static string UrlPath(string? configured) {
        var path = string.IsNullOrWhiteSpace(configured) ? DefaultUrlPath : configured.Trim();
        if (!path.StartsWith('/')) path = "/" + path;
        if (!path.EndsWith('/')) path += "/";
        return path;
    }

    /// <summary>bot.js: the bot check for a page that loads it, taking the info path from its own address.</summary>
    public static string LoaderScript(BotCheckScripts scripts) =>
        "(function(){var s=document.currentScript;if(s)(" + scripts.Check + ")(new URL(\".\",s.src).href);})();";

    /// <summary>
    /// The script element that loads the bot check: <c>&lt;script async src="/-info/bot.js"&gt;&lt;/script&gt;</c>.
    /// <paramref name="nonce"/> is for a page whose Content-Security-Policy names one.
    /// </summary>
    public static string ScriptTag(string urlPath, string? nonce = null) =>
        "<script async src=\"" + WebUtility.HtmlEncode(UrlPath(urlPath) + LoaderFile) + "\"" + nonceAttribute(nonce) + "></script>";

    /// <summary>The bot check as script to write into a page: what <see cref="InlineScriptTag"/> wraps.</summary>
    public static string InlineScript(string urlPath, BotCheckScripts scripts) =>
        "(" + scripts.Check + ")(" + JsonSerializer.Serialize(UrlPath(urlPath)) + ");";

    /// <summary>
    /// The bot check written into the page, about 500 bytes, which saves the request for bot.js. Before
    /// the scripts have come from the service, <paramref name="scripts"/> is null and this is
    /// <see cref="ScriptTag"/>, whose bot.js the server serves once it has them.
    /// <paramref name="nonce"/> is for a page whose Content-Security-Policy names one; a policy
    /// that allows no inline script at all needs <see cref="ScriptTag"/>.
    /// </summary>
    public static string InlineScriptTag(string urlPath, BotCheckScripts? scripts, string? nonce = null) =>
        scripts == null ? ScriptTag(urlPath, nonce) : "<script" + nonceAttribute(nonce) + ">" + InlineScript(urlPath, scripts) + "</script>";

    static string nonceAttribute(string? nonce) => string.IsNullOrWhiteSpace(nonce) ? "" : " nonce=\"" + WebUtility.HtmlEncode(nonce.Trim()) + "\"";
}
