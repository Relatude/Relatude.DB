using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.IO;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;


namespace Relatude.DB.FileConverter;

internal class ProgressEntry(
    DateTime created,
    FileConversionProgressInfo progressInfo,
    FileConversionInfo fileInfo,
    Func<Task<Stream>> getInputStream
    ) {
    public DateTime Created { get; } = created;
    public FileConversionProgressInfo ProgressInfo { get; } = progressInfo;
    public FileConversionInfo FileInfo { get; } = fileInfo;
    public Func<Task<Stream>> GetInputStream { get; } = getInputStream;
}
public class FileConversionInfo(FileIdWithAdjustment idWithAdjustment, string fileName, string hash, FileFormat format) {
    public FileIdWithAdjustment IdWithAdjustment { get; } = idWithAdjustment;
    public string FileName { get; } = fileName;
    public string Hash { get; } = hash;
    public FileFormat FromFormat { get; } = format;
    public FormatPair Formats { get; } = new FormatPair(format, idWithAdjustment.Adjustment.RequestedFormat);
}
internal class FileCache {
    readonly IIOProvider _io;
    public FileCache(IIOProvider io) { _io = io; }
    const int _folderDepth = 2;
    const string baseFolder = "converts";
    Dictionary<Guid, SemaphoreSlim> _locks = [];
    string[] getFilePath(Guid key) {
        var keyString = key.ToString();
        var path = new string[_folderDepth + 1];
        path[0] = baseFolder;
        for (int i = 0; i < _folderDepth - 1; i++) path[i + 1] = keyString.Substring(i * 2, 2);
        path[_folderDepth] = keyString;
        return path;
    }
    string[] getFilePathErrorStatus(Guid key) {
        var path = getFilePath(key);
        path[^1] += ".status";
        return path;
    }
    public bool TryGet(Guid key, [MaybeNullWhen(false)] out FileConversionResult result) {
        var pathError = getFilePathErrorStatus(key);
        if (_io.Exists(pathError)) {
            var errorMessage = _io.ReadString(pathError, "Error");
            result = new(new(FileConversionStatus.Error, 0, 0, errorMessage), null);
            return true;
        }
        var path = getFilePath(key);
        if (_io.Exists(path)) {
            var stream = _io.OpenRead(path, 0).AsStream();
            var length = stream.Length;
            result = new(new(FileConversionStatus.Ready, 100, 0, null), stream);
            return true;
        }
        result = null;
        return false;
    }
    public async Task SetFromStreamAsync(FileIdWithAdjustment fileKey, Stream input) {
        var filePath = getFilePath(fileKey.GetKey());
        using var output = _io.OpenAppend(filePath);
        long bufferSize = 1024 * 1024;
        bufferSize = Math.Min(bufferSize, input.CanSeek ? input.Length : bufferSize);
        var buffer = new byte[bufferSize];
        while (true) {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            await output.AppendAsyncNoChecksumOrLock(buffer, read);
        }
        output.Dispose();
    }
    public void SaveErrorStatus(Guid key, string errorMessage) {
        var path = getFilePath(key);
        _io.DeleteFileIfItExists(path);
        var pathError = getFilePathErrorStatus(key);
        _io.DeleteFileIfItExists(pathError);
        _io.WriteString(pathError, errorMessage);
    }
}
internal class FileConverters {
    private readonly IFileConverter[] _converters;
    private readonly Dictionary<FormatPair, IFileConverter?> _lookUp; // from, to
    public FileConverters(IFileConverter[] converters) {
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
internal class FileConversions {
    readonly Dictionary<Guid, ProgressEntry> _conversions = [];
    Queue<Guid> _queuedForRunning = [];
    readonly HashSet<Guid> _running = [];
    public void AddIfMissing(Guid key, Func<ProgressEntry> createEntry) {
        lock (_conversions) {
            if (_conversions.ContainsKey(key)) return;
            var entry = createEntry();
            _conversions[key] = entry;
            _queuedForRunning.Enqueue(key);
        }
    }
    public bool TryGet(Guid key, [MaybeNullWhen(false)] out ProgressEntry entry) {
        lock (_conversions) {
            return _conversions.TryGetValue(key, out entry);
        }
    }
    public bool TryDequeueForRunning([MaybeNullWhen(false)] out ProgressEntry entry, int maxConcurrentRunning) {
        lock (_conversions) {
            if (_running.Count >= maxConcurrentRunning || _queuedForRunning.Count == 0) {
                entry = null;
                return false;
            }
            var key = _queuedForRunning.Dequeue();
            _running.Add(key);
            entry = _conversions[key];
            return true;
        }
    }
    public void UpdateIfExists(ProgressEntry entry) {
        lock (_conversions) {
            var key = entry.FileInfo.IdWithAdjustment.GetKey();
            if (_conversions.ContainsKey(key)) {
                _conversions[key] = entry;
            }
        }
    }
    public void Remove(ProgressEntry entry) {
        lock (_conversions) {
            var key = entry.FileInfo.IdWithAdjustment.GetKey();
            if (_conversions.Remove(key)) {
                if (!_running.Remove(key)) {
                    _queuedForRunning = new(_queuedForRunning.Where(k => k != key));
                }
            }
        }
    }
    public int Count {
        get {
            lock (_conversions) {
                return _conversions.Count;
            }
        }
    }
}
public class FileConversionEngine : IDisposable {
    readonly FileConverters _fileConverters;
    readonly FileConversions _conversionsInProgress;
    readonly FileCache _fileCache;
    readonly int _maxConcurrentRunning = Math.Max(1, Environment.ProcessorCount / 2);
    Timer? _hart;
    public FileConversionEngine(IFileConverter[] converters, IIOProvider io, FileKeyUtility fileKeys) {
        _fileConverters = new(converters);
        _conversionsInProgress = new();
        _fileCache = new(io);
        Start();
    }
    public void Start() {
        lock (_pulseStateLock) {
            _hart = new Timer(_ => pulse(), null, 1000, 10000);
        }
    }
    void runSoonIfQueued() {
        lock (_pulseStateLock) {
            if (_hart == null) return;
            if (_conversionsInProgress.Count > 0) _hart.Change(1, 10000);
        }
    }
    public void Stop() {
        lock (_pulseStateLock) {
            _hart?.Dispose();
            _hart = null;
        }
    }
    object _pulseStateLock = new();
    bool _pulsesRunning = false;
    void pulse() {
        //Console.WriteLine("Pulse: " + _conversionsInProgress.Count + " conversions in progress");
        lock (_pulseStateLock) {
            if (_pulsesRunning) return;
            _pulsesRunning = true;
        }
        try {
            while (_conversionsInProgress.TryDequeueForRunning(out var entry, _maxConcurrentRunning)) {
                ThreadPool.QueueUserWorkItem(async _ => {
                    try {
                        await convertNow(entry);
                    } catch (Exception ex) {
                        _fileCache.SaveErrorStatus(entry.FileInfo.IdWithAdjustment.GetKey(), ex.Message);
                    } finally {
                        _conversionsInProgress.Remove(entry);
                    }
                });
            }
            runSoonIfQueued();
        } catch (Exception ex) {
            Console.WriteLine(ex.ToString());
        } finally {
            lock (_pulseStateLock) {
                _pulsesRunning = false;
            }
        }
    }
    async Task convertNow(ProgressEntry entry) {
        var converter = _fileConverters.TryGetConverter(entry.FileInfo.Formats, out var c) ? c : throw new Exception("No converter available for " + entry.FileInfo.Formats);
        using var inputStream = await entry.GetInputStream();
        using var outputStream = await converter.ConvertAsync(inputStream, entry.FileInfo);
        await _fileCache.SetFromStreamAsync(entry.FileInfo.IdWithAdjustment, outputStream);
    }
    public async Task<FileConversionResult> TryGetFormatAsync(FileConversionInfo info, int maxWaitMs, Func<Task<Stream>> getInputStream) {
        var key = info.IdWithAdjustment.GetKey();
        var sw = Stopwatch.StartNew();
        if (_fileCache.TryGet(key, out var result)) return result; // check cache first
        ProgressEntry? entry;
        _conversionsInProgress.AddIfMissing(key, () =>
            new(DateTime.UtcNow, new(FileConversionStatus.InProgress), info, getInputStream)
        );
        runSoonIfQueued();
        while (_conversionsInProgress.TryGet(key, out entry)) {
            if (sw.ElapsedMilliseconds >= maxWaitMs) break;
            var remaining = maxWaitMs - sw.ElapsedMilliseconds;
            var min = sw.ElapsedMilliseconds switch { < 100 => 20, < 1000 => 100, < 5000 => 500, _ => 1000 };
            await Task.Delay((int)Math.Min(min, remaining));
        }
        if (_fileCache.TryGet(key, out result)) return result;
        return new(new(FileConversionStatus.Error), null);
    }
    public void Dispose() {
    }
    public Stream GetProgressStream(FileValue fileValue, FileAdjustmentBase adj, FileConversionProgressInfo status) {
        if (_fileConverters.TryGetConverter(new FormatPair(adj.RequestedFormat, adj.RequestedFormat), out var converter)) {
            return converter.GetProgressStream(fileValue, adj, status);
        } else {
            throw new Exception("No converter available for " + adj.RequestedFormat);
        }
    }
}
