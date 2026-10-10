using System.Security.Cryptography;
using Relatude.DB.Common;

namespace Relatude.DB.FileToText;

/// <summary>
/// A file-to-text provider with the texts kept on this machine, so a file read once - a document
/// indexed again, the same picture uploaded twice - is never sent, nor paid for, again here.
/// <para>A text is kept by the SHA-256 of the file and the languages asked for, most likely first. Not
/// by the name: the service judges a file by what it holds, so the same bytes under another name give
/// the same text, which is given back with the name of this call. <c>fresh</c> has the file read
/// again and keeps the new text in place of the old.</para>
/// <para>An answer from here says <see cref="FileToTextResult.Cached"/> and costs nothing;
/// <see cref="FileToTextResult.CreditsLeft"/> is what the last answer from the service said, -1 before
/// there was one. A file given as a stream is read once to find its SHA-256: from where it stands and
/// back again when the stream can seek, otherwise into a temporary file, so a video is not held in memory.</para>
/// </summary>
public sealed class CachingFileToTextProvider(IFileToTextProvider inner, IServiceAnswerCache cache) : IFileToTextProvider, ICachingServiceProvider {
    readonly ServiceAnswerCacheDefaults.CreditsSeen _credits = new();

    /// <summary>The provider that is asked when the cache has no answer.</summary>
    public IFileToTextProvider Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));
    readonly IServiceAnswerCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));

    public string Name => Inner.Name;

    public async Task<FileToTextResult> ExtractTextAsync(byte[] file, string? fileName = null, IReadOnlyList<string>? languages = null,
        bool fresh = false, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(file);
        var key = keyOf(ServiceAnswerCacheDefaults.Sha256(file), languages);
        if (!fresh && kept(key, fileName) is { } answer) return answer;
        return keep(key, await Inner.ExtractTextAsync(file, fileName, languages, fresh, cancellationToken));
    }

    public async Task<FileToTextResult> ExtractTextAsync(Stream file, string? fileName = null, IReadOnlyList<string>? languages = null,
        bool fresh = false, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(file);
        if (file.CanSeek) {
            var start = file.Position;
            var sha = await sha256Async(file, null, cancellationToken);
            var key = keyOf(sha, languages);
            if (!fresh && kept(key, fileName) is { } answer) return answer;
            file.Position = start;
            return keep(key, await Inner.ExtractTextAsync(file, fileName, languages, fresh, cancellationToken));
        }
        // read once into a file of its own while the SHA-256 is taken, and handed on from there
        var path = Path.Combine(Path.GetTempPath(), "relatude-filetotext-" + Guid.NewGuid().ToString("N"));
        await using var copy = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        var copiedSha = await sha256Async(file, copy, cancellationToken);
        var copiedKey = keyOf(copiedSha, languages);
        if (!fresh && kept(copiedKey, fileName) is { } copiedAnswer) return copiedAnswer;
        copy.Position = 0;
        return keep(copiedKey, await Inner.ExtractTextAsync(copy, fileName, languages, fresh, cancellationToken));
    }

    /// <summary>Costs nothing, so it is not kept: the list is the service's as it is now.</summary>
    public Task<FileToTextFormats> GetFormatsAsync(CancellationToken cancellationToken = default) => Inner.GetFormatsAsync(cancellationToken);

    public void ClearCache() => _cache.ClearAll();

    public void Dispose() {
        Inner.Dispose();
        _cache.Dispose();
    }

    // the languages in the order given - the first is the most likely, which OCR goes by - without case or blanks
    static string keyOf(string sha256, IReadOnlyList<string>? languages) =>
        "filetotext|v1|" + sha256 + "|" + string.Join(",", (languages ?? []).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim().ToLowerInvariant()));

    FileToTextResult? kept(string key, string? fileName) {
        var answer = ServiceAnswerCacheDefaults.Read<FileToTextResult>(_cache, key);
        // the service gives back the name it was sent, without any folder before it
        return answer == null ? null : answer with {
            FileName = string.IsNullOrWhiteSpace(fileName) ? answer.FileName : Path.GetFileName(fileName.Trim()),
            Credits = 0,
            CreditsLeft = _credits.Left,
            Cached = true,
        };
    }

    FileToTextResult keep(string key, FileToTextResult result) {
        _credits.Saw(result.CreditsLeft);
        _cache.Set(key, ServiceAnswerCacheDefaults.Serialize(result with { Cached = false }));
        return result;
    }

    /// <summary>The SHA-256 of what is left of the stream, copied to <paramref name="copy"/> on the way when there is one.</summary>
    static async Task<string> sha256Async(Stream source, Stream? copy, CancellationToken cancellationToken) {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0) {
            sha.AppendData(buffer, 0, read);
            if (copy != null) await copy.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (copy != null) await copy.FlushAsync(cancellationToken);
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
