using Relatude.DB.Common;
using Relatude.DB.FileToText;
using Relatude.DB.Imaging;
using Relatude.DB.IO;
using Relatude.DB.Translation;

namespace Relatude.DB.NodeServer;

/// <summary>
/// The caches in front of a database's Imaging, FileToText and Translation providers: what a service
/// answered is kept on this machine, so the same question is not sent over HTTP, nor paid for, twice
/// (see <see cref="CachingImagingProvider"/>, <see cref="CachingFileToTextProvider"/> and
/// <see cref="CachingTranslationProvider"/>). The files are Native KV, one per service, in the indexes
/// folder beside the AI embedding cache: <c>native.imaging.cache.bin</c> and so on.
/// <para>A cache is only a copy, so one that cannot be opened - a file another process holds, a disk
/// that refuses - leaves the provider without one, with a warning, rather than keeping the database
/// from opening.</para>
/// </summary>
public static class ServiceCaches {
    public static IImagingProvider Wrap(IImagingProvider provider, ServiceCacheType type, string? folder, List<string> warnings)
        => Create(type, folder, "imaging", warnings) is { } cache ? new CachingImagingProvider(provider, cache) : provider;

    public static IFileToTextProvider Wrap(IFileToTextProvider provider, ServiceCacheType type, string? folder, List<string> warnings)
        => Create(type, folder, "filetotext", warnings) is { } cache ? new CachingFileToTextProvider(provider, cache) : provider;

    public static ITranslationProvider Wrap(ITranslationProvider provider, ServiceCacheType type, string? folder, List<string> warnings)
        => Create(type, folder, "translation", warnings) is { } cache ? new CachingTranslationProvider(provider, cache) : provider;

    /// <summary>
    /// The cache a service's settings ask for: none, one in memory, or a Native KV file in
    /// <paramref name="folder"/> (in memory when there is no folder). Null, with why in
    /// <paramref name="warnings"/>, when the file cannot be opened.
    /// </summary>
    public static IServiceAnswerCache? Create(ServiceCacheType type, string? folder, string service, List<string> warnings) {
        switch (type) {
            case ServiceCacheType.None:
                return null;
            case ServiceCacheType.Memory:
                return new MemoryServiceAnswerCache();
            case ServiceCacheType.Native:
                var path = FilePath(folder, service);
                try {
                    if (path != null) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    return new NativeKvServiceAnswerCache(path);
                } catch (Exception error) {
                    warnings.Add($"The {service} answer cache at {path} could not be opened, so the service is asked every time: {error.Message}");
                    return null;
                }
            default:
                throw new NotSupportedException($"There is no {service} cache of the type {type}. ");
        }
    }

    /// <summary>Where a service's cache file is kept in <paramref name="folder"/>; null without a folder.</summary>
    public static string? FilePath(string? folder, string service)
        => string.IsNullOrEmpty(folder) ? null : Path.Combine([folder, .. FileKeyUtility.GetServiceCacheFileKey(service)]);
}
