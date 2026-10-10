using Relatude.DB.Common;

namespace Relatude.DB.Imaging;

/// <summary>
/// An imaging provider with its answers in words kept on this machine: what an image shows
/// (<see cref="ImageToMetaAsync"/>), the answers to questions about it, and that it stands the right way
/// up already (<see cref="RotateIfNeededAsync"/> leaving it as it is). The same image asked about again
/// - a picture described again on a reindex - is answered here, and not sent, nor paid for, again.
/// <para>The operations that answer with an image are not kept: a PNG of several megabytes would soon
/// fill the file, the application keeps the image it asked for anyway, and the service keeps them too.
/// A turned image is not kept for the same reason, so only "leave it as it is" is.</para>
/// <para>An answer is kept by the SHA-256 of the image and what else was asked - the language of a
/// description, the words of a question. <c>fresh</c> asks the service again and keeps the new answer
/// in place of the old. An answer from here says Cached and costs nothing; CreditsLeft is what the last
/// answer from the service said, -1 before there was one.</para>
/// </summary>
public sealed class CachingImagingProvider(IImagingProvider inner, IServiceAnswerCache cache) : IImagingProvider, ICachingServiceProvider {
    readonly ServiceAnswerCacheDefaults.CreditsSeen _credits = new();

    /// <summary>The provider that is asked when the cache has no answer.</summary>
    public IImagingProvider Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));
    readonly IServiceAnswerCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));

    public string Name => Inner.Name;

    public async Task<ImagingImage> CreateImageAsync(string description, IReadOnlyList<byte[]>? inspiration = null, int? width = null, int? height = null,
        bool transparent = false, bool fresh = false, CancellationToken cancellationToken = default)
        => seen(await Inner.CreateImageAsync(description, inspiration, width, height, transparent, fresh, cancellationToken));

    public async Task<ImagingImage> ManipulateImageAsync(byte[] image, string instruction, IReadOnlyList<byte[]>? references = null, byte[]? mask = null,
        bool fresh = false, CancellationToken cancellationToken = default)
        => seen(await Inner.ManipulateImageAsync(image, instruction, references, mask, fresh, cancellationToken));

    public async Task<ImagingImage> RemoveBackgroundAsync(byte[] image, bool fresh = false, CancellationToken cancellationToken = default)
        => seen(await Inner.RemoveBackgroundAsync(image, fresh, cancellationToken));

    public async Task<ImagingImage> UpscaleAsync(byte[] image, int factor = 2, bool fresh = false, CancellationToken cancellationToken = default)
        => seen(await Inner.UpscaleAsync(image, factor, fresh, cancellationToken));

    public async Task<ImagingImage> RemoveObjectAsync(byte[] image, byte[] mask, bool fresh = false, CancellationToken cancellationToken = default)
        => seen(await Inner.RemoveObjectAsync(image, mask, fresh, cancellationToken));

    public async Task<ImagingImage> ExpandImageAsync(byte[] image, ImageMargins margins, string? hint = null, bool fresh = false, CancellationToken cancellationToken = default)
        => seen(await Inner.ExpandImageAsync(image, margins, hint, fresh, cancellationToken));

    public async Task<ImagingImage> ShrinkImageAsync(byte[] image, ImageMargins margins, bool fresh = false, CancellationToken cancellationToken = default)
        => seen(await Inner.ShrinkImageAsync(image, margins, fresh, cancellationToken));

    public async Task<ImagingRotation> RotateIfNeededAsync(byte[] image, bool fresh = false, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(image);
        var key = "rotate-if-needed|v1|" + ServiceAnswerCacheDefaults.Sha256(image);
        if (!fresh && _cache.TryGet(key, out _)) return new ImagingRotation(0, null, 0, _credits.Left, true);
        var answer = await Inner.RotateIfNeededAsync(image, fresh, cancellationToken);
        _credits.Saw(answer.CreditsLeft);
        // only "upright already" is kept: a turned image is a picture, and pictures are not kept here
        if (answer.Rotation == 0) _cache.Set(key, "0");
        else _cache.Remove(key);
        return answer;
    }

    public Task<ImagingMeta> ImageToMetaAsync(byte[] image, string? language = null, bool fresh = false, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(image);
        var key = "image-to-meta|v1|" + ServiceAnswerCacheDefaults.Sha256(image) + "|" + (language?.Trim().ToLowerInvariant() ?? "");
        return keptOrAskedAsync(key, fresh, () => Inner.ImageToMetaAsync(image, language, fresh, cancellationToken),
            meta => meta.CreditsLeft, meta => meta with { Credits = 0, CreditsLeft = _credits.Left, Cached = true }, meta => meta with { Cached = false });
    }

    public Task<ImagingAnswer> AskAboutImageAsync(byte[] image, string question, bool fresh = false, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(question);
        var key = "ask-about-image|v1|" + ServiceAnswerCacheDefaults.Sha256(image) + "|" + question;
        return keptOrAskedAsync(key, fresh, () => Inner.AskAboutImageAsync(image, question, fresh, cancellationToken),
            a => a.CreditsLeft, a => a with { Credits = 0, CreditsLeft = _credits.Left, Cached = true }, a => a with { Cached = false });
    }

    public Task<ImagingBoolAnswer> AskAboutImageBoolAsync(byte[] image, string question, bool fresh = false, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(question);
        var key = "ask-about-image-bool|v1|" + ServiceAnswerCacheDefaults.Sha256(image) + "|" + question;
        return keptOrAskedAsync(key, fresh, () => Inner.AskAboutImageBoolAsync(image, question, fresh, cancellationToken),
            a => a.CreditsLeft, a => a with { Credits = 0, CreditsLeft = _credits.Left, Cached = true }, a => a with { Cached = false });
    }

    /// <summary>Costs nothing, so it is not kept: the list is the service's as it is now.</summary>
    public Task<ImagingOperations> GetOperationsAsync(CancellationToken cancellationToken = default) => Inner.GetOperationsAsync(cancellationToken);

    public void ClearCache() => _cache.ClearAll();

    public void Dispose() {
        Inner.Dispose();
        _cache.Dispose();
    }

    ImagingImage seen(ImagingImage image) {
        _credits.Saw(image.CreditsLeft);
        return image;
    }

    async Task<T> keptOrAskedAsync<T>(string key, bool fresh, Func<Task<T>> ask, Func<T, int> creditsLeft, Func<T, T> asKept, Func<T, T> asStored) where T : class {
        if (!fresh && ServiceAnswerCacheDefaults.Read<T>(_cache, key) is { } kept) return asKept(kept);
        var answer = await ask();
        _credits.Saw(creditsLeft(answer));
        _cache.Set(key, ServiceAnswerCacheDefaults.Serialize(asStored(answer)));
        return answer;
    }
}
