//using Relatude.DB.Common;
//using Relatude.DB.IO;
//using Relatude.DB.Web;
//using System.Diagnostics;
//using System.Diagnostics.CodeAnalysis;
//using static System.Runtime.InteropServices.JavaScript.JSType;

//namespace Relatude.DB.FileConverter;

//internal enum FileCacheGetStatus {
//    BeingWrittenTo,
//    Ready,
//    NotExisting
//}
//internal class FileCacheGetResult(FileCacheGetStatus status, Stream? stream) {
//    public FileCacheGetStatus Status { get; } = status;
//    public Stream? Stream { get; } = stream;
//}
//internal class FileCacheSetResult(bool completed, Stream? stream) {
//    public bool Completed { get; } = completed;
//    public Stream? Stream { get; } = stream;
//}
//internal class AsyncConcurrentFileCache {
//    // Special file cache that ensures that a file are not written by multiple threads and read at the same time
//    // It has functionality for waiting for a file to be written by another thread, but with a timeout, to avoid waiting to long so that a " in progress status" can be returned to the caller
//    readonly IIOProvider _io;
//    readonly Cache<Guid, byte[]> _smallFileCache = new(1024 * 1024 * 50); // 50mb
//    readonly int _sizeLimitSmallCache = 300 * 1024; // 300kb, files smaller than this will be kept in memory for faster access
//    readonly Dictionary<Guid, SemaphoreSlim> _locks = new();
//    public AsyncConcurrentFileCache(IIOProvider io) { _io = io; }
//    const int _folderDepth = 2;
//    const string baseFolder = "filecache";
//    string[] getFilePath(FileIdWithAdjustment adj) {
//        var key = adj.GetKey().ToString();
//        var path = new string[_folderDepth + 1];
//        path[0] = baseFolder;
//        for (int i = 0; i < _folderDepth - 1; i++) path[i + 1] = key.Substring(i * 2, 2);
//        path[_folderDepth] = key;
//        return path;
//    }
//    public async Task<FileCacheGetResult> TryGetStreamAsync(FileIdWithAdjustment adj, int maxWaitMs) {
//        var key = adj.GetKey();
//        lock (_smallFileCache) {
//            if (_smallFileCache.TryGet(key, out var data)) {
//                return new(FileCacheGetStatus.Ready, stream: new MemoryStream(data));
//            }
//        }
//        SemaphoreSlim? writeLock = null;
//        lock (_locks) {
//            _locks.TryGetValue(key, out writeLock);
//        }
//        if (writeLock != null) { // file is locked, but wait for a bit
//            if (!await writeLock.WaitAsync(maxWaitMs)) { // timed out, return with not ready flag
//                return new(FileCacheGetStatus.BeingWrittenTo, stream: null);
//            }
//        }
//        if (_io.DoesNotExistsOrIsEmpty(getFilePath(adj))) return new(FileCacheGetStatus.NotExisting, stream: null);
//        var stream = _io.OpenRead(getFilePath(adj), 0)?.AsStream();
//        if (stream != null && stream.Length <= _sizeLimitSmallCache) {
//            var buffer = new byte[stream.Length];
//            stream.Read(buffer, 0, buffer.Length);
//            stream.Close();
//            lock (_smallFileCache) {
//                _smallFileCache.Set(key, buffer, buffer.Length);
//            }
//            return new(FileCacheGetStatus.Ready, stream: new MemoryStream(buffer));
//        }
//        return new(FileCacheGetStatus.Ready, stream: stream);
//    }
//    public async Task<FileCacheSetResult> SetFromStreamAsync(FileIdWithAdjustment fileKey, Stream input, int maxWaitMs) {
//        SemaphoreSlim? existingLock = null;
//        lock (_locks) {
//            if (!_locks.TryGetValue(fileKey.GetKey(), out existingLock)) {
//                var newLock = new SemaphoreSlim(1, 1);
//                newLock.Wait(); // set in locked state
//                _locks[fileKey.GetKey()] = newLock;
//            }
//        }
//        if (existingLock != null) {
//            // should not happen often, means fileKey is already being written to by other thread
//            // Format Engine, should already be avoiding this
//            var acquired = await existingLock.WaitAsync(maxWaitMs);
//            if (!acquired) throw new Exception("Unable to acquire lock for file");
//        }
//        IAppendStream? output = null;
//        bool completedWithInMaxWait = true;
//        byte[]? buffer = null;
//        try {
//            output = _io.OpenAppend(getFilePath(fileKey));
//            var bufferLength = 1024 * 1024;
//            bufferLength = input.Length > bufferLength ? bufferLength : (int)input.Length;
//            buffer = new byte[bufferLength]; // could pool, for later optimization
//            var sw = Stopwatch.StartNew();
//            while (true) {
//                var read = await input.ReadAsync(buffer, 0, buffer.Length);
//                await output.AppendAsyncNoChecksumOrLock(buffer, read);
//                if (read == 0) {
//                    completedWithInMaxWait = true;
//                    break;
//                }
//                if (sw.ElapsedMilliseconds > maxWaitMs) {
//                    completedWithInMaxWait = false;
//                    break;
//                }
//            }
//        } catch {
//            releaseLockAndCloseStreams(fileKey, output, input);
//            throw;
//        }
//        if (completedWithInMaxWait) {
//            releaseLockAndCloseStreams(fileKey, output, input);
//            var finalStream = _io.OpenRead(getFilePath(fileKey), 0).AsStream();
//            return new FileCacheSetResult(true, finalStream); // all done, file is ready
//        } else {
//            // continue writing in background thread, synchronously 
//            ThreadPool.QueueUserWorkItem(_ => {
//                try {
//                    while (true) {
//                        var read = input.Read(buffer, 0, buffer.Length);
//                        output.Append(buffer, read);
//                        if (read == 0) break;
//                    }
//                } finally {
//                    releaseLockAndCloseStreams(fileKey, output, input);
//                }
//            });
//            return new FileCacheSetResult(false, null); // not done yet, but writing is in progress, and lock will be released when done, allowing readers to wait for completion
//        }
//    }
//    void releaseLockAndCloseStreams(FileIdWithAdjustment key, IAppendStream? output, Stream input) {
//        if (output != null) output.Dispose();
//        lock (_locks) {
//            if (_locks.TryGetValue(key.GetKey(), out var lockSlim)) {
//                lockSlim.Release();
//                lockSlim.Dispose();
//                _locks.Remove(key.GetKey());
//            }
//        }
//    }
//    public void Clear(FileIdWithAdjustment key) {
//        throw new NotImplementedException();
//    }
//}
//internal class ProgressEntry(
//    DateTime created,
//    FileConversionProgressInfo progressInfo,
//    FileConversionInfo fileInfo
//    ) {
//    public DateTime Created { get; } = created;
//    public FileConversionProgressInfo ProgressInfo { get; } = progressInfo;
//    public FileConversionInfo FileInfo { get; } = fileInfo;
//}
//public class ImageMetaResult(FileConversionResult result, ImageMeta? meta) {
//    public FileConversionProgressInfo ProgressInfo { get; } = result.ProgressInfo;
//    public ImageMeta? Meta { get; } = meta;
//}
//public class FileConversionInfo(FileIdWithAdjustment idWithAdjustment, string fileName, string hash, FileFormat format) {
//    public FileIdWithAdjustment IdWithAdjustment { get; } = idWithAdjustment;
//    public string FileName { get; } = fileName;
//    public string Hash { get; } = hash;
//    public FileFormat FromFormat { get; } = format;
//    public FormatPair Formats { get; } = new FormatPair(format, idWithAdjustment.Adjustment.RequestedFormat);
//}
//internal class FileConversionLibrary {
//    private readonly IFileConverter[] _converters;
//    private readonly Dictionary<FormatPair, IFileConverter?> _lookUp; // from, to
//    public FileConversionLibrary(IFileConverter[] converters) {
//        _converters = converters;
//        _lookUp = new();
//    }
//    public bool TryGetConverter(FormatPair key, [MaybeNullWhen(false)] out IFileConverter converter) {
//        lock (_lookUp) {
//            if (_lookUp.TryGetValue(key, out var match)) {
//                converter = match;
//                return match != null;
//            }
//            converter = null;
//            foreach (var c in _converters) {
//                // pick first match:
//                var fromBase = FileFormatUtil.GetBaseFormatFromDetailedFormat(key.From);
//                var toBase = FileFormatUtil.GetBaseFormatFromDetailedFormat(key.To);
//                if (c.SupportsConversion(fromBase, key.From, toBase, key.To)) {
//                    converter = c;
//                    break;
//                }
//            }
//            _lookUp[key] = converter;
//            return converter != null;
//        }
//    }
//}
//public class FileConversionEngine : IDisposable {
//    // This Engine is designed to help keep track of longer running conversions
//    // and to help web requests respond either with a progress image or a ready image
//    // You can specify a max time limit for how long you want to wait for a conversion to complete
//    // For most image adjustments, the conversion should be pretty fast, but for some adjustments
//    // like ai or video conversions, it can take a long time and it is better to return a in progress status
//    // Using async is critical when holding requests like this to avoid blocking threads
//    const int conversionProgressUpdateIntervalMs = 1000;
//    readonly FileConversionLibrary _fileConverters;
//    readonly Dictionary<Guid, ProgressEntry> _conversionsInProgress;
//    readonly AsyncConcurrentFileCache _fileCache;
//    private readonly CancellationTokenSource _updateLookCancellationToken = new();

//    public FileConversionEngine(IFileConverter[] converters, IIOProvider io, FileKeyUtility fileKeys) {
//        _fileConverters = new(converters);
//        _conversionsInProgress = [];
//        _fileCache = new AsyncConcurrentFileCache(io);
//    }
//    public void Start() {
//        _ = Task.Run(() => updateLoop(_updateLookCancellationToken.Token));
//    }
//    public void Stop() {
//        _updateLookCancellationToken.Cancel();
//    }

//    async Task updateLoop(CancellationToken token) {
//        while (!token.IsCancellationRequested) {
//            await oneUpdate(token);
//            await Task.Delay(conversionProgressUpdateIntervalMs, token);
//        }
//    }
//    async Task oneUpdate(CancellationToken token) {
//        Dictionary<Guid, ProgressEntry> snapshot;
//        lock (_conversionsInProgress) {
//            if (_conversionsInProgress.Count == 0) return;
//            var maxAge = DateTime.UtcNow.AddSeconds(60);
//            var keysToRemove = _conversionsInProgress.Where(kvp =>
//                kvp.Value.Created < maxAge
//                && kvp.Value.ProgressInfo.Status != FileConversionStatus.InProgress
//            ).Select(kvp => kvp.Key).ToList();
//            foreach (var key in keysToRemove) _conversionsInProgress.Remove(key);
//        }
//    }
//    public async Task<int> ClearAllCaches() {
//        throw new NotImplementedException();
//    }
//    public void ClearFileCache(FileIdWithAdjustment fileIdWithAdjustment) {
//        throw new NotImplementedException();
//    }
//    async Task<FileConversionResult> waitForExistingConversion(Guid key, int maxWait) {
//        var sw = Stopwatch.StartNew();
//        while (true) {
//            var nextWait = maxWait - (int)sw.ElapsedMilliseconds;
//            bool found = false;
//            ProgressEntry? entry = null;
//            lock (_conversionsInProgress) {
//                found = _conversionsInProgress.TryGetValue(key, out entry);
//            }
//            if (!found || entry == null) {
//                // give up, means it was running but no status was found, so return in progress, that will trigger a new conversion if called again
//                return new(new(FileConversionStatus.InProgress));
//            } else if (entry.ProgressInfo.Status == FileConversionStatus.Ready) {
//                // conversion is done and should be in cache, so check cache for file:
//                var cacheResult = await _fileCache.TryGetStreamAsync(entry.FileInfo.IdWithAdjustment, nextWait);
//                return cacheResult.Status switch {
//                    FileCacheGetStatus.Ready =>
//                        // all good, file is ready in cache
//                        new(new(FileConversionStatus.Ready), cacheResult.Stream),
//                    FileCacheGetStatus.BeingWrittenTo =>
//                        // in case conversion is done, should not happen, return in progress that eventually will trigger new conversion if called again
//                        new(new(FileConversionStatus.InProgress)),
//                    FileCacheGetStatus.NotExisting =>
//                        // not in cache, should not happen, return in progress that eventually will trigger new conversion if called again
//                        new(new(FileConversionStatus.InProgress)),
//                    _ => throw new NotImplementedException("Unknown FileCacheGetStatus: " + cacheResult.Status),
//                };
//            } else if (entry.ProgressInfo.Status == FileConversionStatus.InProgress) {
//                return new(new(FileConversionStatus.InProgress));
//            } else if (entry.ProgressInfo.Status == FileConversionStatus.Error || entry.ProgressInfo.Status == FileConversionStatus.Unsupported) {
//                // conversion failed, return that info:
//                return new(new(entry.ProgressInfo.Status, message: entry.ProgressInfo.Message));
//            }
//            await Task.Delay(100); // wait a bit before checking again, to avoid tight loop
//        }
//    }

//    public Task<FileConversionResult> TryGetFormatAsync(FileConversionInfo info, int maxWaitMs, Func<Task<Stream>> getInputStream) {

//        // first direct path, just to check cache
//        var first = tryGetFormatAsync(info, 0, getInputStream).Result;
//        if (first.ProgressInfo.Status == FileConversionStatus.Ready)
//            return Task.FromResult(first);

//        // if not ready, start the conversion in background and return a task that will complete when conversion is done or max wait time is reached:
//        var tcs = new TaskCompletionSource<FileConversionResult>();
//        ThreadPool.QueueUserWorkItem(async _ => {
//            try { tcs.SetResult(await tryGetFormatAsync(info, maxWaitMs, getInputStream)); } catch (Exception ex) { tcs.SetException(ex); }
//        });
//        return Task.WhenAny(tcs.Task, Task.Delay(maxWaitMs).ContinueWith(_ => new FileConversionResult(new(FileConversionStatus.InProgress))))
//            .Unwrap();

//    }

//    async Task<FileConversionResult> tryGetFormatAsync(FileConversionInfo info, int initialMaxWaitMs, Func<Task<Stream>> getInputStream) {

//        var key = info.IdWithAdjustment.GetKey();

//        if (!_fileConverters.TryGetConverter(info.Formats, out var converter)) {
//            return new(new(FileConversionStatus.Unsupported, message: "No converter available for " + info.Formats.From + " to " + info.Formats.To + ". "));
//        }

//        var stopWatch = Stopwatch.StartNew();
//        var remainingWait = initialMaxWaitMs;

//        // check file cache first, and wait if it is already being written from converter to cache:
//        var cacheResult = await _fileCache.TryGetStreamAsync(info.IdWithAdjustment, remainingWait); // check cache, leave some time in case conversion is done, but file is writing to disk
//        switch (cacheResult.Status) {
//            case FileCacheGetStatus.Ready: return new(new(FileConversionStatus.Ready), cacheResult.Stream); // all good, file is ready in cache
//            case FileCacheGetStatus.BeingWrittenTo: return new(new(FileConversionStatus.InProgress)); // in case conversion is done, but cache is not ready with writing to disk
//            case FileCacheGetStatus.NotExisting: break; // not in cache, so continue
//            default: throw new NotImplementedException("Unknown FileCacheGetStatus: " + cacheResult.Status);
//        }

//        remainingWait = initialMaxWaitMs - (int)stopWatch.ElapsedMilliseconds;
//        if (remainingWait <= 0) return new(new(FileConversionStatus.InProgress)); // not in cache, but also no time left to wait, so return with in progress status

//        // so, not in cache, start conversion if not already in progress
//        bool conversionAlreadyInProgress = false;
//        lock (_conversionsInProgress) {
//            if (_conversionsInProgress.TryGetValue(key, out var entry)) {
//                conversionAlreadyInProgress = true;
//            } else {
//                var newProgressInfo = new FileConversionProgressInfo(FileConversionStatus.InProgress);
//                _conversionsInProgress[key] = new(DateTime.UtcNow, newProgressInfo, info);
//            }
//        }
//        if (conversionAlreadyInProgress) {
//            return await waitForExistingConversion(key, remainingWait);
//        }

//        // not in cache, no converter so start conversion:
//        FileConversionResult? result = null;
//        try {
//            var inputStream = await getInputStream();
//            remainingWait = initialMaxWaitMs - (int)stopWatch.ElapsedMilliseconds;
//            result = await converter.ConvertAsync(inputStream, info, remainingWait);
//        } catch (Exception error) {
//            if (result != null && result.Output != null) result.Output.Dispose();
//            lock (_conversionsInProgress) {
//                _conversionsInProgress[key] = new(DateTime.UtcNow, new(FileConversionStatus.Error, message: "Conversion failed. " + error.Message), info);
//            }
//            return new(new(FileConversionStatus.Error, message: error.Message));
//        }
//        switch (result.ProgressInfo.Status) {
//            case FileConversionStatus.InProgress: { // update progress info
//                    lock (_conversionsInProgress) {
//                        var created = _conversionsInProgress[key].Created; // keep original creation time for progress tracking
//                        _conversionsInProgress[key] = new(created, result.ProgressInfo, info);
//                    }
//                    return new(result.ProgressInfo); // not able to complete in maxWaitMs, but still in progress so return without file but with progress info update
//                }
//            case FileConversionStatus.Ready: { // save result
//                    FileCacheSetResult? setResult;
//                    try {
//                        if (result.Output == null) throw new Exception("Empty output of conversion");
//                        setResult = await _fileCache.SetFromStreamAsync(info.IdWithAdjustment, result.Output, remainingWait);
//                    } catch (Exception error) {
//                        lock (_conversionsInProgress) {
//                            _conversionsInProgress[key] = new(DateTime.UtcNow, new(FileConversionStatus.Error, message: "Failed to save result. " + error.Message), info);
//                        }
//                        if (result.Output != null) result.Output.Dispose();
//                        return new(new(FileConversionStatus.Error, message: "Failed to save result. " + error.Message));
//                    }
//                    if (setResult.Completed) {
//                        lock (_conversionsInProgress) {
//                            _conversionsInProgress[key] = new(DateTime.UtcNow, new(FileConversionStatus.Ready), info);
//                        }
//                        return new(new(FileConversionStatus.Ready), setResult.Stream); // all good, file is ready
//                    } else {
//                        return new(new(FileConversionStatus.InProgress)); // in progress, waiting for cache writing
//                    }
//                }
//            case FileConversionStatus.Unsupported:
//                lock (_conversionsInProgress) {
//                    _conversionsInProgress[key] = new(DateTime.UtcNow, new(FileConversionStatus.Unsupported, message: result.ProgressInfo.Message), info);
//                }
//                return new(new(FileConversionStatus.Unsupported, message: result.ProgressInfo.Message));
//            case FileConversionStatus.Error:
//                lock (_conversionsInProgress) {
//                    _conversionsInProgress[key] = new(DateTime.UtcNow, new(FileConversionStatus.Error, message: result.ProgressInfo.Message), info);
//                }
//                return new(new(FileConversionStatus.Error, message: result.ProgressInfo.Message));
//            default: throw new NotImplementedException("Unknown FileConversionStatus: " + result.ProgressInfo.Status);
//        }

//    }
//    public void Dispose() {
//        _updateLookCancellationToken.Cancel();
//    }

//    public Stream GetProgressStream(FileValue fileValue, FileAdjustmentBase adj, FileConversionProgressInfo status) {
//        if (_fileConverters.TryGetConverter(new FormatPair(adj.RequestedFormat, adj.RequestedFormat), out var converter)) {
//            return converter.GetProgressStream(fileValue, adj, status);
//        } else {
//            throw new Exception("No converter available for " + adj.RequestedFormat);
//        }
//    }
//}

//public static class FileConversionEngineExt {
//    public static async Task<ImageMetaResult> TryGetImageMetaAsync(this FileConversionEngine eng, FileConversionInfo info, int maxWaitMs, Func<Task<Stream>> getInputStream) {
//        var result = await eng.TryGetFormatAsync(info, maxWaitMs, getInputStream);
//        if (result.ProgressInfo.Status == FileConversionStatus.Ready) {
//            try {
//                if (result.Output == null) throw new Exception("Empty output of conversion");
//                var meta = ImageMeta.FromStream(result.Output);
//                result.Output.Dispose();
//                return new(result, meta);
//            } catch (Exception error) {
//                var errorMsg = "Failed to read image meta from stream. " + error.Message;
//                return new(new(new(FileConversionStatus.Error, message: errorMsg)), null);
//            }
//        } else {
//            return new(result, null);
//        }
//    }
//}
//using Relatude.DB.Common;
//using Relatude.DB.IO;
//using Relatude.DB.Web;
//using System.Diagnostics;
//using System.Diagnostics.CodeAnalysis;
//using static System.Runtime.InteropServices.JavaScript.JSType;

//namespace Relatude.DB.FileConverter;

//internal enum FileCacheGetStatus {
//    BeingWrittenTo,
//    Ready,
//    NotExisting
//}
//internal class FileCacheGetResult(FileCacheGetStatus status, Stream? stream) {
//    public FileCacheGetStatus Status { get; } = status;
//    public Stream? Stream { get; } = stream;
//}
//internal class FileCacheSetResult(bool completed, Stream? stream) {
//    public bool Completed { get; } = completed;
//    public Stream? Stream { get; } = stream;
//}
//internal class AsyncConcurrentFileCache {
//    // Special file cache that ensures that a file are not written by multiple threads and read at the same time
//    // It has functionality for waiting for a file to be written by another thread, but with a timeout, to avoid waiting to long so that a " in progress status" can be returned to the caller
//    readonly IIOProvider _io;
//    readonly Cache<Guid, byte[]> _smallFileCache = new(1024 * 1024 * 50); // 50mb
//    readonly int _sizeLimitSmallCache = 300 * 1024; // 300kb, files smaller than this will be kept in memory for faster access
//    readonly Dictionary<Guid, SemaphoreSlim> _locks = new();
//    public AsyncConcurrentFileCache(IIOProvider io) { _io = io; }
//    const int _folderDepth = 2;
//    const string baseFolder = "filecache";
//    string[] getFilePath(FileIdWithAdjustment adj) {
//        var key = adj.GetKey().ToString();
//        var path = new string[_folderDepth + 1];
//        path[0] = baseFolder;
//        for (int i = 0; i < _folderDepth - 1; i++) path[i + 1] = key.Substring(i * 2, 2);
//        path[_folderDepth] = key;
//        return path;
//    }
//    public async Task<FileCacheGetResult> TryGetStreamAsync(FileIdWithAdjustment adj, int maxWaitMs) {
//        var key = adj.GetKey();
//        lock (_smallFileCache) {
//            if (_smallFileCache.TryGet(key, out var data)) {
//                return new(FileCacheGetStatus.Ready, stream: new MemoryStream(data));
//            }
//        }
//        SemaphoreSlim? writeLock = null;
//        lock (_locks) {
//            _locks.TryGetValue(key, out writeLock);
//        }
//        if (writeLock != null) { // file is locked, but wait for a bit
//            if (!await writeLock.WaitAsync(maxWaitMs)) { // timed out, return with not ready flag
//                return new(FileCacheGetStatus.BeingWrittenTo, stream: null);
//            }
//        }
//        if (_io.DoesNotExistsOrIsEmpty(getFilePath(adj))) return new(FileCacheGetStatus.NotExisting, stream: null);
//        var stream = _io.OpenRead(getFilePath(adj), 0)?.AsStream();
//        if (stream != null && stream.Length <= _sizeLimitSmallCache) {
//            var buffer = new byte[stream.Length];
//            stream.Read(buffer, 0, buffer.Length);
//            stream.Close();
//            lock (_smallFileCache) {
//                _smallFileCache.Set(key, buffer, buffer.Length);
//            }
//            return new(FileCacheGetStatus.Ready, stream: new MemoryStream(buffer));
//        }
//        return new(FileCacheGetStatus.Ready, stream: stream);
//    }
//    public async Task<FileCacheSetResult> SetFromStreamAsync(FileIdWithAdjustment fileKey, Stream input, int maxWaitMs) {
//        SemaphoreSlim? existingLock = null;
//        lock (_locks) {
//            if (!_locks.TryGetValue(fileKey.GetKey(), out existingLock)) {
//                var newLock = new SemaphoreSlim(1, 1);
//                newLock.Wait(); // set in locked state
//                _locks[fileKey.GetKey()] = newLock;
//            }
//        }
//        if (existingLock != null) {
//            // should not happen often, means fileKey is already being written to by other thread
//            // Format Engine, should already be avoiding this
//            var acquired = await existingLock.WaitAsync(maxWaitMs);
//            if (!acquired) throw new Exception("Unable to acquire lock for file");
//        }
//        IAppendStream? output = null;
//        bool completedWithInMaxWait = true;
//        byte[]? buffer = null;
//        try {
//            output = _io.OpenAppend(getFilePath(fileKey));
//            var bufferLength = 1024 * 1024;
//            bufferLength = input.Length > bufferLength ? bufferLength : (int)input.Length;
//            buffer = new byte[bufferLength]; // could pool, for later optimization
//            var sw = Stopwatch.StartNew();
//            while (true) {
//                var read = await input.ReadAsync(buffer, 0, buffer.Length);
//                await output.AppendAsyncNoChecksumOrLock(buffer, read);
//                if (read == 0) {
//                    completedWithInMaxWait = true;
//                    break;
//                }
//                if (sw.ElapsedMilliseconds > maxWaitMs) {
//                    completedWithInMaxWait = false;
//                    break;
//                }
//            }
//        } catch {
//            releaseLockAndCloseStreams(fileKey, output, input);
//            throw;
//        }
//        if (completedWithInMaxWait) {
//            releaseLockAndCloseStreams(fileKey, output, input);
//            var finalStream = _io.OpenRead(getFilePath(fileKey), 0).AsStream();
//            return new FileCacheSetResult(true, finalStream); // all done, file is ready
//        } else {
//            // continue writing in background thread, synchronously 
//            ThreadPool.QueueUserWorkItem(_ => {
//                try {
//                    while (true) {
//                        var read = input.Read(buffer, 0, buffer.Length);
//                        output.Append(buffer, read);
//                        if (read == 0) break;
//                    }
//                } finally {
//                    releaseLockAndCloseStreams(fileKey, output, input);
//                }
//            });
//            return new FileCacheSetResult(false, null); // not done yet, but writing is in progress, and lock will be released when done, allowing readers to wait for completion
//        }
//    }
//    void releaseLockAndCloseStreams(FileIdWithAdjustment key, IAppendStream? output, Stream input) {
//        if (output != null) output.Dispose();
//        lock (_locks) {
//            if (_locks.TryGetValue(key.GetKey(), out var lockSlim)) {
//                lockSlim.Release();
//                lockSlim.Dispose();
//                _locks.Remove(key.GetKey());
//            }
//        }
//    }
//    public void Clear(FileIdWithAdjustment key) {
//        throw new NotImplementedException();
//    }
//}
//internal class ProgressEntry(
//    DateTime created,
//    FileConversionProgressInfo progressInfo,
//    FileConversionInfo fileInfo
//    ) {
//    public DateTime Created { get; } = created;
//    public FileConversionProgressInfo ProgressInfo { get; } = progressInfo;
//    public FileConversionInfo FileInfo { get; } = fileInfo;
//}
//public class ImageMetaResult(FileConversionResult result, ImageMeta? meta) {
//    public FileConversionProgressInfo ProgressInfo { get; } = result.ProgressInfo;
//    public ImageMeta? Meta { get; } = meta;
//}
//public class FileConversionInfo(FileIdWithAdjustment idWithAdjustment, string fileName, string hash, FileFormat format) {
//    public FileIdWithAdjustment IdWithAdjustment { get; } = idWithAdjustment;
//    public string FileName { get; } = fileName;
//    public string Hash { get; } = hash;
//    public FileFormat FromFormat { get; } = format;
//    public FormatPair Formats { get; } = new FormatPair(format, idWithAdjustment.Adjustment.RequestedFormat);
//}
//internal class FileConversionLibrary {
//    private readonly IFileConverter[] _converters;
//    private readonly Dictionary<FormatPair, IFileConverter?> _lookUp; // from, to
//    public FileConversionLibrary(IFileConverter[] converters) {
//        _converters = converters;
//        _lookUp = new();
//    }
//    public bool TryGetConverter(FormatPair key, [MaybeNullWhen(false)] out IFileConverter converter) {
//        lock (_lookUp) {
//            if (_lookUp.TryGetValue(key, out var match)) {
//                converter = match;
//                return match != null;
//            }
//            converter = null;
//            foreach (var c in _converters) {
//                // pick first match:
//                var fromBase = FileFormatUtil.GetBaseFormatFromDetailedFormat(key.From);
//                var toBase = FileFormatUtil.GetBaseFormatFromDetailedFormat(key.To);
//                if (c.SupportsConversion(fromBase, key.From, toBase, key.To)) {
//                    converter = c;
//                    break;
//                }
//            }
//            _lookUp[key] = converter;
//            return converter != null;
//        }
//    }
//}
//public class FileConversionEngine : IDisposable {
//    // This Engine is designed to help keep track of longer running conversions
//    // and to help web requests respond either with a progress image or a ready image
//    // You can specify a max time limit for how long you want to wait for a conversion to complete
//    // For most image adjustments, the conversion should be pretty fast, but for some adjustments
//    // like ai or video conversions, it can take a long time and it is better to return a in progress status
//    // Using async is critical when holding requests like this to avoid blocking threads
//    const int conversionProgressUpdateIntervalMs = 1000;
//    readonly FileConversionLibrary _fileConverters;
//    readonly Dictionary<Guid, ProgressEntry> _conversionsInProgress;
//    readonly AsyncConcurrentFileCache _fileCache;
//    private readonly CancellationTokenSource _updateLookCancellationToken = new();

//    public FileConversionEngine(IFileConverter[] converters, IIOProvider io, FileKeyUtility fileKeys) {
//        _fileConverters = new(converters);
//        _conversionsInProgress = [];
//        _fileCache = new AsyncConcurrentFileCache(io);
//    }
//    public void Start() {
//        _ = Task.Run(() => updateLoop(_updateLookCancellationToken.Token));
//    }
//    public void Stop() {
//        _updateLookCancellationToken.Cancel();
//    }

//    async Task updateLoop(CancellationToken token) {
//        while (!token.IsCancellationRequested) {
//            await oneUpdate(token);
//            await Task.Delay(conversionProgressUpdateIntervalMs, token);
//        }
//    }
//    async Task oneUpdate(CancellationToken token) {
//        Dictionary<Guid, ProgressEntry> snapshot;
//        lock (_conversionsInProgress) {
//            if (_conversionsInProgress.Count == 0) return;
//            var maxAge = DateTime.UtcNow.AddSeconds(60);
//            var keysToRemove = _conversionsInProgress.Where(kvp =>
//                kvp.Value.Created < maxAge
//                && kvp.Value.ProgressInfo.Status != FileConversionStatus.InProgress
//            ).Select(kvp => kvp.Key).ToList();
//            foreach (var key in keysToRemove) _conversionsInProgress.Remove(key);
//        }
//    }
//    public async Task<int> ClearAllCaches() {
//        throw new NotImplementedException();
//    }
//    public void ClearFileCache(FileIdWithAdjustment fileIdWithAdjustment) {
//        throw new NotImplementedException();
//    }
//    async Task<FileConversionResult> waitForExistingConversion(Guid key, int maxWait) {
//        var sw = Stopwatch.StartNew();
//        while (true) {
//            var nextWait = maxWait - (int)sw.ElapsedMilliseconds;
//            bool found = false;
//            ProgressEntry? entry = null;
//            lock (_conversionsInProgress) {
//                found = _conversionsInProgress.TryGetValue(key, out entry);
//            }
//            if (!found || entry == null) {
//                // give up, means it was running but no status was found, so return in progress, that will trigger a new conversion if called again
//                return new(new(FileConversionStatus.InProgress));
//            } else if (entry.ProgressInfo.Status == FileConversionStatus.Ready) {
//                // conversion is done and should be in cache, so check cache for file:
//                var cacheResult = await _fileCache.TryGetStreamAsync(entry.FileInfo.IdWithAdjustment, nextWait);
//                return cacheResult.Status switch {
//                    FileCacheGetStatus.Ready =>
//                        // all good, file is ready in cache
//                        new(new(FileConversionStatus.Ready), cacheResult.Stream),
//                    FileCacheGetStatus.BeingWrittenTo =>
//                        // in case conversion is done, should not happen, return in progress that eventually will trigger new conversion if called again
//                        new(new(FileConversionStatus.InProgress)),
//                    FileCacheGetStatus.NotExisting =>
//                        // not in cache, should not happen, return in progress that eventually will trigger new conversion if called again
//                        new(new(FileConversionStatus.InProgress)),
//                    _ => throw new NotImplementedException("Unknown FileCacheGetStatus: " + cacheResult.Status),
//                };
//            } else if (entry.ProgressInfo.Status == FileConversionStatus.InProgress) {
//                return new(new(FileConversionStatus.InProgress));
//            } else if (entry.ProgressInfo.Status == FileConversionStatus.Error || entry.ProgressInfo.Status == FileConversionStatus.Unsupported) {
//                // conversion failed, return that info:
//                return new(new(entry.ProgressInfo.Status, message: entry.ProgressInfo.Message));
//            }
//            await Task.Delay(100); // wait a bit before checking again, to avoid tight loop
//        }
//    }

//    public Task<FileConversionResult> TryGetFormatAsync(FileConversionInfo info, int maxWaitMs, Func<Task<Stream>> getInputStream) {

//        // first direct path, just to check cache
//        var first = tryGetFormatAsync(info, 0, getInputStream).Result;
//        if (first.ProgressInfo.Status == FileConversionStatus.Ready)
//            return Task.FromResult(first);

//        // if not ready, start the conversion in background and return a task that will complete when conversion is done or max wait time is reached:
//        var tcs = new TaskCompletionSource<FileConversionResult>();
//        ThreadPool.QueueUserWorkItem(async _ => {
//            try { tcs.SetResult(await tryGetFormatAsync(info, maxWaitMs, getInputStream)); } catch (Exception ex) { tcs.SetException(ex); }
//        });
//        return Task.WhenAny(tcs.Task, Task.Delay(maxWaitMs).ContinueWith(_ => new FileConversionResult(new(FileConversionStatus.InProgress))))
//            .Unwrap();

//    }

//    async Task<FileConversionResult> tryGetFormatAsync(FileConversionInfo info, int initialMaxWaitMs, Func<Task<Stream>> getInputStream) {

//        var key = info.IdWithAdjustment.GetKey();

//        if (!_fileConverters.TryGetConverter(info.Formats, out var converter)) {
//            return new(new(FileConversionStatus.Unsupported, message: "No converter available for " + info.Formats.From + " to " + info.Formats.To + ". "));
//        }

//        var stopWatch = Stopwatch.StartNew();
//        var remainingWait = initialMaxWaitMs;

//        // check file cache first, and wait if it is already being written from converter to cache:
//        var cacheResult = await _fileCache.TryGetStreamAsync(info.IdWithAdjustment, remainingWait); // check cache, leave some time in case conversion is done, but file is writing to disk
//        switch (cacheResult.Status) {
//            case FileCacheGetStatus.Ready: return new(new(FileConversionStatus.Ready), cacheResult.Stream); // all good, file is ready in cache
//            case FileCacheGetStatus.BeingWrittenTo: return new(new(FileConversionStatus.InProgress)); // in case conversion is done, but cache is not ready with writing to disk
//            case FileCacheGetStatus.NotExisting: break; // not in cache, so continue
//            default: throw new NotImplementedException("Unknown FileCacheGetStatus: " + cacheResult.Status);
//        }

//        remainingWait = initialMaxWaitMs - (int)stopWatch.ElapsedMilliseconds;
//        if (remainingWait <= 0) return new(new(FileConversionStatus.InProgress)); // not in cache, but also no time left to wait, so return with in progress status

//        // so, not in cache, start conversion if not already in progress
//        bool conversionAlreadyInProgress = false;
//        lock (_conversionsInProgress) {
//            if (_conversionsInProgress.TryGetValue(key, out var entry)) {
//                conversionAlreadyInProgress = true;
//            } else {
//                var newProgressInfo = new FileConversionProgressInfo(FileConversionStatus.InProgress);
//                _conversionsInProgress[key] = new(DateTime.UtcNow, newProgressInfo, info);
//            }
//        }
//        if (conversionAlreadyInProgress) {
//            return await waitForExistingConversion(key, remainingWait);
//        }

//        // not in cache, no converter so start conversion:
//        FileConversionResult? result = null;
//        try {
//            var inputStream = await getInputStream();
//            remainingWait = initialMaxWaitMs - (int)stopWatch.ElapsedMilliseconds;
//            result = await converter.ConvertAsync(inputStream, info, remainingWait);
//        } catch (Exception error) {
//            if (result != null && result.Output != null) result.Output.Dispose();
//            lock (_conversionsInProgress) {
//                _conversionsInProgress[key] = new(DateTime.UtcNow, new(FileConversionStatus.Error, message: "Conversion failed. " + error.Message), info);
//            }
//            return new(new(FileConversionStatus.Error, message: error.Message));
//        }
//        switch (result.ProgressInfo.Status) {
//            case FileConversionStatus.InProgress: { // update progress info
//                    lock (_conversionsInProgress) {
//                        var created = _conversionsInProgress[key].Created; // keep original creation time for progress tracking
//                        _conversionsInProgress[key] = new(created, result.ProgressInfo, info);
//                    }
//                    return new(result.ProgressInfo); // not able to complete in maxWaitMs, but still in progress so return without file but with progress info update
//                }
//            case FileConversionStatus.Ready: { // save result
//                    FileCacheSetResult? setResult;
//                    try {
//                        if (result.Output == null) throw new Exception("Empty output of conversion");
//                        setResult = await _fileCache.SetFromStreamAsync(info.IdWithAdjustment, result.Output, remainingWait);
//                    } catch (Exception error) {
//                        lock (_conversionsInProgress) {
//                            _conversionsInProgress[key] = new(DateTime.UtcNow, new(FileConversionStatus.Error, message: "Failed to save result. " + error.Message), info);
//                        }
//                        if (result.Output != null) result.Output.Dispose();
//                        return new(new(FileConversionStatus.Error, message: "Failed to save result. " + error.Message));
//                    }
//                    if (setResult.Completed) {
//                        lock (_conversionsInProgress) {
//                            _conversionsInProgress[key] = new(DateTime.UtcNow, new(FileConversionStatus.Ready), info);
//                        }
//                        return new(new(FileConversionStatus.Ready), setResult.Stream); // all good, file is ready
//                    } else {
//                        return new(new(FileConversionStatus.InProgress)); // in progress, waiting for cache writing
//                    }
//                }
//            case FileConversionStatus.Unsupported:
//                lock (_conversionsInProgress) {
//                    _conversionsInProgress[key] = new(DateTime.UtcNow, new(FileConversionStatus.Unsupported, message: result.ProgressInfo.Message), info);
//                }
//                return new(new(FileConversionStatus.Unsupported, message: result.ProgressInfo.Message));
//            case FileConversionStatus.Error:
//                lock (_conversionsInProgress) {
//                    _conversionsInProgress[key] = new(DateTime.UtcNow, new(FileConversionStatus.Error, message: result.ProgressInfo.Message), info);
//                }
//                return new(new(FileConversionStatus.Error, message: result.ProgressInfo.Message));
//            default: throw new NotImplementedException("Unknown FileConversionStatus: " + result.ProgressInfo.Status);
//        }

//    }
//    public void Dispose() {
//        _updateLookCancellationToken.Cancel();
//    }

//    public Stream GetProgressStream(FileValue fileValue, FileAdjustmentBase adj, FileConversionProgressInfo status) {
//        if (_fileConverters.TryGetConverter(new FormatPair(adj.RequestedFormat, adj.RequestedFormat), out var converter)) {
//            return converter.GetProgressStream(fileValue, adj, status);
//        } else {
//            throw new Exception("No converter available for " + adj.RequestedFormat);
//        }
//    }
//}

//public static class FileConversionEngineExt {
//    public static async Task<ImageMetaResult> TryGetImageMetaAsync(this FileConversionEngine eng, FileConversionInfo info, int maxWaitMs, Func<Task<Stream>> getInputStream) {
//        var result = await eng.TryGetFormatAsync(info, maxWaitMs, getInputStream);
//        if (result.ProgressInfo.Status == FileConversionStatus.Ready) {
//            try {
//                if (result.Output == null) throw new Exception("Empty output of conversion");
//                var meta = ImageMeta.FromStream(result.Output);
//                result.Output.Dispose();
//                return new(result, meta);
//            } catch (Exception error) {
//                var errorMsg = "Failed to read image meta from stream. " + error.Message;
//                return new(new(new(FileConversionStatus.Error, message: errorMsg)), null);
//            }
//        } else {
//            return new(result, null);
//        }
//    }
//}