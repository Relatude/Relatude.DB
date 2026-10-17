namespace Relatude.DB.IPLookup;

/// <summary>
/// Who is behind a request, on behalf of the database: where an IP address is, whether it is a bot's,
/// and what a user agent says it is. Reached through <c>NodeStore.Services.IPLookup</c>, for code that
/// logs or analyses traffic - a page request logged in the background after its response has gone,
/// say - and for a site's icon beside a referrer.
///
/// <para>One implementation ships: <c>RelatudeServicesIPLookupProvider</c>, which calls the hosted
/// Relatude IP lookup service with the installation's license; the license needs the feature
/// <c>iplookup</c>, and nothing is charged. The interface is here so that another lookup can be plugged
/// in later without any calling code changing, the way <see cref="Relatude.DB.Translation.ITranslationProvider"/> is.</para>
///
/// <para><b>Bots that say so themselves.</b> A page can carry the bot check, <see cref="GetInlineScriptTag"/>
/// or <see cref="GetBotScriptTag"/>: the browser runs Fingerprint's BotD and reports whether it is
/// driven by a bot to the server the page came from, under its info path (<see cref="BotCheck"/>,
/// <c>/-info/</c>), which passes the reports on to the service with <see cref="ReportAsync"/>. The
/// browser never talks to any other server: the scripts themselves come from the service to the
/// server (<see cref="GetBotCheckScriptsAsync"/>), which keeps them. The report is for the browser's
/// address, and a bot's sticks: once one browser at an address is found to be driven by a bot, a
/// lookup of that address says bot for everyone there (<see cref="IPAddressInfo.IsBot"/>).</para>
///
/// <para>An implementation is long lived and shared: it is built once when the database is configured
/// and disposed with it, so it must be safe to call from several threads at once.</para>
/// </summary>
public interface IIPLookupProvider : IDisposable {
    /// <summary>What this provider is, for the admin UI and the log.</summary>
    string Name { get; }

    /// <summary>
    /// The addresses and user agents looked up, one answer for each request, in the order they were
    /// given. Either may be left out of a request; an address that is not one, or is in no range, has no
    /// <see cref="IPLookupResult.Ip"/>. A long list is sent in as many calls as it takes.
    /// </summary>
    Task<IPLookupResult[]> LookUpAsync(IReadOnlyList<IPLookupRequest> requests, CancellationToken cancellationToken = default);

    /// <summary>
    /// The address of a site's icon - an SVG when it has one, otherwise the largest no larger than
    /// <paramref name="maxSize"/> pixels - or null when it has none. <paramref name="site"/> is a host
    /// name or any address on the site, such as a referrer: <c>www.example.com</c>, <c>https://example.com/page</c>.
    /// </summary>
    Task<string?> GetFaviconUrlAsync(string site, int maxSize = 64, CancellationToken cancellationToken = default);

    /// <summary>
    /// Browsers' bot checks, passed on to the service by the server their pages came from: the server's
    /// info path (<see cref="BotCheck"/>) takes them from the browsers and calls this in batches, so
    /// application code does not. The service takes each for its address, the browser's as the server saw it.
    /// </summary>
    Task ReportAsync(IReadOnlyList<BotReport> reports, CancellationToken cancellationToken = default);

    /// <summary>
    /// The bot check's scripts - the check and Fingerprint's BotD - as the service hands them out, for the
    /// server to serve under its info path. Kept once fetched, and fetched again in the background when
    /// they are an hour old; until then, and while the service cannot be reached, the scripts kept are
    /// the answer. Only the first call waits for the service, and fails when it cannot be reached.
    /// </summary>
    Task<BotCheckScripts> GetBotCheckScriptsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The bot check's scripts as they were last fetched, without waiting: null until the first fetch
    /// is done, which asking starts (see <see cref="GetBotCheckScriptsAsync"/>).
    /// </summary>
    BotCheckScripts? CachedBotCheckScripts { get; }

    /// <summary>
    /// Where the server the page comes from serves the bot check, and takes its reports: its
    /// <c>InfoUrlPath</c>, <c>/-info/</c> unless its settings say otherwise. Starts and ends with a slash.
    /// </summary>
    string InfoUrlPath => BotCheck.DefaultUrlPath;

    /// <summary>
    /// The bot check written into the page, for its <c>head</c> or the end of its <c>body</c>: about 500
    /// bytes of script that loads BotD from this server's info path - from the browser's cache after the
    /// first page - and reports there, so a page view costs one request. It is the same for every
    /// visitor, so it may go into a page kept in an output cache. <paramref name="nonce"/> is for a page
    /// whose Content-Security-Policy names one; a policy that allows no inline script needs
    /// <see cref="GetBotScriptTag"/>.
    /// <para>The script comes from the service, which the server asks once an hour at most
    /// (<see cref="CachedBotCheckScripts"/>). Until the server has it - the first page after a start - this
    /// gives <see cref="GetBotScriptTag"/>, which works the same with one request more; a page cached
    /// then keeps that tag.</para>
    /// </summary>
    string GetInlineScriptTag(string? nonce = null) => BotCheck.InlineScriptTag(InfoUrlPath, CachedBotCheckScripts, nonce);

    /// <summary>
    /// The same bot check as <see cref="GetInlineScriptTag"/>, as a script element that loads it from
    /// this server: <c>&lt;script async src="/-info/bot.js"&gt;&lt;/script&gt;</c>. One request more,
    /// for a page that may not carry inline script.
    /// </summary>
    string GetBotScriptTag(string? nonce = null) => BotCheck.ScriptTag(InfoUrlPath, nonce);

    /// <summary>One address and user agent looked up. Many go faster in one call.</summary>
    async Task<IPLookupResult> LookUpAsync(string? ip, string? userAgent = null, CancellationToken cancellationToken = default)
        => (await LookUpAsync([new IPLookupRequest(ip, userAgent)], cancellationToken))[0];
}

/// <summary>What to look up: an address (IPv4 or IPv6) and a user agent. Either may be left out.</summary>
public sealed record IPLookupRequest(string? Ip, string? UserAgent = null);

/// <summary>
/// What was found for one request: <see cref="Ip"/> is null when the address is not one or is in no
/// range, <see cref="UserAgent"/> when none was sent. <see cref="IsBot"/>, <see cref="BotName"/> and
/// <see cref="IsHuman"/> weigh the two together.
/// </summary>
public sealed record IPLookupResult(IPAddressInfo? Ip, UserAgentInfo? UserAgent) {
    /// <summary>
    /// Whether the visitor is a bot: its user agent says so, or its address is a bot's - in a network a
    /// bot's owner publishes, or where a browser's bot check has found a bot.
    /// </summary>
    public bool IsBot => BotName != null;

    /// <summary>
    /// Who or what the bot is, or null when it is none: the user agent's name for it ("Googlebot"),
    /// otherwise whose network the address is in ("google", or "-" for one of the others), or "Script"
    /// when the bot check found it.
    /// </summary>
    public string? BotName {
        get {
            if (UserAgent is { IsBot: true } agent) return string.IsNullOrEmpty(agent.BotName) ? "-" : agent.BotName;
            if (Ip is { IsBot: true } address) return string.IsNullOrEmpty(address.BotName) ? "-" : address.BotName;
            return null;
        }
    }

    /// <summary>
    /// Whether the visitor is a person, as far as can be told: a browser at its address has run the bot
    /// check, no browser there has been found to be a bot, and nothing else says it is one. Neither this
    /// nor <see cref="IsBot"/> is an address no browser has run the check from - not yet, the script was
    /// blocked or scripts are off, or a bot that runs no scripts.
    /// </summary>
    public bool IsHuman => !IsBot && Ip is { ConfirmedFromScript: true };
}

/// <summary>
/// Where an address is, and whether it is a bot's: <see cref="IsBot"/> when it is in a network a bot's
/// owner publishes (<see cref="BotName"/> saying whose: "google", "bing", or "-"), or when a browser at
/// the address has run the bot check and been found to be a bot (BotName "Script"). A bot's report
/// sticks, for everyone at the address. <see cref="ConfirmedFromScript"/> says a browser there has run
/// the check. Values the ranges lack are "-". The locations are IP2Location LITE data: a product
/// showing them has to say "This site or product includes IP2Location LITE data available from https://lite.ip2location.com".
/// </summary>
public sealed record IPAddressInfo(string Ip, string CountryCode, string CountryName, string Region, string City,
    float Latitude, float Longitude, bool IsBot, bool ConfirmedFromScript, string? BotName);

/// <summary>What a user agent says it is. A bot has <see cref="BotName"/>, and no browser, operating system or device.</summary>
public sealed record UserAgentInfo(bool IsBot, string? BotName, string? Browser, string? Os, string? DeviceType);

/// <summary>A browser's bot check as the server its page came from passes it on: the browser's address as the server saw it, and whether BotD found a bot.</summary>
public sealed record BotReport(string Ip, bool IsBot);

/// <summary>
/// The bot check as the service hands it out: <see cref="Check"/>, the check as a JavaScript function of
/// the server's info path, <c>function (root) { … }</c>, which imports <c>root + "botd.js?v=" + Version</c>
/// and posts its answer to <c>root + "report?isBot=…"</c>; <see cref="BotD"/>, Fingerprint's BotD (MIT
/// licensed, an ES module); and <see cref="Version"/>, which changes when either does.
/// </summary>
public sealed record BotCheckScripts(string Version, string Check, string BotD);

/// <summary>
/// How the database reaches an IP lookup service. The shape follows
/// <see cref="Relatude.DB.Translation.TranslationProviderSettings"/>: a type name resolved when the
/// database opens, an endpoint, and a key. The hosted Relatude service needs neither of the last two
/// on a server, which calls it with the installation's own license. There is no answer cache: what
/// browsers report about an address changes, and a lookup costs nothing.
/// </summary>
public class IPLookupProviderSettings {
    /// <summary>Which implementation is used. Empty or "RelatudeServices" is the hosted Relatude IP lookup service; anything else is taken as the full type name of a custom provider.</summary>
    public string? TypeName { get; set; }

    /// <summary>The root of the service. Only this server calls it - browsers load the bot check from this server's own info path - so it may be an address only the server reaches. Empty uses the provider's own default; set it for a self-hosted or test deployment.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>The key a custom provider sends with. The Relatude service uses the installation's
    /// license API key, falling back to this one only where the server has none - or where the provider
    /// is built from code, without a server.</summary>
    public string? ApiKey { get; set; }
}
