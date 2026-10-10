using System.Text;
using System.Text.Json;
using Relatude.DB.Common;
using Relatude.DB.FileConversion;
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
/// <para><b>Recordings and videos</b> are transcribed, with when each line is spoken (see
/// <see cref="Transcript"/>). The service reads sound only, and this provider sends it: the database's
/// file converters - the FFmpeg plugin, Relatude.DB.Plugins.FFMpeg - take the sound of a video or a
/// recording on this machine, as one channel of Ogg Opus at 16 kHz and 24 kbps, about 11 MB an hour
/// (<see cref="SpeechAdjustment"/>). It comes out the same, byte for byte, every time, so the service
/// knows the sound of a video it has read before and charges what it sets for a text kept. A recording
/// longer than the service reads in one call (<see cref="FileToTextFormats.MaxAudioMinutes"/>) is sent
/// in parts, each charged as a file, and the transcript is put together, timed from the start. Without
/// a converter a recording is sent as it is, and a video is refused (415) before anything is charged.</para>
/// </summary>
public class RelatudeServicesFileToTextProvider : IFileToTextProvider {
    /// <summary>The hosted service, used when the settings name no other.</summary>
    public const string DefaultServiceUrl = "https://filetotext.services.relatude.com";

    /// <summary>The short name this provider is configured under, beside its own type name.
    /// <c>LateBindings.CreateFileToTextProvider</c> resolves both, and the settings page offers this one.</summary>
    public const string ShortName = "RelatudeServices";

    readonly RelatudeServiceClient _client;
    readonly Func<FileConversionEngine?>? _fileConversion;

    public RelatudeServicesFileToTextProvider(FileToTextProviderSettings settings) : this(settings, null) { }

    /// <param name="settings">Where the service is, and the key to use when there is no license key.</param>
    /// <param name="licenseApiKey">The API key of the license the installation runs under, asked for at
    /// every call, so a new license takes effect without the database reopening. When it has one it is
    /// used before <see cref="FileToTextProviderSettings.ApiKey"/>, the same rule as <c>RelatudeServicesSMSProvider</c>.</param>
    /// <param name="fileConversion">The engine whose file converters take the sound of a video or a
    /// recording, asked for at every such file: the database's, which exists only once the database is
    /// open. Null, or an engine without a converter that does it, sends a recording as it is and refuses a video.</param>
    public RelatudeServicesFileToTextProvider(FileToTextProviderSettings settings, Func<string?>? licenseApiKey, Func<FileConversionEngine?>? fileConversion = null) {
        ArgumentNullException.ThrowIfNull(settings);
        _fileConversion = fileConversion;
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
        ArgumentNullException.ThrowIfNull(file, nameof(file));
        if (MediaFiles.Detect(file.AsSpan(0, Math.Min(file.Length, MediaFiles.HeadBytes))) is { } media && converterFor(media) is { } engine) {
            var path = tempPath(engine, media);
            try {
                await File.WriteAllBytesAsync(path, file, cancellationToken);
                return await transcribeAsync(engine, path, media, fileName, languages, fresh, cancellationToken);
            } finally {
                tryDelete(path);
            }
        }
        return await readAsync(file, fileName, languages, fresh, cancellationToken);
    }

    /// <summary>
    /// The text of the file read from <paramref name="file"/>. A recording or a video goes to a temp
    /// file rather than into memory - a video can be large, and only its sound is sent - and anything
    /// else is read to its end first. See the overload taking the bytes.
    /// </summary>
    public async Task<FileToTextResult> ExtractTextAsync(Stream file, string? fileName = null, IReadOnlyList<string>? languages = null,
        bool fresh = false, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(file);
        var head = new byte[MediaFiles.HeadBytes];
        int read = 0, n;
        while (read < head.Length && (n = await file.ReadAsync(head.AsMemory(read), cancellationToken)) > 0) read += n;
        if (MediaFiles.Detect(head.AsSpan(0, read)) is { } media && converterFor(media) is { } engine) {
            var path = tempPath(engine, media);
            try {
                await using (var target = File.Create(path)) {
                    await target.WriteAsync(head.AsMemory(0, read), cancellationToken);
                    await file.CopyToAsync(target, cancellationToken);
                }
                return await transcribeAsync(engine, path, media, fileName, languages, fresh, cancellationToken);
            } finally {
                tryDelete(path);
            }
        }
        using var buffer = new MemoryStream();
        buffer.Write(head, 0, read);
        await file.CopyToAsync(buffer, cancellationToken);
        return await readAsync(buffer.ToArray(), fileName, languages, fresh, cancellationToken);
    }

    /// <summary>
    /// What a recording or the sound of a video is made into before it is sent: one channel at 16 kHz,
    /// Ogg Opus at 24 kbps, the encoder tuned for speech. What speech to text needs, at about 11 MB an
    /// hour; speech to text hears at 16 kHz in any case.
    /// </summary>
    public static FileAdjustmentAudio SpeechAdjustment() =>
        new() { RequestedFormat = FileFormat.Ogg, Channels = 1, SampleRate = 16_000, BitRateKbps = 24, Speech = true };

    /// <summary>
    /// The engine that takes the sound of this kind of file, when the database has a file converter
    /// that does: the FFmpeg plugin. Null for a recording without one, which is sent as it is. A video
    /// without one is refused before anything is sent or charged: the service reads sound only.
    /// </summary>
    FileConversionEngine? converterFor(FileFormat media) {
        var engine = _fileConversion?.Invoke();
        if (engine != null && engine.CanConvert(media, FileFormat.Ogg)) return engine;
        if (FileFormatUtil.GetFileType(media) == FileType.Audio) return null;
        const string reason = "The Relatude FileToText service reads a video by its sound, and no file converter on this server takes the sound out. "
            + "Add the FFmpeg plugin (Relatude.DB.Plugins.FFMpeg) to the server's file converters: options.FileConverters.Add(new FFMpegVideoConverter()). ";
        throw new RelatudeServiceException(415, $"{media.ToString().ToUpper()} video: {reason}", reason);
    }

    /// <summary>A part is this much shorter than the longest recording the service takes, so it never runs over by a frame.</summary>
    static readonly TimeSpan _partMargin = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The transcript of a recording or a video at <paramref name="path"/>: its sound taken by the
    /// engine's converter, in parts when it plays for longer than the service takes in one call, each
    /// part read by the service and the transcripts put together, timed from the start of the whole.
    /// </summary>
    async Task<FileToTextResult> transcribeAsync(FileConversionEngine engine, string path, FileFormat media, string? fileName,
        IReadOnlyList<string>? languages, bool fresh, CancellationToken cancellationToken) {
        var maxMinutes = await maxAudioMinutesAsync(cancellationToken);
        TimeSpan? length = null;
        if (maxMinutes > 0 && engine.CanConvert(media, FileFormat.FileMetaJson)) {
            var meta = await engine.ConvertFileAsync(path, media, new FileAdjustmentMeta(), cancellationToken);
            try {
                length = BasicFileMeta.FromBytes(await File.ReadAllBytesAsync(meta, cancellationToken))?.Duration;
            } finally {
                tryDelete(meta);
            }
        }
        var part = TimeSpan.FromMinutes(Math.Max(1, maxMinutes)) - _partMargin;
        // the last part takes what is left: less than a second more is no part of its own
        var parts = maxMinutes > 0 && length is { } total && total > part ? (int)Math.Ceiling((total - TimeSpan.FromSeconds(1)) / part) : 1;
        var read = new List<(TimeSpan Offset, FileToTextResult Text)>();
        for (var i = 0; i < parts; i++) {
            var sound = SpeechAdjustment();
            if (i > 0) sound.StartMs = (int)(part * i).TotalMilliseconds;
            if (i < parts - 1) sound.DurationMs = (int)part.TotalMilliseconds;
            var converted = await engine.ConvertFileAsync(path, media, sound, cancellationToken);
            byte[] bytes;
            try {
                bytes = await File.ReadAllBytesAsync(converted, cancellationToken);
            } finally {
                tryDelete(converted);
            }
            read.Add((part * i, await readAsync(bytes, fileName, languages, fresh, cancellationToken)));
        }
        return join(read, media, length);
    }

    /// <summary>The parts' transcripts as one: timed from the start of the whole, what they cost added up, and the file taken for what it is.</summary>
    static FileToTextResult join(List<(TimeSpan Offset, FileToTextResult Text)> parts, FileFormat media, TimeSpan? length) {
        var first = parts[0].Text;
        var text = parts.Count == 1 ? first.Text
            : parts.All(p => p.Text.Timed) ? Transcript.Write(parts.SelectMany(p => Transcript.Shift(Transcript.Parse(p.Text.Text), p.Offset)))
            : string.Join("\n", parts.Select(p => p.Text.Text).Where(t => t.Length > 0));
        var played = length is { } total ? Math.Round(total.TotalSeconds, 3) : (double?)null;
        return first with {
            Text = text,
            Characters = text.Length,
            Format = MediaFiles.Key(media),
            Truncated = parts.Any(p => p.Text.Truncated),
            Language = parts.Select(p => p.Text.Language).FirstOrDefault(l => l != null),
            Credits = parts.Sum(p => p.Text.Credits),
            CreditsLeft = parts[^1].Text.CreditsLeft,
            Cached = parts.All(p => p.Text.Cached),
            Timed = parts.Any(p => p.Text.Timed),
            Duration = parts.Count == 1 ? first.Duration ?? played : played ?? parts.Sum(p => p.Text.Duration ?? 0),
        };
    }

    /// <summary>The longest recording the service takes in one call, as it last said, and when it was asked.</summary>
    sealed record AudioLimit(int Minutes, DateTime AskedUtc);
    AudioLimit? _audioLimit;
    static readonly TimeSpan _audioLimitKept = TimeSpan.FromHours(1);
    /// <summary>The service's own default, for when it cannot be asked: the call that follows says what is wrong.</summary>
    const int _defaultMaxAudioMinutes = 60;

    async Task<int> maxAudioMinutesAsync(CancellationToken cancellationToken) {
        if (_audioLimit is { } known && DateTime.UtcNow - known.AskedUtc < _audioLimitKept) return known.Minutes;
        try {
            var minutes = (await GetFormatsAsync(cancellationToken)).MaxAudioMinutes;
            _audioLimit = new(minutes, DateTime.UtcNow);
            return minutes;
        } catch (RelatudeServiceException) {
            return _defaultMaxAudioMinutes;
        }
    }

    /// <summary>A file of this kind in the engine's temp folder, or the machine's when the database keeps none on disk.</summary>
    static string tempPath(FileConversionEngine engine, FileFormat format) {
        string folder;
        try {
            folder = engine.LocalTempFolderPath;
        } catch {
            folder = Path.GetTempPath();
        }
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, Guid.NewGuid().ToString("N") + (FileFormatUtil.GetExtensionWithDot(format) ?? ".bin"));
    }

    static void tryDelete(string path) {
        try {
            File.Delete(path);
        } catch { // a temp file left behind is cleared with the engine's temp folder
        }
    }

    /// <summary>The text of a file the service reads as it is: sent once, named by its SHA-256, and asked for.</summary>
    async Task<FileToTextResult> readAsync(byte[] file, string? fileName, IReadOnlyList<string>? languages, bool fresh, CancellationToken cancellationToken) {
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
