using System.Net;

namespace Relatude.DB.IPLookup;

/// <summary>
/// Who is behind a request, on behalf of the database: where an IP address is, whether it is a bot's,
/// what a user agent says it is, and what the visitor's own browser said about itself on the page it
/// was shown. Reached through <c>NodeStore.Services.IPLookup</c>, for code that logs or analyses
/// traffic - a page request logged in the background after its response has gone, say - and for a
/// site's icon beside a referrer.
///
/// <para>One implementation ships: <c>RelatudeServicesIPLookupProvider</c>, which calls the hosted
/// Relatude IP lookup service with the installation's license; the license needs the feature
/// <c>iplookup</c>, and nothing is charged. The interface is here so that another lookup can be plugged
/// in later without any calling code changing, the way <see cref="Relatude.DB.Translation.ITranslationProvider"/> is.</para>
///
/// <para><b>Bots that say so themselves.</b> A page can carry the bot check, <see cref="GetBotScriptTag"/>
/// with a new visit id for every page view: the browser runs Fingerprint's BotD and reports to the
/// service whether it is driven by a bot. The code that logs the request a few seconds later looks it
/// up with the same visit id, and <see cref="IPLookupResult.Visit"/> says what the browser reported -
/// matched to that very page view even when the browser reached the service by another address than
/// the site, and <see cref="VisitReport.Reported"/> false when the script has not run, which a bot
/// that runs no scripts never does.</para>
///
/// <para>An implementation is long lived and shared: it is built once when the database is configured
/// and disposed with it, so it must be safe to call from several threads at once.</para>
/// </summary>
public interface IIPLookupProvider : IDisposable {
    /// <summary>What this provider is, for the admin UI and the log.</summary>
    string Name { get; }

    /// <summary>
    /// The addresses, user agents and visits looked up, one answer for each request, in the order they
    /// were given. Any of the three may be left out of a request; an address that is not one, or is in no
    /// range, has no <see cref="IPLookupResult.Ip"/>. A long list is sent in as many calls as it takes.
    /// </summary>
    Task<IPLookupResult[]> LookUpAsync(IReadOnlyList<IPLookupRequest> requests, CancellationToken cancellationToken = default);

    /// <summary>
    /// The address of a site's icon - an SVG when it has one, otherwise the largest no larger than
    /// <paramref name="maxSize"/> pixels - or null when it has none. <paramref name="site"/> is a host
    /// name or any address on the site, such as a referrer: <c>www.example.com</c>, <c>https://example.com/page</c>.
    /// </summary>
    Task<string?> GetFaviconUrlAsync(string site, int maxSize = 64, CancellationToken cancellationToken = default);

    /// <summary>The address a page loads the bot check from (botService.js), which reports to the service it came from.</summary>
    string BotScriptUrl { get; }

    /// <summary>
    /// The script element that runs the bot check on a page, for its <c>head</c> or the end of its
    /// <c>body</c>: <c>&lt;script async src="…/botService.js" data-visit="…"&gt;&lt;/script&gt;</c>. Give
    /// every page view a new visit id, <see cref="Guid.NewGuid"/>, keep it with what is logged of the
    /// request, and look the visit up when the request is logged. Render the element for each request
    /// rather than into a page kept in an output cache: every visitor of a cached page would report for
    /// the same visit.
    /// </summary>
    string GetBotScriptTag(Guid visit) =>
        "<script async src=\"" + WebUtility.HtmlEncode(BotScriptUrl) + "\" data-visit=\"" + visit.ToString("D") + "\"></script>";

    /// <summary>One address, user agent and visit looked up. Many go faster in one call.</summary>
    async Task<IPLookupResult> LookUpAsync(string? ip, string? userAgent = null, Guid? visit = null, CancellationToken cancellationToken = default)
        => (await LookUpAsync([new IPLookupRequest(ip, userAgent, visit)], cancellationToken))[0];
}

/// <summary>
/// What to look up: an address (IPv4 or IPv6), a user agent, and the visit id the page gave the bot
/// check (<see cref="IIPLookupProvider.GetBotScriptTag"/>). Any of them may be left out.
/// </summary>
public sealed record IPLookupRequest(string? Ip, string? UserAgent = null, Guid? Visit = null);

/// <summary>
/// What was found for one request: <see cref="Ip"/> is null when the address is not one or is in no
/// range, <see cref="UserAgent"/> when none was sent, and <see cref="Visit"/> when no visit was asked about.
/// <see cref="IsBot"/>, <see cref="BotName"/> and <see cref="IsHuman"/> weigh the three together.
/// </summary>
public sealed record IPLookupResult(IPAddressInfo? Ip, UserAgentInfo? UserAgent, VisitReport? Visit) {
    /// <summary>
    /// Whether the visitor is a bot, by every sign there is: a user agent that says so, an address in a
    /// bot's published network, the visit's own bot check, and - when the visit has no report - what a
    /// browser at the address reported before. A browser that passed the bot check for this visit
    /// outweighs an earlier report from its address, never the first two.
    /// </summary>
    public bool IsBot => BotName != null;

    /// <summary>
    /// Who or what the bot is, by the strongest sign, or null when it is none: the user agent's name for
    /// it ("Googlebot"), whose network the address is in ("google", or "-" for one of the others), or
    /// what the bot check found, BotD's kind of bot ("headless_chrome") or "Script".
    /// </summary>
    public string? BotName {
        get {
            if (UserAgent is { IsBot: true } agent) return string.IsNullOrEmpty(agent.BotName) ? "-" : agent.BotName;
            if (Ip is { IsBot: true, ConfirmedFromScript: false } network) return string.IsNullOrEmpty(network.BotName) ? "-" : network.BotName;
            if (Visit is { Reported: true } visit) return visit.IsBot ? visit.BotKind ?? "Script" : null;
            if (Ip is { IsBot: true, ConfirmedFromScript: true }) return "Script";
            return null;
        }
    }

    /// <summary>
    /// Whether the visitor is a person, as far as can be told: the bot check ran for this visit and found
    /// no bot, and nothing else says it is one. Neither this nor <see cref="IsBot"/> is a visit whose
    /// browser has not reported - not yet, the script was blocked or scripts are off, or a bot that runs
    /// no scripts.
    /// </summary>
    public bool IsHuman => !IsBot && Visit is { Reported: true, IsBot: false };
}

/// <summary>
/// Where an address is, and whether it is a bot's. <see cref="IsBot"/> is a browser's own report when a
/// page with the bot check has made one from that address (<see cref="ConfirmedFromScript"/>, BotName
/// "Script"), otherwise whether the address is in a bot's published network, <see cref="BotName"/>
/// saying whose ("google", "bing", or "-"). Values the ranges lack are "-". The locations are
/// IP2Location LITE data: a product showing them has to say "This site or product includes
/// IP2Location LITE data available from https://lite.ip2location.com".
/// </summary>
public sealed record IPAddressInfo(string Ip, string CountryCode, string CountryName, string Region, string City,
    float Latitude, float Longitude, bool IsBot, bool ConfirmedFromScript, string? BotName);

/// <summary>What a user agent says it is. A bot has <see cref="BotName"/>, and no browser, operating system or device.</summary>
public sealed record UserAgentInfo(bool IsBot, string? BotName, string? Browser, string? Os, string? DeviceType);

/// <summary>
/// What the browser shown a page said about itself, for that page view. <see cref="Reported"/> is false
/// while it has said nothing - the script has not run yet, was kept from running, or never will, as for
/// a bot that runs no scripts - and once the report is no longer kept, about an hour after it was made.
/// <see cref="BotKind"/> is BotD's name for the kind of bot (headless_chrome, selenium, …), and
/// <see cref="ReportedFrom"/> the address the browser reported from, which may not be the one the site saw.
/// </summary>
public sealed record VisitReport(Guid Id, bool Reported, bool IsBot, string? BotKind, string? ReportedFrom, DateTime? ReportedUtc);

/// <summary>
/// How the database reaches an IP lookup service. The shape follows
/// <see cref="Relatude.DB.Translation.TranslationProviderSettings"/>: a type name resolved when the
/// database opens, an endpoint, and a key. The hosted Relatude service needs neither of the last two
/// on a server, which calls it with the installation's own license. There is no answer cache: what a
/// visit's browser reported changes after the first question, and a lookup costs nothing.
/// </summary>
public class IPLookupProviderSettings {
    /// <summary>Which implementation is used. Empty or "RelatudeServices" is the hosted Relatude IP lookup service; anything else is taken as the full type name of a custom provider.</summary>
    public string? TypeName { get; set; }

    /// <summary>The root of the service, which the bot check is loaded from too, so a browser must reach it. Empty uses the provider's own default; set it for a self-hosted or test deployment.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>The key a custom provider sends with. The Relatude service uses the installation's
    /// license API key, falling back to this one only where the server has none - or where the provider
    /// is built from code, without a server.</summary>
    public string? ApiKey { get; set; }
}
