using Relatude.DB.Common;
using System.Diagnostics;

namespace Relatude.DB.DataStores.Indexes.KvStore;

/// <summary>
/// Implemented by persisted value indexes whose per-value id set cache can be saved to and
/// restored from the facet sets sidecar file (see <see cref="FacetSetsFile"/>).
/// </summary>
internal interface IValueIdsCachePersistence {
    string UniqueKey { get; }
    bool AreThereNewUnsavedCachedSets { get; }
    byte[]? SaveCachedSets();
    void LoadCachedSets(byte[] section);
}

/// <summary>
/// Sidecar file persisting the per-value id set caches of the native KV value indexes across
/// restarts, so the first filtered facet query after a clean shutdown is served from memory
/// instead of walking the value trees. Written at store dispose, stamped with the engine
/// timestamp; only loaded when the stamp matches the engine exactly (any write in between -
/// including a crash before the write reached the file - makes it stale and it is ignored).
/// </summary>
internal static class FacetSetsFile {
    const long _magic = 0x315445_53464244; // "RDBFSET1"
    public const string FileName = "facetsets.bin";

    public static Dictionary<string, byte[]>? TryRead(string path, long engineTimestamp, Action<string>? log, out long cachedTimestamp) {
        cachedTimestamp = 0;
        try {
            if (!File.Exists(path)) {
                log?.Invoke($"No facet sets file.");
                return null;
            }
            using var r = new BinaryReader(new BufferedStream(File.OpenRead(path), 1 << 20));
            if (r.ReadInt64() != _magic) return null;
            cachedTimestamp = r.ReadInt64();
            if (cachedTimestamp != engineTimestamp) {
                log?.Invoke($"Deleted old facet sets file.");
                File.Delete(path);
                return null;
            }
            var sectionCount = r.ReadInt32();
            var sections = new Dictionary<string, byte[]>(sectionCount);
            for (var i = 0; i < sectionCount; i++) {
                var key = r.ReadString();
                var length = r.ReadInt32();
                sections[key] = r.ReadBytes(length);
            }
            log?.Invoke($"Facet sets file loaded, {sectionCount} sections. ");
            return sections;
        } catch(Exception err) {
            log?.Invoke("Error reading facet sets file: " + err.Message);
            return null; // unreadable or truncated: fall back to cold caches
        }
    }

    public static void Write(string path, long engineTimestamp, IEnumerable<IValueIdsCachePersistence> indexes, Action<string>? log) {
        var sections = new List<(string key, byte[] data)>();
        var sw = Stopwatch.StartNew();
        foreach (var index in indexes) {
            var data = index.SaveCachedSets();
            if (data != null) sections.Add((index.UniqueKey, data));
        }
        if (File.Exists(path)) File.Delete(path);
        var dir = Path.GetDirectoryName(path);
        if(!Directory.Exists(dir)) Directory.CreateDirectory(dir!);
        using (var w = new BinaryWriter(new BufferedStream(File.Create(path), 1 << 20))) {
            w.Write(_magic);
            w.Write(engineTimestamp);
            w.Write(sections.Count);
            foreach (var (key, data) in sections) {
                w.Write(key);
                w.Write(data.Length);
                w.Write(data);
            }
        }
        var fileSize = new FileInfo(path).Length;
        sw.Stop();
        log?.Invoke($"Facet sets file of {fileSize.ToByteString()} written successfully in {sw.ElapsedMilliseconds} ms.");
    }

    // typed value (de)serialization for the cache keys; tag 0 = type not supported (not persisted)
    public static byte TypeTag<T>() {
        var t = typeof(T);
        if (t == typeof(int)) return 1;
        if (t == typeof(long)) return 2;
        if (t == typeof(double)) return 3;
        if (t == typeof(float)) return 4;
        if (t == typeof(bool)) return 5;
        if (t == typeof(Guid)) return 6;
        if (t == typeof(string)) return 7;
        if (t == typeof(DateTime)) return 8;
        if (t == typeof(DateTimeOffset)) return 9;
        if (t == typeof(TimeSpan)) return 10;
        if (t == typeof(decimal)) return 11;
        return 0;
    }
    public static void WriteValue<T>(BinaryWriter w, T v) {
        switch (v) {
            case int i: w.Write(i); break;
            case long l: w.Write(l); break;
            case double d: w.Write(d); break;
            case float f: w.Write(f); break;
            case bool b: w.Write(b); break;
            case Guid g: w.Write(g.ToByteArray()); break;
            case string s: w.Write(s); break;
            case DateTime dt: w.Write(dt.ToBinary()); break;
            case DateTimeOffset dto: w.Write(dto.Ticks); w.Write(dto.Offset.Ticks); break;
            case TimeSpan ts: w.Write(ts.Ticks); break;
            case decimal m: w.Write(m); break;
            default: throw new NotSupportedException(typeof(T).Name);
        }
    }
    public static T ReadValue<T>(BinaryReader r) {
        var t = typeof(T);
        object v;
        if (t == typeof(int)) v = r.ReadInt32();
        else if (t == typeof(long)) v = r.ReadInt64();
        else if (t == typeof(double)) v = r.ReadDouble();
        else if (t == typeof(float)) v = r.ReadSingle();
        else if (t == typeof(bool)) v = r.ReadBoolean();
        else if (t == typeof(Guid)) v = new Guid(r.ReadBytes(16));
        else if (t == typeof(string)) v = r.ReadString();
        else if (t == typeof(DateTime)) v = DateTime.FromBinary(r.ReadInt64());
        else if (t == typeof(DateTimeOffset)) v = new DateTimeOffset(r.ReadInt64(), new TimeSpan(r.ReadInt64()));
        else if (t == typeof(TimeSpan)) v = new TimeSpan(r.ReadInt64());
        else if (t == typeof(decimal)) v = r.ReadDecimal();
        else throw new NotSupportedException(t.Name);
        return (T)v;
    }
}
