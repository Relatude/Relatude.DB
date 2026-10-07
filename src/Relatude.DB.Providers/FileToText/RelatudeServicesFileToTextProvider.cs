using System.Text;
using System.Text.Json;
using Relatude.DB.Common;
using Relatude.DB.Http;

namespace Relatude.DB.FileToText;

/// <summary>
/// The text of files through the hosted Relatude FileToText service (Relatude.DB.Services.FileToText),
/// over plain HttpClient.
/// <para>Like <c>RelatudeServicesAIProvider</c> it needs no account with a vendor: the service reads
/// most files itself and holds the OCR vendor's credentials, and charges every file to the license
/// behind the API key, which needs its "filetotext" credit account with a balance, and no feature.
/// A file costs the same whatever its kind, size or number of pages, and a text the service read before
/// costs what it sets for that. A refusal comes back as a <see cref="RelatudeServiceException"/>
/// repeating the service's own reason - out of credits, no "filetotext" account, a kind of file nothing
/// reads (415, before anything is charged), a damaged or locked file (422) - so what the database logs
/// is what the person configuring it needs to read.</para>
/// <para>A file is sent once and named by its SHA-256 from then on, so reading the same file again -
/// in other languages, or anew - sends only its name. A file larger than one request may carry (10 MB
/// on the hosted service) goes in parts. A call is paid for before the service reads the file, and the
/// credits are never given back, so it is repeated only on the two answers the service gives before
/// any money moves (429 and 503); see <c>RelatudeServiceClient</c>.</para>
/// <para>The key is the one issued with the license. On a server it is the installation's own, handed
/// in as <c>licenseApiKey</c>, so a database's file-to-text settings need none; elsewhere it is
/// <see cref="FileToTextProviderSettings.ApiKey"/>. A provider without either is still built, and says
/// what is missing the first time it is called.</para>
/// <para><see cref="FileToTextProviderSettings.ServiceUrl"/> is the root of the service and defaults to
/// <c>https://filetotext.services.relatude.com</c>; point it at your own deployment when the service is
/// self-hosted.</para>
/// </summary>
public class RelatudeServicesFileToTextProvider : IFileToTextProvider {
    /// <summary>The hosted service, used when the settings name no other.</summary>
    public const string DefaultServiceUrl = "https://filetotext.services.relatude.com";

    /// <summary>The short name this provider is configured under, beside its own type name.
    /// <c>LateBindings.CreateFileToTextProvider</c> resolves both, and the settings page offers this one.</summary>
    public const string ShortName = "RelatudeServices";

    readonly RelatudeServiceClient _client;

    public RelatudeServicesFileToTextProvider(FileToTextProviderSettings settings) : this(settings, null) { }

    /// <param name="settings">Where the service is, and the key to use when there is no license key.</param>
    /// <param name="licenseApiKey">The API key of the license the installation runs under, asked for at
    /// every call, so a new license takes effect without the database reopening. When it has one it is
    /// used before <see cref="FileToTextProviderSettings.ApiKey"/>, the same rule as <c>RelatudeServicesSMSProvider</c>.</param>
    public RelatudeServicesFileToTextProvider(FileToTextProviderSettings settings, Func<string?>? licenseApiKey) {
        ArgumentNullException.ThrowIfNull(settings);
        ServiceUrl = RootUrl(settings.ServiceUrl);
        _client = new RelatudeServiceClient(Name, ServiceUrl + "/api/filetotext", () => {
            var key = licenseApiKey?.Invoke();
            return string.IsNullOrWhiteSpace(key) ? settings.ApiKey : key;
        }, "There is no API key to call the Relatude FileToText service with. The service charges every file to a license: "
            + "set this installation's license key and API key under Services in the admin UI, or give the file-to-text settings an API key of their own. ",
            // the service gives each reader five minutes - a scanned PDF is read page by page - and a
            // file may go on to the next reader when the first is unsure
            TimeSpan.FromMinutes(15));
    }

    /// <summary>The root of the service this provider calls, without a trailing slash.</summary>
    public string ServiceUrl { get; }

    /// <summary>The service root a settings value names, with the hosted service as the default.</summary>
    public static string RootUrl(string? serviceUrl) => (string.IsNullOrWhiteSpace(serviceUrl) ? DefaultServiceUrl : serviceUrl.Trim()).TrimEnd('/');

    /// <summary>Whether a configured provider type name means this provider, by either of its names.</summary>
    public static bool IsProviderName(string? typeName) =>
        !string.IsNullOrWhiteSpace(typeName)
        && (typeName.Trim().Equals(nameof(RelatudeServicesFileToTextProvider), StringComparison.OrdinalIgnoreCase)
            || typeName.Trim().Equals(ShortName, StringComparison.OrdinalIgnoreCase));

    public string Name => "Relatude FileToText service";

    public async Task<FileToTextResult> ExtractTextAsync(byte[] file, string? fileName = null, IReadOnlyList<string>? languages = null,
        bool fresh = false, CancellationToken cancellationToken = default) {
        var input = ServiceFile.Of(file, nameof(file));
        var codes = (languages ?? []).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToArray();
        string body;
        using (var ms = new MemoryStream()) {
            using (var w = new Utf8JsonWriter(ms)) {
                w.WriteStartObject();
                w.WriteString("file", input.Sha256);
                // the name goes as given: the service drops any folder before it
                if (!string.IsNullOrWhiteSpace(fileName)) w.WriteString("fileName", fileName);
                if (codes.Length > 0) {
                    w.WriteStartArray("languages");
                    foreach (var code in codes) w.WriteStringValue(code);
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            body = Encoding.UTF8.GetString(ms.ToArray());
        }
        var (response, _) = await _client.PostAsync("extract", body, fresh, [input], cancellationToken);
        using (response) {
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var text = _client.Parse<FileToTextResult>(json, "extract", "the text of a file");
            return text with { Text = text.Text ?? "", Format = text.Format ?? "unknown", Cached = RelatudeServiceClient.WasCached(response) };
        }
    }

    public async Task<FileToTextFormats> GetFormatsAsync(CancellationToken cancellationToken = default) {
        var json = await _client.GetAsync("formats", cancellationToken);
        var formats = _client.Parse<FileToTextFormats>(json, "formats", "a list of formats");
        // an older service may leave a list out; it reads as empty rather than as a failure
        return formats with { Formats = [.. (formats.Formats ?? []).Select(f => f with { Extensions = f.Extensions ?? [] })] };
    }

    public void Dispose() {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}
