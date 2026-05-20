using Relatude.DB.Common;
using Relatude.DB.IO;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.FileConversion;

public class ProgressEntry(
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
    const string _baseFolder = "converted";
    readonly Cache<Guid, byte[]> _smallCache = new(100 * 1024 * 1024); // 100mb
    int _smallFileSizeLimit = 200 * 1024; // 200kb
    string[] getFilePath(Guid key) {
        var keyString = key.ToString();
        var path = new string[_folderDepth + 1];
        path[0] = _baseFolder;
        for (int i = 0; i < _folderDepth - 1; i++) path[i + 1] = keyString.Substring(i * 2, 2);
        path[_folderDepth] = keyString;
        return path;
    }
    string[] getFilePathErrorStatus(Guid key) {
        var path = getFilePath(key);
        path[^1] += ".status";
        return path;
    }
    public bool TryGetResult(Guid key, [MaybeNullWhen(false)] out FileConversionResult result) {
        if (_smallCache.TryGet(key, out var smallData)) {
            result = new(new(FileConversionStatus.Ready, 100, 0, null), new MemoryStream(smallData));
            return true;
        }
        var path = getFilePath(key);
        if (_io.Exists(path)) {
            var stream = _io.OpenRead(path, 0).AsStream();
            var length = stream.Length;
            if (length <= _smallFileSizeLimit) {
                var buffer = new byte[length];
                stream.Read(buffer, 0, (int)length);
                stream.Dispose();
                _smallCache.Set(key, buffer, (int)length);
                result = new(new(FileConversionStatus.Ready, 100, 0, null), new MemoryStream(buffer));
            } else {
                result = new(new(FileConversionStatus.Ready, 100, 0, null), stream);
            }
            return true;
        }
        var pathError = getFilePathErrorStatus(key);
        if (_io.Exists(pathError)) {
            var errorMessage = _io.ReadString(pathError, "Error");
            result = new(new(FileConversionStatus.Error, 0, 0, errorMessage), null);
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
        var isSmallFile = input.CanSeek ? input.Length <= _smallFileSizeLimit : false;
        var smallFileData = (isSmallFile) ? new MemoryStream((int)input.Length) : null;
        var buffer = new byte[bufferSize];
        while (true) {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            await output.AppendAsyncNoChecksumOrLock(buffer, read);
            if (smallFileData != null) smallFileData.Write(buffer, 0, read);
        }
        input.Dispose();
        output.Dispose();
        if (smallFileData != null) _smallCache.Set(fileKey.GetKey(), smallFileData.ToArray(), (int)smallFileData.Length);
    }
    public void Clear(Guid key) {
        var path = getFilePath(key);
        _io.DeleteFileIfItExists(path);
        var pathError = getFilePathErrorStatus(key);
        _io.DeleteFileIfItExists(pathError);
        _smallCache.Clear_EvenIf0Size(key);
    }
    public void ClearAll() {
        if (_io is IIOProviderWithFolders ioWithFolders) {
            var folders = ioWithFolders.GetFoldersAsync(new[] { _baseFolder }, true, true).Result;
            foreach (var folder in folders) {
                if (folder.Name == _baseFolder) continue;
                ioWithFolders.DeleteFolderIfItExists(new[] { folder.Name });
            }
        }
        _smallCache.ClearAll_NotSize0();
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
    struct converterInfo(int concurrentCount, DateTime lastWork) {
        public readonly int ConcurrentCount = concurrentCount;
        public readonly DateTime LastWork = lastWork;
    }
    private readonly IFileConverter[] _converters;
    private readonly Dictionary<FormatPair, IFileConverter?> _lookUp; // from, to
    private readonly Dictionary<IFileConverter, converterInfo> _concurrentWork;
    public FileConverters(IFileConverter[] converters) {
        _converters = converters;
        _lookUp = new();
        _concurrentWork = new();
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
    public bool TryReserveWorkOnConverter(FormatPair key) {
        if (!TryGetConverter(key, out var converter)) return false;
        lock (_concurrentWork) {
            var i = _concurrentWork.TryGetValue(converter, out var match) ? match : new converterInfo(0, DateTime.MinValue);
            if (i.ConcurrentCount >= converter.MaxConcurrentWork) {
                // Console.WriteLine("Too many concurrent calls for converter");
                return false;
            }
            var now = DateTime.UtcNow;
            if (now.Subtract(i.LastWork).TotalMilliseconds <= converter.MinIntervalBetweenCallsInMs) {
                // Console.WriteLine("Converter called too often");
                return false;
            }
            _concurrentWork[converter] = new converterInfo(i.ConcurrentCount + 1, now);
            return true;
        }
    }
    public void ReleaseWorkFromConverter(FormatPair key) {
        if (!TryGetConverter(key, out var converter)) return;
        lock (_concurrentWork) {
            if (_concurrentWork.TryGetValue(converter, out var existing)) {
                var count = existing.ConcurrentCount - 1;
                if (count < 0)
                    count = 0;// should not happen                
                _concurrentWork[converter] = new converterInfo(count, existing.LastWork);
            }
        }
    }
}
internal class OrderedDictionary<K, V> : IDictionary<K, V> where K : notnull {
    private readonly IDictionary<K, LinkedListNode<KeyValuePair<K, V>>> m_Dictionary;
    private readonly LinkedList<KeyValuePair<K, V>> m_LinkedList;
    public OrderedDictionary() {
        m_Dictionary = new Dictionary<K, LinkedListNode<KeyValuePair<K, V>>>();
        m_LinkedList = new LinkedList<KeyValuePair<K, V>>();
    }

    public V this[K key] {
        get { return m_Dictionary[key].Value.Value; }
        set { Add(key, value); }
    }

    public int Count => m_Dictionary.Count;
    public virtual bool IsReadOnly => m_Dictionary.IsReadOnly;
    public ICollection<K> Keys => m_Dictionary.Keys;
    public ICollection<V> Values => m_Dictionary.Values.Select(node => node.Value.Value).ToList(); // not efficient
    public bool Add(K item, V value) {
        if (m_Dictionary.ContainsKey(item)) return false;
        var node = m_LinkedList.AddLast(new KeyValuePair<K, V>(item, value));
        m_Dictionary.Add(item, node);
        return true;
    }
    public void Add(KeyValuePair<K, V> item) {
        Add(item.Key, item.Value);
    }
    public void Clear() {
        m_LinkedList.Clear();
        m_Dictionary.Clear();
    }
    public bool Contains(KeyValuePair<K, V> item) => m_Dictionary.TryGetValue(item.Key, out var node) && EqualityComparer<V>.Default.Equals(node.Value.Value, item.Value);
    public bool ContainsKey(K key) => m_Dictionary.ContainsKey(key);
    public void CopyTo(KeyValuePair<K, V>[] array, int arrayIndex) {
        foreach (var kvp in m_LinkedList) {
            if (arrayIndex >= array.Length) break;
            array[arrayIndex++] = kvp;
        }
    }
    public IEnumerator<KeyValuePair<K, V>> GetEnumerator() => m_LinkedList.GetEnumerator();
    public bool Remove(K item) {
        if (m_Dictionary.TryGetValue(item, out var node)) {
            m_Dictionary.Remove(item);
            m_LinkedList.Remove(node);
        }
        return true;
    }

    public bool Remove(KeyValuePair<K, V> item) {
        if (m_Dictionary.TryGetValue(item.Key, out var node) && EqualityComparer<V>.Default.Equals(node.Value.Value, item.Value)) {
            m_Dictionary.Remove(item.Key);
            m_LinkedList.Remove(node);
            return true;
        }
        return false;
    }

    public bool TryGetValue(K key, [MaybeNullWhen(false)] out V value) {
        if (m_Dictionary.TryGetValue(key, out var node)) {
            value = node.Value.Value;
            return true;
        }
        value = default;
        return false;
    }
    void IDictionary<K, V>.Add(K key, V value) {
        Add(key, value);
    }

    IEnumerator IEnumerable.GetEnumerator() {
        return GetEnumerator();
    }
}
internal class FileConversions {
    readonly OrderedDictionary<Guid, ProgressEntry> _conversions = [];
    readonly HashSet<Guid> _doingWorkOnEntry = [];
    public void AddIfMissing(Guid key, Func<ProgressEntry> createEntry) {
        lock (_conversions) {
            if (_conversions.ContainsKey(key)) return;
            var entry = createEntry();
            _conversions.Add(key, entry);
        }
    }
    public bool TryGet(Guid key, [MaybeNullWhen(false)] out ProgressEntry entry) {
        lock (_conversions) {
            return _conversions.TryGetValue(key, out entry);
        }
    }
    public bool TryGetWorkIfNotAlreadyWorkingOnEntryOrConverterTooBusy([MaybeNullWhen(false)] out ProgressEntry entry, Func<ProgressEntry, bool> tryReserveWork) {
        lock (_conversions) {
            if (_conversions.Count == 0) {
                entry = null;
                return false;
            }
            // look for next entry that is allowed to start
            foreach (var potentialKey in _conversions.Keys) {
                if (_doingWorkOnEntry.Contains(potentialKey)) continue; // already working on this entry
                if (!_conversions.TryGetValue(potentialKey, out var potentialEntry)) {
                    // this should not happen, but if it does, just skip this key
                    continue;
                }
                if (tryReserveWork(potentialEntry)) {
                    _doingWorkOnEntry.Add(potentialKey);
                    entry = potentialEntry;
                    return true;
                } else {
                    // worker too busy, move to next
                }
            }
            entry = null;
            return false;
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
            _conversions.Remove(key);
            _doingWorkOnEntry.Remove(key);
        }
    }
    public int Count {
        get {
            lock (_conversions) {
                return _conversions.Count;
            }
        }
    }
    public void ClearAll() {
        lock (_conversions) {
            _conversions.Clear();
            _doingWorkOnEntry.Clear();
        }
    }
    public ProgressEntry[] GetAll() {
        lock (_conversions) {
            return [.. _conversions.Values];
        }
    }
    internal void RegisterNotDoingWorkOnEntry(ProgressEntry entry) {
        lock (_conversions) {
            var key = entry.FileInfo.IdWithAdjustment.GetKey();
            _doingWorkOnEntry.Remove(key);
        }
    }
}
class ConversionScheduler(Action task, Action<Exception> onError) {
    object _pulseStateLock = new();
    bool _pulsesRunning = false;
    Timer? _hart;
    public void Start() {
        lock (_pulseStateLock) {
            _hart = new Timer(_ => pulse(), null, 1000, 1000);
        }
    }
    public bool IsStopped {
        get {
            lock (_pulseStateLock) {
                return _hart == null;
            }
        }
    }
    public void RunSoon() {
        lock (_pulseStateLock) {
            if (_hart == null) return;
            _hart.Change(1, 10000);
        }
    }
    public void Stop() {
        lock (_pulseStateLock) {
            _hart?.Dispose();
            _hart = null;
        }
    }
    void pulse() {
        lock (_pulseStateLock) {
            if (_pulsesRunning) return;
            _pulsesRunning = true;
        }
        try {
            task();
        } catch (Exception ex) {
            onError(ex);
        } finally {
            lock (_pulseStateLock) {
                _pulsesRunning = false;
            }
        }
    }
}
public class FileConversionEngine : IDisposable {
    readonly FileConverters _fileConverters;
    readonly FileConversions _conversionsInProgress;
    readonly FileCache _fileCache;
    readonly ConversionScheduler _scheduler;
    public FileConversionEngine(IFileConverter[] converters, IIOProvider io, FileKeyUtility fileKeys) {
        _fileConverters = new(converters);
        _conversionsInProgress = new();
        _fileCache = new(io);
        _scheduler = new ConversionScheduler(pulse, ex => Console.WriteLine(ex.ToString()));
        _scheduler.Start();
    }
    bool tryReserveWork(ProgressEntry entry) {
        return _fileConverters.TryReserveWorkOnConverter(entry.FileInfo.Formats);
    }
    void pulse() {
        while (_conversionsInProgress.TryGetWorkIfNotAlreadyWorkingOnEntryOrConverterTooBusy(out var entry, tryReserveWork)) {
            ThreadPool.QueueUserWorkItem(async _ => {
                try {
                    var progress = await doConvertWork(entry);
                    switch (progress.Status) {
                        case FileConversionStatus.Ready:
                            _conversionsInProgress.Remove(entry);
                            break;
                        case FileConversionStatus.InProgress:
                            _conversionsInProgress.UpdateIfExists(new(entry.Created, progress, entry.FileInfo, entry.GetInputStream));
                            break;
                        case FileConversionStatus.Error:
                            _fileCache.SaveErrorStatus(entry.FileInfo.IdWithAdjustment.GetKey(), progress.Message ?? "Failed. ");
                            _conversionsInProgress.Remove(entry);
                            break;
                        default:
                            throw new Exception("Unknown status: " + progress.Status);
                    }
                } catch (Exception ex) {
                    _fileCache.SaveErrorStatus(entry.FileInfo.IdWithAdjustment.GetKey(), ex.Message);
                    _conversionsInProgress.Remove(entry);
                }
                _fileConverters.ReleaseWorkFromConverter(entry.FileInfo.Formats);
                _conversionsInProgress.RegisterNotDoingWorkOnEntry(entry);
            });
        }
        if (_conversionsInProgress.Count > 0) _scheduler.RunSoon();
    }
    async Task<FileConversionProgressInfo> doConvertWork(ProgressEntry entry) {
        if (!_fileConverters.TryGetConverter(entry.FileInfo.Formats, out var converter)) {
            throw new Exception("No converter available for " + entry.FileInfo.Formats.ToString()?.ToUpper());
        }
        var conversionResult = await converter.DoConvertWork(entry.GetInputStream, entry.FileInfo);
        if (conversionResult.Output != null) {
            await _fileCache.SetFromStreamAsync(entry.FileInfo.IdWithAdjustment, conversionResult.Output);
        }
        return conversionResult.ProgressInfo;
    }
    public async Task<FileConversionResult> TryGetFormatAsync(FileConversionInfo info, int maxWaitMs, Func<Task<Stream>> getInputStream) {
        var key = info.IdWithAdjustment.GetKey();
        if (_fileCache.TryGetResult(key, out var result)) return result; // check cache first
        var sw = Stopwatch.StartNew();
        ProgressEntry? entry;
        _conversionsInProgress.AddIfMissing(key, () =>
            new(DateTime.UtcNow, new(FileConversionStatus.InProgress), info, getInputStream)
        );
        _scheduler.RunSoon();
        if (!_fileConverters.TryGetConverter(info.Formats, out var converter)) {
            return new(new(FileConversionStatus.Error, 0, 0, "No converter available from " + info.Formats.From.ToString().ToUpper() + " to " + info.Formats.To.ToString().ToUpper() + ". "), null);
        }
        while (_conversionsInProgress.TryGet(key, out entry)) {
            if (sw.ElapsedMilliseconds >= maxWaitMs) break;
            var remaining = maxWaitMs - sw.ElapsedMilliseconds;
            var min = sw.ElapsedMilliseconds switch { < 100 => 20, < 1000 => 100, < 5000 => 500, _ => 1000 };
            await Task.Delay((int)Math.Min(min, remaining));
        }
        if (_fileCache.TryGetResult(key, out result)) return result;
        if (entry != null) return new(entry.ProgressInfo, null);
        return new(new(FileConversionStatus.Error, 0, 0, "Unknown status"), null);
    }
    public void Dispose() {
    }
    public Stream GetStatus(FileValue fileValue, FileAdjustmentBase adj, FileConversionProgressInfo status) {
        if (_fileConverters.TryGetConverter(new FormatPair(adj.RequestedFormat, adj.RequestedFormat), out var converter)) {
            return converter.GetStatusRepresentation(fileValue, adj, status);
        } else {
            var baseFormat = FileFormatUtil.GetBaseFormatFromDetailedFormat(fileValue.Format);
            if (baseFormat == FileType.Image) {
                if (_fileConverters.TryGetConverter(new FormatPair(FileFormat.Png, FileFormat.Png), out converter)) {
                    return converter.GetStatusRepresentation(fileValue, adj, status);
                }
            }
        }
        throw new Exception("No converter available for status representation of format " + adj.RequestedFormat.ToString().ToUpper());
    }
    public void Start() => _scheduler.Start();
    public void Stop() => _scheduler.Stop();
    public void ClearCache(Guid key) => _fileCache.Clear(key);
    public void ClearAllCache() => _fileCache.ClearAll();
    public void ClearQueue() {
        _conversionsInProgress.ClearAll();
    }
    public int QueueCount => _conversionsInProgress.Count;
    public ProgressEntry[] GetRunning() => _conversionsInProgress.GetAll();
    public void KillRunning(Guid key) {
        throw new NotImplementedException();
        if (_conversionsInProgress.TryGet(key, out var entry)) {
            //_scheduler.TryStopIfRunning(key);
            //_conversionsInProgress.Remove(entry);
            //_fileCache.Clear(key);
        }
    }
}
