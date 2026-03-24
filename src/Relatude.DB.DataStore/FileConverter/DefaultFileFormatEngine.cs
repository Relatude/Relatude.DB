using Relatude.DB.IO;

namespace Relatude.DB.FileConverter;

internal class FileCacheGetResult(bool timedOut, bool ready) {
    public bool Ready { get; } = ready;
    public bool TimedOut { get; } = timedOut;  // in case conversion is done, but cache is not ready with writing to disk
    public Stream? Stream { get; set; } = null;
}
internal class FileCacheSetResult(bool completed) {
    public bool Completed { get; } = completed;
    public Stream? Stream { get; set; } = null;
}
internal class ConcurrentFileCache {
    readonly IIOProvider _io;
    public ConcurrentFileCache(IIOProvider io) { _io = io; }
    public Task<FileCacheGetResult> TryGetAsync(string key, int maxWaitMs) {
        throw new NotImplementedException();
    }
    public Task<FileCacheSetResult> SetAsync(string key, Stream? stream, int maxWaitMs) {
        throw new NotImplementedException();
    }
    public void Clear(string key) {
        throw new NotImplementedException();
    }
}
internal class ProgressEntry(
    DateTime created,
    FileConversionProgressInfo progressInfo,
    FileIdWithAdjustment adjustments,
    int maxWaitMs,
    string fileName,
    string hash
    ) {
    public DateTime Created { get; } = created;
    public FileConversionProgressInfo ProgressInfo { get; } = progressInfo;
    public FileIdWithAdjustment Adjustments { get; } = adjustments;
    public int MaxWaitMs { get; } = maxWaitMs;
    public string FileName { get; } = fileName;
    public string Hash { get; } = hash;
}
internal class FileFormatEngine : IDisposable {
    const int conversionProgressUpdateIntervalMs = 1000;
    readonly IFileConverter _fileConverter;
    readonly IUrlProvider _urlProvider;
    readonly IIOProvider _io;
    readonly FileKeyUtility _fileKeys;
    readonly Dictionary<string, ProgressEntry> _conversionsInProgressAtConverter;
    readonly ConcurrentFileCache _fileCache;
    private readonly CancellationTokenSource _cts = new();
    public FileFormatEngine(IFileConverter converter, IUrlProvider urlProvider, IIOProvider io, FileKeyUtility fileKeys) {
        _fileConverter = converter;
        _urlProvider = urlProvider;
        _io = io;
        _urlProvider = urlProvider;
        _fileKeys = fileKeys;
        _conversionsInProgressAtConverter = [];
        _fileCache = new ConcurrentFileCache(io);
    }
    public void Start() {
        _ = Task.Run(() => updateProgress(_cts.Token));
    }
    public void Stop() {
        _cts.Cancel();
    }
    async Task updateProgress(CancellationToken token) {
        Dictionary<string, ProgressEntry> snapshot;
        lock (_conversionsInProgressAtConverter) {
            snapshot = _conversionsInProgressAtConverter.ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        while (!token.IsCancellationRequested) {
            try {
                foreach (var kv in snapshot) {
                    var e = kv.Value;
                    var result = await TryGetFormatAsync(e.Adjustments, e.MaxWaitMs, e.FileName, e.Hash, () => throw new Exception("Unexpected call to getInputStream."));
                }
            } catch { }
        }
    }
    public void ClearAllCaches() {
        throw new NotImplementedException();
    }
    public void ClearFileCache(FileIdWithAdjustment fileIdWithAdjustment) {
        throw new NotImplementedException();
    }
    public string GetUrl(FileIdWithAdjustment fileIdWithAdjustment) {
        return _urlProvider.GetUrl(fileIdWithAdjustment);
    }

    public async Task<FileConversionResult> TryGetFormatAsync(FileIdWithAdjustment adjustments, int maxWaitMs, string fileName, string hash, Func<Stream> getInputStream) {

        var cacheKey = adjustments.GetKey();

        // check cache first:
        var cacheResult = await _fileCache.TryGetAsync(cacheKey, maxWaitMs); // check cache, leave some time in case conversion is done, but file is writing to disk
        if (cacheResult.Ready) return new(new(FileConversionStatus.Ready), cacheResult.Stream); // all good, file is ready in cache
        if (cacheResult.TimedOut) return new(new(FileConversionStatus.InProgress)); // in case conversion is done, but cache is not ready with writing to disk

        // not in cache, check if conversion is in progress at converter:
        lock (_conversionsInProgressAtConverter) {
            if (_conversionsInProgressAtConverter.TryGetValue(cacheKey, out var progressInfo))
                return new FileConversionResult(progressInfo.ProgressInfo);
            var newProgressInfo = new FileConversionProgressInfo(FileConversionStatus.InProgress);
            _conversionsInProgressAtConverter[cacheKey] = newProgressInfo;
        }

        // not in cache, nor converter; start conversion:
        try {
            using var inputStream = getInputStream();
            var result = await _fileConverter.ConvertAsync(inputStream, adjustments, hash, fileName, maxWaitMs);
            switch (result.ProgressInfo.Status) {
                case FileConversionStatus.InProgress: {
                        lock (_conversionsInProgressAtConverter) {
                            _conversionsInProgressAtConverter[cacheKey] = result.ProgressInfo;
                        }
                        return new(result.ProgressInfo); // in progress, waiting for conversion
                    }
                case FileConversionStatus.Ready: {
                        lock (_conversionsInProgressAtConverter) {
                            if (_conversionsInProgressAtConverter.ContainsKey(cacheKey)) {
                                _conversionsInProgressAtConverter.Remove(cacheKey);
                            }
                        }
                        var setResult = await _fileCache.SetAsync(cacheKey, result.Output, maxWaitMs);
                        if (setResult.Completed) {
                            return new(new(FileConversionStatus.Ready), setResult.Stream); // all good, file is ready
                        } else {
                            return new(new(FileConversionStatus.InProgress)); // in progress, waiting for cache writing
                        }
                    }
                case FileConversionStatus.Error: return new(new(FileConversionStatus.Error, message: result.ProgressInfo.Message));
                default: throw new NotImplementedException("Unknown FileConversionStatus: " + result.ProgressInfo.Status);
            }
        } finally {
            lock (_conversionsInProgressAtConverter) {
                _conversionsInProgressAtConverter.Remove(cacheKey);
            }
        }

    }
    public void Dispose() {
        _cts.Cancel();
    }
}
