using System.Text.Json.Serialization;

namespace Relatude.DB.FileToText;

/// <summary>
/// The text of a file, plain, for indexing or for reading: documents, spreadsheets, presentations,
/// e-mails, e-books, PDFs, pictures whose text is read by OCR, and the speech of recordings and videos,
/// transcribed with when each line is spoken. Reached through <c>NodeStore.Services.FileToText</c>.
///
/// <para>One implementation ships: <c>RelatudeServicesFileToTextProvider</c>, which calls the hosted
/// Relatude FileToText service and charges each file to the license. The interface is here so that a
/// reader of your own can be plugged in later without any calling code changing, the way
/// <see cref="Relatude.DB.SMS.ISMSProvider"/> is.</para>
///
/// <para>A file is judged by what it holds, not by what it is called: a PDF named notes.txt is read as
/// a PDF. The name only settles what the bytes leave open, such as which kind of text a file is.
/// <see cref="GetFormatsAsync"/> lists the kinds of file the service knows, and which it reads.</para>
///
/// <para>The same file read twice, in the same languages, is answered from what the service kept, at
/// the price it sets for that. <c>fresh</c> has it read again, and paid for again.</para>
///
/// <para><b>Recordings and videos.</b> Their text is a transcript (<see cref="FileToTextResult.Timed"/>):
/// a line for each stretch of speech, beginning with when it is spoken, which <see cref="Transcript"/>
/// takes the times out of for an index and writes as subtitles. The service reads sound only, so the
/// Relatude provider sends a video's sound, which the database's FFmpeg file converter
/// (Relatude.DB.Plugins.FFMpeg) takes out on this machine; without it a video is refused, and a
/// recording is sent as it is.</para>
///
/// <para>An implementation is long lived and shared: it is built once when the database is configured
/// and disposed with it, so it must be safe to call from several threads at once.</para>
/// </summary>
public interface IFileToTextProvider : IDisposable {
    /// <summary>What this provider is, for the admin UI and the log.</summary>
    string Name { get; }

    /// <summary>
    /// The text of the file. <paramref name="fileName"/> is what it is called - any folder before it is
    /// dropped - and is given back; <paramref name="languages"/> are language codes, such as nb and en,
    /// most likely first, which help OCR.
    /// <para>A file nothing reads is refused before anything is charged, with a
    /// <see cref="Relatude.DB.Common.RelatudeServiceException"/> whose status is 415.</para>
    /// <para>A recording longer than the service takes in one call is sent in parts, each charged as a
    /// file, and the transcript is put together, timed from the start of the whole.</para>
    /// </summary>
    Task<FileToTextResult> ExtractTextAsync(byte[] file, string? fileName = null, IReadOnlyList<string>? languages = null,
        bool fresh = false, CancellationToken cancellationToken = default);

    /// <summary>The text of the file read from <paramref name="file"/>, which is read to its end first. See the overload taking the bytes.</summary>
    async Task<FileToTextResult> ExtractTextAsync(Stream file, string? fileName = null, IReadOnlyList<string>? languages = null,
        bool fresh = false, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(file);
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);
        return await ExtractTextAsync(buffer.ToArray(), fileName, languages, fresh, cancellationToken);
    }

    /// <summary>
    /// Every kind of file the service knows and whether it reads it there, what a file costs, and how
    /// large a file may be. Costs nothing, and needs no license.
    /// </summary>
    Task<FileToTextFormats> GetFormatsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The text of a file, plain. Every line ends in \n, and pages, where the file has them, are separated
/// by a form feed (\f), as pdftotext does; <see cref="Pages"/> counts them. <see cref="Format"/> is
/// the key of the kind of file it was taken to be (see <see cref="FileToTextFormats"/>).
/// <see cref="Truncated"/> says the text is not all the file holds - it was cut at the service's limit,
/// or a reader stopped at a limit of its own - and <see cref="Ocr"/> that some of it was read from
/// pictures. Title, Author and Language are what the file says of itself, when it says; for a recording,
/// Language is the language spoken.
/// <see cref="Credits"/> is what the call cost, <see cref="CreditsLeft"/> what is left on the license's
/// account, and <see cref="Cached"/> that the text was read before and given again.
/// </summary>
public sealed record FileToTextResult(string Text, string Format, string? FileName, int Characters, int? Pages, bool Truncated, bool Ocr,
    string? Title, string? Author, string? Language, int Credits, int CreditsLeft, bool Cached) {
    /// <summary>The text page by page, split at the form feeds. One page for a file without pages.</summary>
    [JsonIgnore]
    public string[] PageTexts => Text.Split('\f');

    /// <summary>
    /// The text is a transcript of a recording or of the sound of a video: a line for each stretch of
    /// speech, beginning with when it starts and ends, such as
    /// <c>[00:01:02.500 --&gt; 00:01:05.250] And that is how it began.</c> (see <see cref="Transcript"/>).
    /// </summary>
    public bool Timed { get; init; }

    /// <summary>How long the recording plays, in seconds; null for a file that is not one.</summary>
    public double? Duration { get; init; }

    /// <summary>The text without the time at the start of each line, as an index wants it: for a file that is not a recording, the text.</summary>
    [JsonIgnore]
    public string TextWithoutTimestamps => Timed ? Transcript.RemoveTimestamps(Text) : Text;

    /// <summary>The lines of a transcript, with when each is spoken; none for a file that is not a recording.</summary>
    [JsonIgnore]
    public IReadOnlyList<TranscriptCue> Cues => Timed ? Transcript.Parse(Text) : [];

    /// <summary>The transcript as WebVTT subtitles, for a video player's &lt;track&gt;; just the header for a file that is not a recording.</summary>
    public string ToWebVtt() => Transcript.ToWebVtt(Cues);

    /// <summary>The transcript as SubRip (.srt) subtitles; empty for a file that is not a recording.</summary>
    public string ToSrt() => Transcript.ToSrt(Cues);
}

/// <summary>
/// Every kind of file the service knows, and whether it reads it there; what a file costs, and what it
/// costs when its text was read before; and how files reach the service: up to
/// <see cref="PartBytes"/> in one request, larger in parts of that size, at most <see cref="MaxFileBytes"/>.
/// </summary>
public sealed record FileToTextFormats(FileToTextFormat[] Formats, int CreditsPerOperation, int CreditsPerCachedOperation, int PartBytes, long MaxFileBytes) {
    /// <summary>
    /// The longest recording the service reads in one call, in minutes; 0 when it sets no limit, or
    /// does not say. A longer one is sent in parts, each charged as a file.
    /// </summary>
    public int MaxAudioMinutes { get; init; }

    /// <summary>The kind of file a name's extension says, when the service knows it. The service itself judges a file by its bytes.</summary>
    public FileToTextFormat? ForFileName(string? fileName) {
        var extension = Path.GetExtension(fileName ?? "").TrimStart('.');
        if (extension.Length == 0) return null;
        return Formats.FirstOrDefault(f => f.Extensions.Any(e => string.Equals(e.TrimStart('.'), extension, StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>
/// One kind of file: its key, a name for people, its group (text, markup, pdf, document, spreadsheet,
/// presentation, email, ebook, image, audio, video, other), its media type, the extensions it goes by,
/// and whether it is read there. Videos are known there and not read: the Relatude provider sends their sound.
/// </summary>
public sealed record FileToTextFormat(string Key, string Name, string Group, string MediaType, string[] Extensions, bool Available);

/// <summary>
/// How the database reaches a service that reads the text of files. The shape follows
/// <see cref="Relatude.DB.SMS.SMSProviderSettings"/>: a type name resolved when the database opens, an
/// endpoint, and the key that pays for the calls. The hosted Relatude service needs neither of the
/// last two on a server, which calls it with the installation's own license.
/// </summary>
public class FileToTextProviderSettings {
    /// <summary>Which implementation is used. Empty or "RelatudeServices" is the hosted Relatude FileToText service; anything else is taken as the full type name of a custom provider.</summary>
    public string? TypeName { get; set; }

    /// <summary>The root of the service. Empty uses the provider's own default; set it for a self-hosted or test deployment.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>The key a custom provider sends with. The Relatude service charges each file to the
    /// installation's license and uses the license's API key, falling back to this one only where the
    /// server has none - or where the provider is built from code, without a server.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Where the answers are kept on this machine, so the same call is not made, nor paid for, twice:
    /// Native (the default) in a file beside the AI embedding cache, Memory while the process runs, None never.
    /// Kept: the text of every file read, by its SHA-256 and the languages asked for.</summary>
    public Relatude.DB.Common.ServiceCacheType CacheType { get; set; } = Relatude.DB.Common.ServiceCacheType.Native;
}
