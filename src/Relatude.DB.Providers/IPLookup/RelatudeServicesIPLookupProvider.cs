using System.Globalization;
using System.Text;
using System.Text.Json;
using Relatude.DB.Common;
using Relatude.DB.Http;

namespace Relatude.DB.IPLookup;

/// <summary>
/// IP lookups through the hosted Relatude IP lookup service (Relatude.DB.Services.IPLookup), over plain HttpClient.
/// <para>It needs no account with a vendor and charges nothing: the service holds the IP ranges itself,
/// and lets in the license behind the API key when the license has the feature "iplookup". A refusal
/// comes back as a <see cref="RelatudeServiceException"/> repeating the service's own reason - no such
/// feature, a key it does not know - so what the database logs is what the person configuring it needs
/// to read. As nothing is charged, a call is repeated on every failure that may pass, a timeout included.</para>
/// <para>The key is the one issued with the license. On a server it is the installation's own, handed
/// in as <c>licenseApiKey</c>, so a database's IP lookup settings need none; elsewhere it is
/// <see cref="IPLookupProviderSettings.ApiKey"/>. A provider without either is still built, and says
/// what is missing the first time it is called. The bot check needs no key: a browser reports for itself.</para>
/// <para><see cref="IPLookupProviderSettings.ServiceUrl"/> is the root of the service and defaults to
/// <c>https://iplookup.relatude.com</c>; point it at your own deployment when the service is self-hosted.
/// Browsers load the bot check from it too, so it has to be an address they reach.</para>
/// </summary>
public class RelatudeServicesIPLookupProvider : IIPLookupProvider {
    /// <summary>The hosted service, used when the settings name no other.</summary>
    public const string DefaultServiceUrl = "https://iplookup.relatude.com";

    /// <summary>The short name this provider is configured under, beside its own type name.
    /// <c>LateBindings.CreateIPLookupProvider</c> resolves both, and the settings page offers this one.</summary>
    public const string ShortName = "RelatudeServices";

    /// <summary>The most lookups sent in one call; a longer list goes in several. The hosted service takes up to 10,000.</summary>
    public const int LookupsPerCall = 1000;

    readonly RelatudeServiceClient _client;

    public RelatudeServicesIPLookupProvider(IPLookupProviderSettings settings) : this(settings, null) { }

    /// <param name="settings">Where the service is, and the key to use when there is no license key.</param>
    /// <param name="licenseApiKey">The API key of the license the installation runs under, asked for at
    /// every call, so a new license takes effect without the database reopening. When it has one it is
    /// used before <see cref="IPLookupProviderSettings.ApiKey"/>, the same rule as <c>RelatudeServicesSMSProvider</c>.</param>
    public RelatudeServicesIPLookupProvider(IPLookupProviderSettings settings, Func<string?>? licenseApiKey) {
        ArgumentNullException.ThrowIfNull(settings);
        ServiceUrl = RootUrl(settings.ServiceUrl);
        _client = new RelatudeServiceClient(Name, ServiceUrl + "/api/iplookup", () => {
            var key = licenseApiKey?.Invoke();
            return string.IsNullOrWhiteSpace(key) ? settings.ApiKey : key;
        }, "There is no API key to call the Relatude IP lookup service with. The service needs the key of a license with the feature \"iplookup\": "
            + "set this installation's license key and API key under Services in the admin UI, or give the IP lookup settings an API key of their own. ",
            // a lookup takes microseconds; a site's icon may take the service a couple of requests to find
            TimeSpan.FromSeconds(60));
    }

    /// <summary>The root of the service this provider calls, without a trailing slash.</summary>
    public string ServiceUrl { get; }

    /// <summary>The service root a settings value names, with the hosted service as the default.</summary>
    public static string RootUrl(string? serviceUrl) => (string.IsNullOrWhiteSpace(serviceUrl) ? DefaultServiceUrl : serviceUrl.Trim()).TrimEnd('/');

    /// <summary>Whether a configured provider type name means this provider, by either of its names.</summary>
    public static bool IsProviderName(string? typeName) =>
        !string.IsNullOrWhiteSpace(typeName)
        && (typeName.Trim().Equals(nameof(RelatudeServicesIPLookupProvider), StringComparison.OrdinalIgnoreCase)
            || typeName.Trim().Equals(ShortName, StringComparison.OrdinalIgnoreCase));

    public string Name => "Relatude IP lookup service";

    public string BotScriptUrl => ServiceUrl + "/botService.js";

    public async Task<IPLookupResult[]> LookUpAsync(IReadOnlyList<IPLookupRequest> requests, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(requests);
        for (var i = 0; i < requests.Count; i++) {
            var request = requests[i] ?? throw new ArgumentException($"requests[{i}] is null. ", nameof(requests));
            if (request.Visit == Guid.Empty) throw new ArgumentException($"requests[{i}] has the empty Guid as its visit: give every page view a Guid.NewGuid(). ", nameof(requests));
        }
        var answers = new IPLookupResult[requests.Count];
        for (var start = 0; start < requests.Count; start += LookupsPerCall) {
            var count = Math.Min(LookupsPerCall, requests.Count - start);
            var json = await _client.SendFreeAsync(HttpMethod.Post, "lookupmany", body(requests, start, count), cancellationToken);
            var part = _client.Parse<IPLookupResult[]>(json, "lookupmany", "a list of lookups");
            if (part.Length != count) throw new RelatudeServiceException(0, $"The {Name} answered {count} lookups with {part.Length}. ");
            part.CopyTo(answers, start);
        }
        return answers;
    }

    sealed record FaviconAnswer(string? Name, string? Url);

    public async Task<string?> GetFaviconUrlAsync(string site, int maxSize = 64, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(site)) throw new ArgumentException("Give the site whose icon to find, such as example.com. ", nameof(site));
        var query = "favicon?name=" + Uri.EscapeDataString(site.Trim()) + "&maxSize=" + Math.Clamp(maxSize, 1, 1024).ToString(CultureInfo.InvariantCulture);
        var json = await _client.SendFreeAsync(HttpMethod.Get, query, null, cancellationToken);
        var answer = _client.Parse<FaviconAnswer>(json, "favicon", "a site's icon");
        return string.IsNullOrWhiteSpace(answer.Url) ? null : answer.Url;
    }

    // [{ ip, userAgent, visit }, …], each field only when the request has it
    static string body(IReadOnlyList<IPLookupRequest> requests, int start, int count) {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) {
            w.WriteStartArray();
            for (var i = start; i < start + count; i++) {
                var request = requests[i];
                w.WriteStartObject();
                if (request.Ip != null) w.WriteString("ip", request.Ip);
                if (request.UserAgent != null) w.WriteString("userAgent", request.UserAgent);
                if (request.Visit is { } visit) w.WriteString("visit", visit.ToString("D"));
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public void Dispose() {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}
