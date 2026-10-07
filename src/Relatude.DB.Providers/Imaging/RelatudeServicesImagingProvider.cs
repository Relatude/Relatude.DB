using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Relatude.DB.Common;
using Relatude.DB.Http;

namespace Relatude.DB.Imaging;

/// <summary>
/// Image AI through the hosted Relatude Imaging service (Relatude.DB.Services.Imaging), over plain HttpClient.
/// <para>Like <c>RelatudeServicesAIProvider</c> it needs no account with a vendor: the service holds the
/// vendor credentials and charges every call to the license behind the API key, which needs its
/// "ai_image" credit account with a balance, and no feature. Every operation costs the same, whichever
/// vendor does it, and an answer the service made before for the same call costs what the service sets
/// for that. A refusal comes back as a <see cref="RelatudeServiceException"/> repeating the
/// service's own reason - out of credits, no "ai_image" account, an operation not offered, content
/// turned down - so what the database logs is what the person configuring it needs to read.</para>
/// <para>Images are sent once and named by their SHA-256 from then on: the first call with an image
/// uploads it, and later calls with the same bytes - an answer passed on to the next operation
/// included - send only its name. A call is paid for before the service hands it to a provider, and
/// the credits are never given back, so it is repeated only on the two answers the service gives before
/// any money moves (429 and 503); see <c>RelatudeServiceClient</c>.</para>
/// <para>The key is the one issued with the license. On a server it is the installation's own, handed
/// in as <c>licenseApiKey</c>, so a database's imaging settings need none; elsewhere it is
/// <see cref="ImagingProviderSettings.ApiKey"/>. A provider without either is still built, and says
/// what is missing the first time it is called.</para>
/// <para><see cref="ImagingProviderSettings.ServiceUrl"/> is the root of the service and defaults to
/// <c>https://imaging.services.relatude.com</c>; point it at your own deployment when the service is
/// self-hosted.</para>
/// </summary>
public class RelatudeServicesImagingProvider : IImagingProvider {
    /// <summary>The hosted service, used when the settings name no other.</summary>
    public const string DefaultServiceUrl = "https://imaging.services.relatude.com";

    /// <summary>The short name this provider is configured under, beside its own type name.
    /// <c>LateBindings.CreateImagingProvider</c> resolves both, and the settings page offers this one.</summary>
    public const string ShortName = "RelatudeServices";

    readonly RelatudeServiceClient _client;

    public RelatudeServicesImagingProvider(ImagingProviderSettings settings) : this(settings, null) { }

    /// <param name="settings">Where the service is, and the key to use when there is no license key.</param>
    /// <param name="licenseApiKey">The API key of the license the installation runs under, asked for at
    /// every call, so a new license takes effect without the database reopening. When it has one it is
    /// used before <see cref="ImagingProviderSettings.ApiKey"/>, the same rule as <c>RelatudeServicesSMSProvider</c>.</param>
    public RelatudeServicesImagingProvider(ImagingProviderSettings settings, Func<string?>? licenseApiKey) {
        ArgumentNullException.ThrowIfNull(settings);
        ServiceUrl = RootUrl(settings.ServiceUrl);
        _client = new RelatudeServiceClient(Name, ServiceUrl + "/api/imaging", () => {
            var key = licenseApiKey?.Invoke();
            return string.IsNullOrWhiteSpace(key) ? settings.ApiKey : key;
        }, "There is no API key to call the Relatude Imaging service with. The service charges every call to a license: "
            + "set this installation's license key and API key under Services in the admin UI, or give the imaging settings an API key of their own. ",
            // the service gives each provider three minutes, and a call may go on to a second one
            TimeSpan.FromMinutes(10));
    }

    /// <summary>The root of the service this provider calls, without a trailing slash.</summary>
    public string ServiceUrl { get; }

    /// <summary>The service root a settings value names, with the hosted service as the default.</summary>
    public static string RootUrl(string? serviceUrl) => (string.IsNullOrWhiteSpace(serviceUrl) ? DefaultServiceUrl : serviceUrl.Trim()).TrimEnd('/');

    /// <summary>Whether a configured provider type name means this provider, by either of its names.</summary>
    public static bool IsProviderName(string? typeName) =>
        !string.IsNullOrWhiteSpace(typeName)
        && (typeName.Trim().Equals(nameof(RelatudeServicesImagingProvider), StringComparison.OrdinalIgnoreCase)
            || typeName.Trim().Equals(ShortName, StringComparison.OrdinalIgnoreCase));

    public string Name => "Relatude Imaging service";

    public Task<ImagingImage> CreateImageAsync(string description, IReadOnlyList<byte[]>? inspiration = null, int? width = null, int? height = null,
        bool transparent = false, bool fresh = false, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("A description is required. ", nameof(description));
        if (width.HasValue != height.HasValue) throw new ArgumentException("Give both a width and a height, or neither. ", width.HasValue ? nameof(height) : nameof(width));
        var images = list(inspiration, nameof(inspiration));
        var body = write(w => {
            w.WriteString("description", description);
            writeList(w, "inspiration", images);
            if (width is { } x) w.WriteNumber("width", x);
            if (height is { } y) w.WriteNumber("height", y);
            if (transparent) w.WriteBoolean("transparent", true);
        });
        return imageAsync("create-image", body, fresh, images, cancellationToken);
    }

    public Task<ImagingImage> ManipulateImageAsync(byte[] image, string instruction, IReadOnlyList<byte[]>? references = null, byte[]? mask = null,
        bool fresh = false, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(instruction)) throw new ArgumentException("An instruction is required. ", nameof(instruction));
        var input = ServiceFile.Of(image, nameof(image));
        var others = list(references, nameof(references));
        var masked = mask == null ? null : ServiceFile.Of(mask, nameof(mask));
        var body = write(w => {
            w.WriteString("instruction", instruction);
            w.WriteString("image", input.Sha256);
            writeList(w, "references", others);
            if (masked != null) w.WriteString("mask", masked.Sha256);
        });
        ServiceFile[] files = masked == null ? [input, .. others] : [input, .. others, masked];
        return imageAsync("manipulate-image", body, fresh, files, cancellationToken);
    }

    public Task<ImagingImage> RemoveBackgroundAsync(byte[] image, bool fresh = false, CancellationToken cancellationToken = default) {
        var input = ServiceFile.Of(image, nameof(image));
        return imageAsync("remove-background", write(w => w.WriteString("image", input.Sha256)), fresh, [input], cancellationToken);
    }

    public Task<ImagingImage> UpscaleAsync(byte[] image, int factor = 2, bool fresh = false, CancellationToken cancellationToken = default) {
        if (factor is not (2 or 4)) throw new ArgumentOutOfRangeException(nameof(factor), factor, "An image is upscaled 2 or 4 times. ");
        var input = ServiceFile.Of(image, nameof(image));
        var body = write(w => {
            w.WriteString("image", input.Sha256);
            w.WriteNumber("factor", factor);
        });
        return imageAsync("upscale", body, fresh, [input], cancellationToken);
    }

    public Task<ImagingImage> RemoveObjectAsync(byte[] image, byte[] mask, bool fresh = false, CancellationToken cancellationToken = default) {
        var input = ServiceFile.Of(image, nameof(image));
        var masked = ServiceFile.Of(mask, nameof(mask));
        var body = write(w => {
            w.WriteString("image", input.Sha256);
            w.WriteString("mask", masked.Sha256);
        });
        return imageAsync("remove-object", body, fresh, [input, masked], cancellationToken);
    }

    public Task<ImagingImage> ExpandImageAsync(byte[] image, ImageMargins margins, string? hint = null, bool fresh = false, CancellationToken cancellationToken = default) {
        var input = ServiceFile.Of(image, nameof(image));
        checkMargins(margins);
        var body = write(w => {
            w.WriteString("image", input.Sha256);
            writeMargins(w, margins);
            if (!string.IsNullOrWhiteSpace(hint)) w.WriteString("hint", hint);
        });
        return imageAsync("expand-image", body, fresh, [input], cancellationToken);
    }

    public Task<ImagingImage> ShrinkImageAsync(byte[] image, ImageMargins margins, bool fresh = false, CancellationToken cancellationToken = default) {
        var input = ServiceFile.Of(image, nameof(image));
        checkMargins(margins);
        var body = write(w => {
            w.WriteString("image", input.Sha256);
            writeMargins(w, margins);
        });
        return imageAsync("shrink-image", body, fresh, [input], cancellationToken);
    }

    /// <summary>
    /// The service answers the image turned, as any image answer, with X-Rotation saying how far; or no
    /// content and a rotation of 0 when it left the image as it is.
    /// </summary>
    public async Task<ImagingRotation> RotateIfNeededAsync(byte[] image, bool fresh = false, CancellationToken cancellationToken = default) {
        var input = ServiceFile.Of(image, nameof(image));
        const string operation = "rotate-if-needed";
        var (response, key) = await _client.PostAsync(operation, write(w => w.WriteString("image", input.Sha256)), fresh, [input], cancellationToken);
        using (response) {
            var said = RelatudeServiceClient.Header(response, "X-Rotation");
            if (!int.TryParse(said, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rotation) || rotation is not (0 or 90 or 180 or 270))
                throw new RelatudeServiceException(0, $"The {Name} answered {operation} without a rotation of 0, 90, 180 or 270: '{said}'. ");
            var credits = RelatudeServiceClient.IntHeader(response, "X-Credits");
            var creditsLeft = RelatudeServiceClient.IntHeader(response, "X-Credits-Left");
            var cached = RelatudeServiceClient.WasCached(response);
            if (rotation == 0) return new ImagingRotation(0, null, credits, creditsLeft, cached);
            var png = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (png.Length == 0) throw new RelatudeServiceException(0, $"The {Name} answered {operation} with a rotation of {rotation} but without the image. ");
            var turned = imageOf(png, response, key);
            return new ImagingRotation(rotation, turned, credits, creditsLeft, cached);
        }
    }

    sealed record MetaAnswer(string? Title, string? Description, string[]? Keywords, string? Language, ImagingFocusPoint? Focus, ImagingObject[]? Objects, int Credits, int CreditsLeft);

    public async Task<ImagingMeta> ImageToMetaAsync(byte[] image, string? language = null, bool fresh = false, CancellationToken cancellationToken = default) {
        var input = ServiceFile.Of(image, nameof(image));
        var body = write(w => {
            w.WriteString("image", input.Sha256);
            if (!string.IsNullOrWhiteSpace(language)) w.WriteString("language", language.Trim());
        });
        var (response, _) = await _client.PostAsync("image-to-meta", body, fresh, [input], cancellationToken);
        using (response) {
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var meta = _client.Parse<MetaAnswer>(json, "image-to-meta", "what an image shows");
            return new ImagingMeta(meta.Title ?? "", meta.Description ?? "", meta.Keywords ?? [], meta.Language, meta.Focus, meta.Objects ?? [],
                meta.Credits, meta.CreditsLeft, RelatudeServiceClient.WasCached(response));
        }
    }

    sealed record TextAnswer(string? Answer, int Credits, int CreditsLeft);

    public async Task<ImagingAnswer> AskAboutImageAsync(byte[] image, string question, bool fresh = false, CancellationToken cancellationToken = default) {
        var answer = await askAsync<TextAnswer>("ask-about-image", image, question, fresh, "an answer", cancellationToken);
        return new ImagingAnswer(answer.Value.Answer ?? "", answer.Value.Credits, answer.Value.CreditsLeft, answer.Cached);
    }

    sealed record BoolAnswer(bool Answer, int Certainty, int Credits, int CreditsLeft);

    public async Task<ImagingBoolAnswer> AskAboutImageBoolAsync(byte[] image, string question, bool fresh = false, CancellationToken cancellationToken = default) {
        var answer = await askAsync<BoolAnswer>("ask-about-image-bool", image, question, fresh, "a yes or a no", cancellationToken);
        return new ImagingBoolAnswer(answer.Value.Answer, answer.Value.Certainty, answer.Value.Credits, answer.Value.CreditsLeft, answer.Cached);
    }

    /// <summary>A question about an image, and the service's JSON answer to it, read as <typeparamref name="T"/>.</summary>
    async Task<(T Value, bool Cached)> askAsync<T>(string operation, byte[] image, string question, bool fresh, string what, CancellationToken cancellationToken) where T : class {
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("A question is required. ", nameof(question));
        var input = ServiceFile.Of(image, nameof(image));
        var body = write(w => {
            w.WriteString("image", input.Sha256);
            w.WriteString("question", question);
        });
        var (response, _) = await _client.PostAsync(operation, body, fresh, [input], cancellationToken);
        using (response) {
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return (_client.Parse<T>(json, operation, what), RelatudeServiceClient.WasCached(response));
        }
    }

    public async Task<ImagingOperations> GetOperationsAsync(CancellationToken cancellationToken = default) {
        var json = await _client.GetAsync("operations", cancellationToken);
        var operations = _client.Parse<ImagingOperations>(json, "operations", "a list of operations");
        // an older service may leave the list out; it reads as nothing offered rather than as a failure
        return operations with { Operations = operations.Operations ?? [] };
    }

    /// <summary>
    /// Makes a call whose answer is an image: the PNG as the body, what it cost and what it is in the
    /// headers. The service holds the answer for the license from then on, so this client remembers it,
    /// and passing it to the next operation sends only its name.
    /// </summary>
    async Task<ImagingImage> imageAsync(string operation, string body, bool fresh, ServiceFile[] files, CancellationToken cancellationToken) {
        var (response, key) = await _client.PostAsync(operation, body, fresh, files, cancellationToken);
        using (response) {
            var png = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (png.Length == 0) throw new RelatudeServiceException(0, $"The {Name} answered {operation} without an image. ");
            return imageOf(png, response, key);
        }
    }

    /// <summary>An image answer's PNG with what the headers say of it; the service holds it for the license from then on.</summary>
    ImagingImage imageOf(byte[] png, HttpResponseMessage response, string key) {
        var sha256 = RelatudeServiceClient.Header(response, "X-Sha256")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(sha256)) sha256 = ServiceFile.Sha256Of(png);
        _client.MarkHeld(key, sha256);
        var (width, height) = PngSize(png);
        return new ImagingImage(png, width, height, sha256,
            RelatudeServiceClient.IntHeader(response, "X-Credits"), RelatudeServiceClient.IntHeader(response, "X-Credits-Left"), RelatudeServiceClient.WasCached(response));
    }

    /// <summary>The width and height a PNG's header gives, or zeros for anything that is not a PNG.</summary>
    public static (int Width, int Height) PngSize(ReadOnlySpan<byte> png) {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        // the signature, then the IHDR chunk: its length, its type, and the width and height first in it
        if (png.Length < 24 || !png[..8].SequenceEqual(signature) || !png.Slice(12, 4).SequenceEqual("IHDR"u8)) return (0, 0);
        return ((int)BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4)), (int)BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4)));
    }

    static ServiceFile[] list(IReadOnlyList<byte[]>? images, string parameterName) {
        if (images == null || images.Count == 0) return [];
        var files = new ServiceFile[images.Count];
        for (var i = 0; i < images.Count; i++) files[i] = ServiceFile.Of(images[i] ?? throw new ArgumentException($"{parameterName}[{i}] is null. ", parameterName), parameterName);
        return files;
    }

    static void writeList(Utf8JsonWriter w, string name, ServiceFile[] files) {
        if (files.Length == 0) return;
        w.WriteStartArray(name);
        foreach (var file in files) w.WriteStringValue(file.Sha256);
        w.WriteEndArray();
    }

    static void checkMargins(ImageMargins margins) {
        ArgumentNullException.ThrowIfNull(margins);
        if (margins.Top < 0 || margins.Right < 0 || margins.Bottom < 0 || margins.Left < 0) throw new ArgumentOutOfRangeException(nameof(margins), margins, "A margin cannot be less than 0. ");
        if (margins.Top + margins.Right + margins.Bottom + margins.Left == 0) throw new ArgumentException("At least one margin must be more than 0. ", nameof(margins));
    }

    // only the sides that move: a side left out is 0 to the service
    static void writeMargins(Utf8JsonWriter w, ImageMargins margins) {
        if (margins.Top > 0) w.WriteNumber("top", margins.Top);
        if (margins.Right > 0) w.WriteNumber("right", margins.Right);
        if (margins.Bottom > 0) w.WriteNumber("bottom", margins.Bottom);
        if (margins.Left > 0) w.WriteNumber("left", margins.Left);
    }

    static string write(Action<Utf8JsonWriter> properties) {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) {
            w.WriteStartObject();
            properties(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public void Dispose() {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}
