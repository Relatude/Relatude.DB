using Relatude.DB.Common;
using Relatude.DB.IO;
using Relatude.DB.Web;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.FileConverter;

internal enum FileCacheGetStatus {
    BeingWrittenTo,
    Ready,
    NotExisting
}
internal class FileCacheGetResult(FileCacheGetStatus status, Stream? stream) {
    public FileCacheGetStatus Status { get; } = status;
    public Stream? Stream { get; } = stream;
}
internal class FileCacheSetResult(bool completed, Stream? stream) {
    public bool Completed { get; } = completed;
    public Stream? Stream { get; } = stream;
}
internal class AsyncConcurrentFileCache {
    // Special file cache that ensures that a file are not written by multiple threads and read at the same time
    // It has functionality for waiting for a file to be written by another thread, but with a timeout, to avoid waiting to long so that a " in progress status" can be returned to the caller
    readonly IIOProvider _io;
    readonly Dictionary<string, SemaphoreSlim> _locks = new();
    public AsyncConcurrentFileCache(IIOProvider io) { _io = io; }
    public async Task<FileCacheGetResult> TryGetStreamAsync(FileIdWithAdjustment adj, int maxWaitMs) {
        SemaphoreSlim? writeLock = null;
        lock (_locks) {
            _locks.TryGetValue(adj.GetKey(), out writeLock);
        }
        if (writeLock != null) { // file is locked, but wait for a bit
            if (!await writeLock.WaitAsync(maxWaitMs)) { // timed out, return with not ready flag
                return new(FileCacheGetStatus.BeingWrittenTo, stream: null);
            }
        }
        if (_io.DoesNotExistsOrIsEmpty(adj.GetFilePath())) return new(FileCacheGetStatus.NotExisting, stream: null);
        var stream = _io.OpenRead(adj.GetFilePath(), 0)?.AsStream();
        return new(FileCacheGetStatus.Ready, stream: stream);
    }
    public async Task<FileCacheSetResult> SetFromStreamAsync(FileIdWithAdjustment fileKey, Stream input, int maxWaitMs) {
        SemaphoreSlim? existingLock = null;
        lock (_locks) {
            if (!_locks.TryGetValue(fileKey.GetKey(), out existingLock)) {
                var newLock = new SemaphoreSlim(1, 1);
                newLock.Wait(); // set in locked state
                _locks[fileKey.GetKey()] = newLock;
            }
        }
        if (existingLock != null) {
            // should not happen often, means fileKey is already being written to by other thread
            // Format Engine, should already be avoiding this
            var acquired = await existingLock.WaitAsync(maxWaitMs);
            if (!acquired) throw new Exception("Unable to acquire lock for file");
        }
        IAppendStream? output = null;
        bool completedWithInMaxWait = true;
        byte[]? buffer = null;
        try {
            output = _io.OpenAppend(fileKey.GetFilePath());
            var bufferLength = 1024 * 1024;
            bufferLength = input.Length > bufferLength ? bufferLength : (int)input.Length;
            buffer = new byte[bufferLength]; // could pool, for later optimization
            var sw = Stopwatch.StartNew();
            while (true) {
                var read = await input.ReadAsync(buffer, 0, buffer.Length);
                await output.AppendAsyncNoChecksumOrLock(buffer, read);
                if (read == 0) {
                    completedWithInMaxWait = true;
                    break;
                }
                if (sw.ElapsedMilliseconds > maxWaitMs) {
                    completedWithInMaxWait = false;
                    break;
                }
            }
        } catch {
            releaseLockAndCloseStreams(fileKey, output, input);
            throw;
        }
        if (completedWithInMaxWait) {
            releaseLockAndCloseStreams(fileKey, output, input);
            var finalStream = _io.OpenRead(fileKey.GetFilePath(), 0).AsStream();
            return new FileCacheSetResult(true, finalStream); // all done, file is ready
        } else {
            // continue writing in background thread, synchronously 
            ThreadPool.QueueUserWorkItem(_ => {
                try {
                    while (true) {
                        var read = input.Read(buffer, 0, buffer.Length);
                        output.Append(buffer, read);
                        if (read == 0) break;
                    }
                } finally {
                    releaseLockAndCloseStreams(fileKey, output, input);
                }
            });
            return new FileCacheSetResult(false, null); // not done yet, but writing is in progress, and lock will be released when done, allowing readers to wait for completion
        }
    }
    void releaseLockAndCloseStreams(FileIdWithAdjustment key, IAppendStream? output, Stream input) {
        if (output != null) output.Dispose();
        lock (_locks) {
            if (_locks.TryGetValue(key.GetKey(), out var lockSlim)) {
                lockSlim.Release();
                lockSlim.Dispose();
                _locks.Remove(key.GetKey());
            }
        }
    }
    public void Clear(FileIdWithAdjustment key) {
        throw new NotImplementedException();
    }
}
internal class ProgressEntry(
    DateTime created,
    FileConversionProgressInfo progressInfo,
    FileConversionInfo fileInfo,
    int maxWaitMs
    ) {
    public DateTime Created { get; } = created;
    public FileConversionProgressInfo ProgressInfo { get; } = progressInfo;
    public FileConversionInfo FileInfo { get; } = fileInfo;
    public int MaxWaitMs { get; } = maxWaitMs;
}
public class ImageMetaResult(FileConversionResult result, ImageMeta? meta) {
    public FileConversionProgressInfo ProgressInfo { get; } = result.ProgressInfo;
    public ImageMeta? Meta { get; } = meta;
}
public class FileConversionInfo(FileIdWithAdjustment idWithAdjustment, string fileName, string hash, FileFormat format) {
    public FileIdWithAdjustment IdWithAdjustment { get; } = idWithAdjustment;
    public string FileName { get; } = fileName;
    public string Hash { get; } = hash;
    public FileFormat FromFormat { get; } = format;
    public FormatPair Formats { get; } = new FormatPair(format, idWithAdjustment.Adjustment.RequestedFormat);
}
internal class FileConversionLibrary {
    private readonly IFileConverter[] _converters;
    private readonly Dictionary<FormatPair, IFileConverter?> _lookUp; // from, to
    public FileConversionLibrary(IFileConverter[] converters) {
        _converters = converters;
        _lookUp = new();
    }
    public bool TryGetConverter(FormatPair key, [MaybeNullWhen(false)] out IFileConverter converter) {
        lock (_lookUp) {
            if (_lookUp.TryGetValue(key, out var match)) {
                converter = match;
                return match != null;
            }
            converter = null;
            foreach (var c in _converters) {
                // pick first match:
                var fromBase = FileFormatUtil.GetBaseFormatFromDetailedFormat(key.From);
                var toBase = FileFormatUtil.GetBaseFormatFromDetailedFormat(key.To);
                if (c.SupportsConversion(fromBase, key.From, toBase, key.To)) {
                    converter = c;
                    break;
                }
            }
            _lookUp[key] = converter;
            return converter != null;
        }
    }
}
public class FileConversionEngine : IDisposable {
    // This Engine is designed to help keep track of longer running conversions
    // and to help web requests respond either with a progress image or a ready image
    // You can specify a max time limit for how long you want to wait for a conversion to complete
    // For most image adjustments, the conversion should be pretty fast, but for some adjustments
    // like ai or video conversions, it can take a long time and it is better to return a in progress status
    // Using async is critical when holding requests like this to avoid blocking threads
    const int conversionProgressUpdateIntervalMs = 1000;
    readonly FileConversionLibrary _fileConverters;
    readonly IUrlProvider _urlProvider;
    readonly Dictionary<string, ProgressEntry> _conversionsInProgress;
    readonly AsyncConcurrentFileCache _fileCache;
    private readonly CancellationTokenSource _updateLookCancellationToken = new();
    public FileConversionEngine(IFileConverter[] converters, IUrlProvider urlProvider, IIOProvider io, FileKeyUtility fileKeys) {
        _fileConverters = new(converters);
        _urlProvider = urlProvider;
        _conversionsInProgress = [];
        _fileCache = new AsyncConcurrentFileCache(io);
    }
    public void Start() {
        _ = Task.Run(() => updateLoop(_updateLookCancellationToken.Token));
    }
    public void Stop() {
        _updateLookCancellationToken.Cancel();
    }
    async Task updateLoop(CancellationToken token) {
        while (!token.IsCancellationRequested) {
            await oneUpdate(token);
            await Task.Delay(conversionProgressUpdateIntervalMs, token);
        }
    }
    async Task oneUpdate(CancellationToken token) {
        Dictionary<string, ProgressEntry> snapshot;
        lock (_conversionsInProgress) {
            if (_conversionsInProgress.Count == 0) return;
            snapshot = _conversionsInProgress.ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        foreach (var kv in snapshot) {
            if (token.IsCancellationRequested) return;
            try {
                var e = kv.Value;
                var result = await TryGetFormatAsync(e.FileInfo, e.MaxWaitMs, () => throw new Exception("Unexpected call to getInputStream."));
            } catch { }
        }
    }
    public async Task<int> ClearAllCaches() {
        throw new NotImplementedException();
    }
    public void ClearFileCache(FileIdWithAdjustment fileIdWithAdjustment) {
        throw new NotImplementedException();
    }
    public string GetUrl(FileIdWithAdjustment fileIdWithAdjustment) {
        return _urlProvider.GetUrl(fileIdWithAdjustment);
    }
    public async Task<FileConversionResult> TryGetFormatAsync(FileConversionInfo info, int maxWaitMs, Func<Stream> getInputStream) {

        if (!_fileConverters.TryGetConverter(info.Formats, out var converter)) {
            return new(new(FileConversionStatus.Unsupported, message: "No converter available for " + info.Formats.From + " to " + info.Formats.To + ". "));
        }

        var key = info.IdWithAdjustment.GetKey();

        // check file cache first:
        var cacheResult = await _fileCache.TryGetStreamAsync(info.IdWithAdjustment, maxWaitMs); // check cache, leave some time in case conversion is done, but file is writing to disk
        switch (cacheResult.Status) {
            case FileCacheGetStatus.Ready: return new(new(FileConversionStatus.Ready), cacheResult.Stream); // all good, file is ready in cache
            case FileCacheGetStatus.BeingWrittenTo: return new(new(FileConversionStatus.InProgress)); // in case conversion is done, but cache is not ready with writing to disk
            case FileCacheGetStatus.NotExisting: break; // not in cache, so continue
            default: throw new NotImplementedException("Unknown FileCacheGetStatus: " + cacheResult.Status);
        }

        // not in cache, check if conversion is in progress at converter:
        lock (_conversionsInProgress) {
            if (_conversionsInProgress.TryGetValue(key, out var progressInfo))
                return new FileConversionResult(progressInfo.ProgressInfo);
            var newProgressInfo = new FileConversionProgressInfo(FileConversionStatus.InProgress);
            _conversionsInProgress[key] = new ProgressEntry(DateTime.UtcNow, newProgressInfo, info, maxWaitMs);
        }

        // not in cache, nor converter; start conversion:
        FileConversionResult? result = null;
        try {
            var inputStream = getInputStream();
            result = await converter.ConvertAsync(inputStream, info, maxWaitMs);
        } catch (Exception error) {
            if (result != null && result.Output != null) result.Output.Dispose();
            lock (_conversionsInProgress) {
                _conversionsInProgress.Remove(key);
            }
            return new(new(FileConversionStatus.Error, message: error.Message));
        }
        switch (result.ProgressInfo.Status) {
            case FileConversionStatus.InProgress: { // update progress info
                    lock (_conversionsInProgress) {
                        var created = _conversionsInProgress[key].Created; // keep original creation time for progress tracking
                        _conversionsInProgress[key] = new ProgressEntry(created, result.ProgressInfo, info, maxWaitMs);
                    }
                    return new(result.ProgressInfo); // not able to complete in maxWaitMs, but still in progress so return without file but with progress info update
                }
            case FileConversionStatus.Ready: { // save result
                    FileCacheSetResult? setResult;
                    try {
                        if (result.Output == null) throw new Exception("Empty output of conversion");
                        setResult = await _fileCache.SetFromStreamAsync(info.IdWithAdjustment, result.Output, maxWaitMs);
                    } catch (Exception error) {
                        lock (_conversionsInProgress) {
                            _conversionsInProgress.Remove(key);
                        }
                        if (result.Output != null) result.Output.Dispose();
                        return new(new(FileConversionStatus.Error, message: "Failed to save result. " + error.Message));
                    }
                    if (setResult.Completed) {
                        lock (_conversionsInProgress) {
                            _conversionsInProgress.Remove(key);
                        }
                        return new(new(FileConversionStatus.Ready), setResult.Stream); // all good, file is ready
                    } else {
                        return new(new(FileConversionStatus.InProgress)); // in progress, waiting for cache writing
                    }
                }
            case FileConversionStatus.Unsupported:
                lock (_conversionsInProgress) {
                    _conversionsInProgress.Remove(key);
                }
                return new(new(FileConversionStatus.Unsupported, message: result.ProgressInfo.Message));
            case FileConversionStatus.Error:
                lock (_conversionsInProgress) {
                    _conversionsInProgress.Remove(key);
                }
                return new(new(FileConversionStatus.Error, message: result.ProgressInfo.Message));
            default: throw new NotImplementedException("Unknown FileConversionStatus: " + result.ProgressInfo.Status);
        }

    }
    public void Dispose() {
        _updateLookCancellationToken.Cancel();
    }
}

public static class FileConversionEngineExt {
    public static async Task<ImageMetaResult> TryGetImageMetaAsync(this FileConversionEngine eng, FileConversionInfo info, int maxWaitMs, Func<Stream> getInputStream) {
        var result = await eng.TryGetFormatAsync(info, maxWaitMs, getInputStream);
        if (result.ProgressInfo.Status == FileConversionStatus.Ready) {
            try {
                if (result.Output == null) throw new Exception("Empty output of conversion");
                var meta = ImageMeta.FromStream(result.Output);
                result.Output.Dispose();
                return new(result, meta);
            } catch (Exception error) {
                var errorMsg = "Failed to read image meta from stream. " + error.Message;
                return new(new(new(FileConversionStatus.Error, message: errorMsg)), null);
            }
        } else {
            return new(result, null);
        }
    }
}