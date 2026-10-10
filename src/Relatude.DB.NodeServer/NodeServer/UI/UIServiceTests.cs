using Microsoft.AspNetCore.Http.Features;
using Relatude.DB.FileToText;
using Relatude.DB.Imaging;
using Relatude.DB.NodeServer.Json;
using Relatude.DB.Translation;
using System.Globalization;
using System.Text.Json;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// The Imaging, FileToText and Translation test panels of the Relatude Services page: real calls to the
/// hosted services with this installation's own API key, so whoever set up the license can see them work
/// before any code depends on them. Like the SMS and AI tests they are the services, not a database:
/// no database's settings are read beyond the address the page sends, and every call is charged to the
/// license like any other. The license server is asked first, so a license without the credit account
/// a call is charged to is told so here rather than by a refusal from the service.
///
/// <para>What the services offer and what they cost are commands, since they are small JSON and cost
/// nothing, and so are the Translation calls, which carry nothing but text. The Imaging and FileToText
/// calls carry images and files both ways, so they are two routes of their own taking a multipart form -
/// the files as files, the rest as fields - rather than base64 in a command. An image comes back as the PNG itself with what it cost in headers, as the service sends
/// it; everything else as JSON. A refusal is <c>{ error }</c>, worded by the service.</para>
/// </summary>
sealed class UIServiceTests(RelatudeDBServer server) {
    /// <summary>The key of the credit account the Relatude Imaging service charges every operation to. It is what licenses the calls: no feature is needed.</summary>
    internal const string ImagingAccountKey = "ai_image";

    /// <summary>The key of the credit account the Relatude FileToText service charges every file to.</summary>
    internal const string FileToTextAccountKey = "filetotext";

    /// <summary>The key of the credit account the Relatude Translation service charges every call to.</summary>
    internal const string TranslationAccountKey = "translation";

    /// <summary>The most a test may send in one request: a few images, or one file, well past what the services take of either.</summary>
    const long _maxRequestBytes = 256L * 1024 * 1024;

    internal void Register(UICommands commands) {
        commands.Register("license-imaging-operations", async ctx => {
            using var provider = imagingProvider(ctx.Payload<ServicePayload>().ServiceUrl);
            return await provider.GetOperationsAsync(ctx.Http.RequestAborted);
        });
        commands.Register("license-filetotext-formats", async ctx => {
            using var provider = fileToTextProvider(ctx.Payload<ServicePayload>().ServiceUrl);
            return await provider.GetFormatsAsync(ctx.Http.RequestAborted);
        });
        commands.Register("license-translation-languages", async ctx => {
            using var provider = translationProvider(ctx.Payload<ServicePayload>().ServiceUrl);
            return await provider.GetLanguagesAsync(ctx.Http.RequestAborted);
        });
        commands.Register("license-translation-test", async ctx => await translationTestAsync(ctx.Payload<TranslationTestPayload>(), ctx.Http.RequestAborted));
    }

    internal void Map(WebApplication app, string path) {
        app.MapPost(path + "imaging-test", (Delegate)imagingTestAsync);
        app.MapPost(path + "filetotext-test", (Delegate)fileToTextTestAsync);
    }

    sealed record ServicePayload(string? ServiceUrl);

    /// <summary>
    /// A Translation test: translate, or detect the languages of, the texts. Each text may name its own
    /// languages, as the service lets it; the call's To and From are for the texts that name none.
    /// </summary>
    sealed record TranslationTestPayload(string? ServiceUrl, string? Operation, TranslationTestText[]? Texts, string? To, string? From, string? Format, bool Fresh);
    sealed record TranslationTestText(string? Text, string? From, string? To);

    RelatudeServicesImagingProvider imagingProvider(string? serviceUrl) =>
        new(new ImagingProviderSettings { ServiceUrl = string.IsNullOrWhiteSpace(serviceUrl) ? null : serviceUrl.Trim() }, () => server.Settings.ApiKey);

    // a video's sound is taken by the default database's file converters, as its own provider takes it
    RelatudeServicesFileToTextProvider fileToTextProvider(string? serviceUrl) =>
        new(new FileToTextProviderSettings { ServiceUrl = string.IsNullOrWhiteSpace(serviceUrl) ? null : serviceUrl.Trim() }, () => server.Settings.ApiKey,
            () => (server.DefaultContainer?.Store?.Datastore as Relatude.DB.DataStores.DataStoreLocal)?.FileConversion);

    RelatudeServicesTranslationProvider translationProvider(string? serviceUrl) =>
        new(new TranslationProviderSettings { ServiceUrl = string.IsNullOrWhiteSpace(serviceUrl) ? null : serviceUrl.Trim() }, () => server.Settings.ApiKey);

    /// <summary>
    /// The service URL a database here is set up with, for the Relatude service rather than a custom
    /// provider, so a test panel starts from the address the installation really calls. Null when no
    /// database names one, which is the hosted service.
    /// </summary>
    internal static string? ConfiguredImagingUrl(RelatudeDBServer server) => server.GetContainers()
        .Select(c => c.Settings.ImagingSettings)
        .Where(s => s != null && (string.IsNullOrWhiteSpace(s.TypeName) || RelatudeServicesImagingProvider.IsProviderName(s.TypeName)))
        .Select(s => s!.ServiceUrl)
        .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));

    /// <summary>The FileToText address a database here is set up with; see <see cref="ConfiguredImagingUrl"/>.</summary>
    internal static string? ConfiguredFileToTextUrl(RelatudeDBServer server) => server.GetContainers()
        .Select(c => c.Settings.FileToTextSettings)
        .Where(s => s != null && (string.IsNullOrWhiteSpace(s.TypeName) || RelatudeServicesFileToTextProvider.IsProviderName(s.TypeName)))
        .Select(s => s!.ServiceUrl)
        .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));

    /// <summary>The Translation address a database here is set up with; see <see cref="ConfiguredImagingUrl"/>.</summary>
    internal static string? ConfiguredTranslationUrl(RelatudeDBServer server) => server.GetContainers()
        .Select(c => c.Settings.TranslationSettings)
        .Where(s => s != null && (string.IsNullOrWhiteSpace(s.TypeName) || RelatudeServicesTranslationProvider.IsProviderName(s.TypeName)))
        .Select(s => s!.ServiceUrl)
        .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));

    /// <summary>Refuses a call the license cannot pay for, in the words the SMS and AI tests use.</summary>
    async Task requireAccountAsync(string accountKey, string service, CancellationToken cancellationToken) {
        var status = await server.LicenseLogin.DescribeAsync(cancellationToken);
        if (status.State != "valid" || status.License == null) throw new InvalidOperationException($"There is no valid license to call the {service} with. " + status.Reason);
        if (!status.License.Active) throw new InvalidOperationException("The license is " + (status.License.Expired ? "expired." : "disabled."));
        if (!UILicense.CarriesAccount(status.License, accountKey)) throw new InvalidOperationException($"This license has no '{accountKey}' credit account to charge the call to.");
    }

    /// <summary>
    /// One operation of the Imaging service. The form names the operation and carries its fields as
    /// the service names them: description, instruction, hint, question and language as text; width, height,
    /// factor and the four margins as numbers; transparent and fresh as "true"; and the images as
    /// files under image, mask, inspiration and references. Every image answer comes back as the PNG,
    /// with what it cost, its size and its SHA-256 in headers; image-to-meta and the questions as JSON. rotate-if-needed
    /// comes back as the image turned, with X-Rotation saying how far, or as JSON with a rotation of 0
    /// when the image was left as it is.
    /// </summary>
    async Task<IResult> imagingTestAsync(HttpContext ctx) {
        try {
            var form = await readFormAsync(ctx);
            var cancellationToken = ctx.RequestAborted;
            var operation = text(form, "operation") ?? throw new ArgumentException("Choose an operation. ");
            await requireAccountAsync(ImagingAccountKey, "Relatude Imaging service", cancellationToken);
            using var provider = imagingProvider(text(form, "serviceUrl"));
            var fresh = flag(form, "fresh");
            ImagingImage image;
            switch (operation) {
                case "create-image":
                    image = await provider.CreateImageAsync(text(form, "description") ?? "", await filesAsync(form, "inspiration", cancellationToken),
                        number(form, "width"), number(form, "height"), flag(form, "transparent"), fresh, cancellationToken);
                    break;
                case "manipulate-image":
                    image = await provider.ManipulateImageAsync(await requiredFileAsync(form, "image", cancellationToken), text(form, "instruction") ?? "",
                        await filesAsync(form, "references", cancellationToken), await fileAsync(form, "mask", cancellationToken), fresh, cancellationToken);
                    break;
                case "remove-background":
                    image = await provider.RemoveBackgroundAsync(await requiredFileAsync(form, "image", cancellationToken), fresh, cancellationToken);
                    break;
                case "upscale":
                    image = await provider.UpscaleAsync(await requiredFileAsync(form, "image", cancellationToken), number(form, "factor") ?? 2, fresh, cancellationToken);
                    break;
                case "remove-object":
                    image = await provider.RemoveObjectAsync(await requiredFileAsync(form, "image", cancellationToken),
                        await fileAsync(form, "mask", cancellationToken) ?? throw new ArgumentException("Choose a mask: white where the object is, black elsewhere. "), fresh, cancellationToken);
                    break;
                case "expand-image":
                    image = await provider.ExpandImageAsync(await requiredFileAsync(form, "image", cancellationToken), margins(form), text(form, "hint"), fresh, cancellationToken);
                    break;
                case "shrink-image":
                    image = await provider.ShrinkImageAsync(await requiredFileAsync(form, "image", cancellationToken), margins(form), fresh, cancellationToken);
                    break;
                case "rotate-if-needed":
                    var rotation = await provider.RotateIfNeededAsync(await requiredFileAsync(form, "image", cancellationToken), fresh, cancellationToken);
                    if (rotation.Image == null) return Results.Json(new { rotation.Rotation, rotation.Credits, rotation.CreditsLeft, rotation.Cached }, RelatudeDBJsonOptions.Default);
                    ctx.Response.Headers["X-Rotation"] = rotation.Rotation.ToString(CultureInfo.InvariantCulture);
                    image = rotation.Image;
                    break;
                case "image-to-meta":
                    var meta = await provider.ImageToMetaAsync(await requiredFileAsync(form, "image", cancellationToken), text(form, "language"), fresh, cancellationToken);
                    return Results.Json(meta, RelatudeDBJsonOptions.Default);
                case "ask-about-image":
                    var answer = await provider.AskAboutImageAsync(await requiredFileAsync(form, "image", cancellationToken), text(form, "question") ?? "", fresh, cancellationToken);
                    return Results.Json(answer, RelatudeDBJsonOptions.Default);
                case "ask-about-image-bool":
                    var yesNo = await provider.AskAboutImageBoolAsync(await requiredFileAsync(form, "image", cancellationToken), text(form, "question") ?? "", fresh, cancellationToken);
                    return Results.Json(yesNo, RelatudeDBJsonOptions.Default);
                default:
                    throw new ArgumentException("There is no operation called '" + operation + "'. ");
            }
            var headers = ctx.Response.Headers;
            headers["X-Credits"] = image.Credits.ToString(CultureInfo.InvariantCulture);
            headers["X-Credits-Left"] = image.CreditsLeft.ToString(CultureInfo.InvariantCulture);
            headers["X-Sha256"] = image.Sha256;
            headers["X-Cache"] = image.Cached ? "hit" : "miss";
            headers["X-Width"] = image.Width.ToString(CultureInfo.InvariantCulture);
            headers["X-Height"] = image.Height.ToString(CultureInfo.InvariantCulture);
            headers.CacheControl = "no-store";
            return Results.File(image.Png, "image/png");
        } catch (Exception error) when (!ctx.RequestAborted.IsCancellationRequested) {
            return refused(error);
        }
    }

    /// <summary>
    /// One file read by the FileToText service: the file under file, the languages as one field of
    /// codes separated by commas or spaces, and fresh as "true". The answer is the service's own, the
    /// whole text included; for a recording or a video also the text without its timestamps and the
    /// transcript as WebVTT subtitles, as Transcript makes them. The file is read as a stream, so a
    /// video goes to a temp file for its sound to be taken, not into memory.
    /// </summary>
    async Task<IResult> fileToTextTestAsync(HttpContext ctx) {
        try {
            var form = await readFormAsync(ctx);
            var cancellationToken = ctx.RequestAborted;
            var file = form.Files.GetFile("file") ?? throw new ArgumentException("Choose a file. ");
            var languages = (text(form, "languages") ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            await requireAccountAsync(FileToTextAccountKey, "Relatude FileToText service", cancellationToken);
            using var provider = fileToTextProvider(text(form, "serviceUrl"));
            await using var stream = file.OpenReadStream();
            var result = await provider.ExtractTextAsync(stream, file.FileName, languages, flag(form, "fresh"), cancellationToken);
            var answer = JsonSerializer.SerializeToNode(result, RelatudeDBJsonOptions.Default)!.AsObject();
            if (result.Timed) {
                answer["textWithoutTimestamps"] = result.TextWithoutTimestamps;
                answer["webVtt"] = result.ToWebVtt();
            }
            return Results.Json(answer, RelatudeDBJsonOptions.Default);
        } catch (Exception error) when (!ctx.RequestAborted.IsCancellationRequested) {
            return refused(error);
        }
    }

    /// <summary>
    /// The texts translated, or their languages found, by the Translation service: the answer is the
    /// service's own, one translation or one language for each text in the order sent.
    /// </summary>
    async Task<object> translationTestAsync(TranslationTestPayload payload, CancellationToken cancellationToken) {
        var texts = (payload.Texts ?? []).Where(t => !string.IsNullOrWhiteSpace(t.Text)).ToArray();
        if (texts.Length == 0) throw new ArgumentException("Write a text to translate. ");
        await requireAccountAsync(TranslationAccountKey, "Relatude Translation service", cancellationToken);
        using var provider = translationProvider(payload.ServiceUrl);
        switch (payload.Operation) {
            case "translate":
                var format = string.Equals(payload.Format, "html", StringComparison.OrdinalIgnoreCase) ? TranslationFormat.Html : TranslationFormat.Text;
                return await provider.TranslateAsync(texts.Select(t => new TranslationText(t.Text!, t.From, t.To)).ToArray(), payload.To, payload.From, format, payload.Fresh, cancellationToken);
            case "detect":
                return await provider.DetectLanguagesAsync(texts.Select(t => t.Text!).ToArray(), payload.Fresh, cancellationToken);
            default:
                throw new ArgumentException("There is no translation operation called '" + payload.Operation + "'. ");
        }
    }

    static IResult refused(Exception error) =>
        Results.Json(new { error = error.Message }, RelatudeDBJsonOptions.Default, statusCode: error is ArgumentException ? 400 : 500);

    static async Task<IFormCollection> readFormAsync(HttpContext ctx) {
        // a few images, or a file of up to the 50 MB the FileToText service reads, is past the server's own request limit
        var size = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (size != null && !size.IsReadOnly) size.MaxRequestBodySize = _maxRequestBytes;
        if (!ctx.Request.HasFormContentType) throw new ArgumentException("Expected a multipart form. ");
        return await ctx.Request.ReadFormAsync(ctx.RequestAborted);
    }

    static string? text(IFormCollection form, string name) {
        var value = form[name].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    static bool flag(IFormCollection form, string name) => string.Equals(text(form, name), "true", StringComparison.OrdinalIgnoreCase);

    static int? number(IFormCollection form, string name) {
        var value = text(form, name);
        if (value == null) return null;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : throw new ArgumentException($"'{name}' is not a whole number. ");
    }

    static ImageMargins margins(IFormCollection form) => new(number(form, "top") ?? 0, number(form, "right") ?? 0, number(form, "bottom") ?? 0, number(form, "left") ?? 0);

    static async Task<byte[]> readAsync(IFormFile file, CancellationToken cancellationToken) {
        using var buffer = new MemoryStream((int)Math.Min(file.Length, int.MaxValue));
        await file.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    static async Task<byte[]?> fileAsync(IFormCollection form, string name, CancellationToken cancellationToken) =>
        form.Files.GetFile(name) is { Length: > 0 } file ? await readAsync(file, cancellationToken) : null;

    static async Task<byte[]> requiredFileAsync(IFormCollection form, string name, CancellationToken cancellationToken) =>
        await fileAsync(form, name, cancellationToken) ?? throw new ArgumentException($"Choose an image for '{name}'. ");

    static async Task<byte[][]> filesAsync(IFormCollection form, string name, CancellationToken cancellationToken) {
        var files = form.Files.GetFiles(name).Where(f => f.Length > 0).ToArray();
        var bytes = new byte[files.Length][];
        for (var i = 0; i < files.Length; i++) bytes[i] = await readAsync(files[i], cancellationToken);
        return bytes;
    }
}
