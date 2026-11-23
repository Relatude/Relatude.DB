using System.Text;
using System.Text.Json;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.History;

/// <summary>What the history index has read of one log file (history.json holds one per source).</summary>
internal sealed class HistorySourceState {
    /// <summary>"secondary", or the log file's number: "00000003".</summary>
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
    public bool Secondary { get; set; }
    public Guid FileId { get; set; }
    public long Format { get; set; }
    /// <summary>How far the file has been read: the start of the next transaction.</summary>
    public long ReadPos { get; set; }
    /// <summary>Where the last transaction read starts, and its timestamp: checked before reading on, so a cut file is noticed.</summary>
    public long LastTxPos { get; set; }
    public long LastTxTs { get; set; }
    /// <summary>The newest timestamp of a transaction taken from this file or the ones before it: what comes later must be newer.</summary>
    public long MaxTs { get; set; }
    public long Rows { get; set; }
    public long Links { get; set; }
    /// <summary>While true, a 1001 file made by a truncation is still in its copy of every node.</summary>
    public bool CopyPrefix { get; set; }
    /// <summary>Whether the file starts with such a copy, for reading it again from the start.</summary>
    public bool StartsWithCopy { get; set; }
    /// <summary>Transactions from this timestamp on are the secondary log's to give (a numbered file read while it is on).</summary>
    public long SkipFrom { get; set; }
    public long Length { get; set; }
}

/// <summary>history.json: the sources read and the tables the rows number into.</summary>
internal sealed class HistoryStateFile {
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    public List<HistorySourceState> Sources { get; set; } = [];
    public List<Guid> Types { get; set; } = [Guid.Empty];
    public List<Guid> Writers { get; set; } = [Guid.Empty];
    public List<Guid> Cultures { get; set; } = [Guid.Empty];
    public List<Guid> Collections { get; set; } = [Guid.Empty];
    public List<Guid> Relations { get; set; } = [Guid.Empty];
    public DateTime? StartedUtc { get; set; }
}

/// <summary>A table: ids by number and back, number 0 being none (Guid.Empty). Safe to read while the reader adds to it.</summary>
internal sealed class HistoryTable {
    readonly List<Guid> _ids;
    readonly Dictionary<Guid, int> _numbers = [];
    readonly int _max;
    readonly object _sync = new();
    public HistoryTable(List<Guid> ids, int max) {
        _ids = ids;
        _max = max;
        if (_ids.Count == 0 || _ids[0] != Guid.Empty) _ids.Insert(0, Guid.Empty);
        for (var i = 0; i < _ids.Count; i++) _numbers.TryAdd(_ids[i], i);
    }
    public int Count { get { lock (_sync) return _ids.Count; } }
    public Guid this[int n] { get { lock (_sync) return n >= 0 && n < _ids.Count ? _ids[n] : Guid.Empty; } }
    public int Of(Guid id) {
        lock (_sync) {
            if (_numbers.TryGetValue(id, out var n)) return n;
            if (_ids.Count > _max) return 0; // full: said as none rather than wrapped into another's number
            n = _ids.Count;
            _ids.Add(id);
            _numbers[id] = n;
            return n;
        }
    }
    public bool TryGet(Guid id, out int n) { lock (_sync) return _numbers.TryGetValue(id, out n); }
}

/// <summary>
/// The history index on the index storage, in <c>indexes/history</c>: history.json, and per source a rows file of
/// changes and one of links. Files are opened for a read or a batch and closed after it, never held. history.json is
/// written beside itself and put in place; a rows file longer than it says is cut back to it.
/// </summary>
internal sealed class HistoryFiles(IIOProvider io) {
    public static readonly string[] Folder = [FileKeyUtility.IndexStoreFolderKey, "history"];
    static readonly string[] stateKey = [.. Folder, "history.json"];
    static readonly string[] stateTempKey = [.. Folder, "history.json.tmp"];
    public IIOProvider IO => io;
    public static string[] ChangesKey(string source) => [.. Folder, "changes." + source + ".bin"];
    public static string[] LinksKey(string source) => [.. Folder, "links." + source + ".bin"];
    static readonly JsonSerializerOptions json = new() { WriteIndented = true };

    public HistoryStateFile? ReadState() {
        try {
            var key = io.Exists(stateKey) ? stateKey : io.Exists(stateTempKey) ? stateTempKey : null; // a write cut short between delete and rename
            if (key == null) return null;
            var size = io.GetFileSizeOrZeroIfUnknown(key);
            if (size <= 0 || size > 64 * 1024 * 1024) return null;
            using var s = io.OpenRead(key, 0);
            var bytes = s.Read((int)size);
            var state = JsonSerializer.Deserialize<HistoryStateFile>(bytes, json);
            return state?.Version == HistoryStateFile.CurrentVersion ? state : null;
        } catch {
            return null;
        }
    }
    public void WriteState(HistoryStateFile state) {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, json);
        io.DeleteFileIfItExists(stateTempKey);
        using (var s = io.OpenAppend(stateTempKey)) s.Append(bytes); // not flushed to disk: after a crash the last batch is read again from the log
        io.DeleteFileIfItExists(stateKey);
        if (io.CanRenameFile) {
            io.RenameFile(stateTempKey, stateKey);
        } else {
            using (var s = io.OpenAppend(stateKey)) s.Append(bytes);
            io.DeleteFileIfItExists(stateTempKey);
        }
    }
    /// <summary>The bytes the index takes: history.json and the rows files of the sources named.</summary>
    public long Size(IEnumerable<string> sources) {
        long total = Length(stateKey);
        foreach (var source in sources) total += Length(ChangesKey(source)) + Length(LinksKey(source));
        return total;
    }
    public void DeleteAll() {
        io.DeleteFolderIfItExists(Folder);
        // a provider with virtual folders: the files one by one
        foreach (var f in io.GetFiles(Folder)) io.DeleteFileIfItExists(f.KeyOf());
    }
    public void DeleteSource(string source) {
        io.DeleteFileIfItExists(ChangesKey(source));
        io.DeleteFileIfItExists(LinksKey(source));
    }
    public long Length(string[] key) => io.Exists(key) ? io.GetFileSizeOrZeroIfUnknown(key) : 0;
    public void Append(string[] key, byte[] bytes, int count) {
        if (count == 0) return;
        io.EnsureFolder(Folder);
        using var s = io.OpenAppend(key);
        s.Append(bytes, count);
    }
    /// <summary>Cuts a rows file back to <paramref name="length"/> bytes.</summary>
    public void Cut(string[] key, long length) {
        var size = Length(key);
        if (size <= length) return;
        if (length <= 0) {
            io.DeleteFileIfItExists(key);
            return;
        }
        if (io.CanTruncate) {
            io.TruncateFile(key, length);
            return;
        }
        var kept = Read(key, 0, (int)length);
        io.DeleteFileIfItExists(key);
        Append(key, kept, kept.Length);
    }
    public byte[] Read(string[] key, long position, int count) {
        var buffer = new byte[count];
        if (count == 0) return buffer;
        using var s = io.OpenRead(key, position);
        var read = 0;
        while (read < count) {
            var n = s.ReadInto(buffer.AsSpan(read, count - read));
            if (n <= 0) throw new IOException("The history index file " + key.AsKeyString() + " ends before it should. ");
            read += n;
        }
        return buffer;
    }
}

/// <summary>The timestamps of one source's rows, kept in memory so periods and transactions are found by halving, without reading the rows.</summary>
internal sealed class TimestampList {
    long[] _items = new long[256];
    public int Count { get; private set; }
    public long this[int i] => _items[i];
    public void Add(long v) {
        if (Count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
        _items[Count++] = v;
    }
    public void Cut(int count) => Count = Math.Min(Count, Math.Max(0, count));
    /// <summary>The first index whose timestamp is at least <paramref name="t"/> (Count when none is).</summary>
    public int LowerBound(long t, int count) {
        int lo = 0, hi = Math.Min(count, Count);
        while (lo < hi) {
            var mid = (lo + hi) >>> 1;
            if (_items[mid] < t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
