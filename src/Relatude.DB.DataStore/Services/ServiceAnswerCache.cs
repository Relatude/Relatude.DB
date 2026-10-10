using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Relatude.DB.Common;

/// <summary>
/// Where a service's answers are kept on this machine, so the same call is not made over HTTP - and
/// paid for - twice. <see cref="Native"/> keeps them in a file of their own beside the AI embedding
/// cache, <see cref="Memory"/> only while the process runs, and <see cref="None"/> asks the service
/// every time.
/// </summary>
public enum ServiceCacheType {
    None = 0,
    Native = 1,
    Memory = 2,
}

/// <summary>
/// The answers a service gave, kept by what was asked: the key is everything that decides the answer
/// - the operation, the text or the SHA-256 of the file, the languages - and the value the answer as
/// JSON. An answer older than the cache's age limit is as good as missing. Two keys that hash alike are
/// told apart, so a hit is always the answer to this very question.
/// <para>A cache is only a copy: losing it costs the calls to fill it again, never data. An
/// implementation must be safe to call from several threads at once.</para>
/// </summary>
public interface IServiceAnswerCache : IDisposable {
    bool TryGet(string key, [MaybeNullWhen(false)] out string value);
    void Set(string key, string value);
    /// <summary>Several answers at once, written together: one disk write for a whole call's worth.</summary>
    void SetMany(IReadOnlyCollection<KeyValuePair<string, string>> entries);
    void Remove(string key);
    void ClearAll();
}

/// <summary>A provider that keeps answers in a cache of its own, which <c>NodeStore.MaintenanceAsync(ClearAiCache)</c> empties.</summary>
public interface ICachingServiceProvider {
    void ClearCache();
}

/// <summary>
/// Answers kept in memory only, for as long as the process runs: the most recently used, up to
/// <c>maxBytes</c> of them. For a store without a disk of its own, and for tests.
/// </summary>
public sealed class MemoryServiceAnswerCache(long maxBytes = 32L * 1024 * 1024, TimeSpan? maxAge = null) : IServiceAnswerCache {
    sealed record Entry(string Key, string Value, DateTime StoredUtc);
    readonly object _lock = new();
    readonly Cache<ulong, Entry> _entries = new(maxBytes);
    readonly TimeSpan _maxAge = maxAge ?? ServiceAnswerCacheDefaults.MaxAge;

    public bool TryGet(string key, [MaybeNullWhen(false)] out string value) {
        ArgumentNullException.ThrowIfNull(key);
        lock (_lock) {
            if (_entries.TryGet(key.XXH64Hash(), out var entry) && entry.Key == key && DateTime.UtcNow - entry.StoredUtc <= _maxAge) {
                value = entry.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    public void Set(string key, string value) {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_lock) _entries.Set(key.XXH64Hash(), new Entry(key, value, DateTime.UtcNow), (key.Length + value.Length) * 2 + 64);
    }

    public void SetMany(IReadOnlyCollection<KeyValuePair<string, string>> entries) {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var (key, value) in entries) Set(key, value);
    }

    public void Remove(string key) {
        ArgumentNullException.ThrowIfNull(key);
        lock (_lock) _entries.Clear_EvenIf0Size(key.XXH64Hash());
    }

    public void ClearAll() {
        lock (_lock) _entries.ClearAll_NotSize0();
    }

    public void Dispose() => ClearAll();
}

/// <summary>What the service answer caches share.</summary>
public static class ServiceAnswerCacheDefaults {
    /// <summary>
    /// How long an answer is kept: as long as the hosted services keep theirs. A translation corrected
    /// at the service, say, reaches this machine within that time without anyone clearing anything.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);

    /// <summary>How answers are written to and read from a cache.</summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    /// <summary>The answer kept under a key, or null when there is none or it no longer reads as one: an answer that cannot be read is a miss, not a failure.</summary>
    internal static T? Read<T>(IServiceAnswerCache cache, string key) where T : class {
        if (!cache.TryGet(key, out var json)) return null;
        try {
            return JsonSerializer.Deserialize<T>(json, Json);
        } catch (JsonException) {
            return null;
        }
    }

    /// <summary>A file's bytes as the caches name them: their SHA-256 in lowercase hex.</summary>
    internal static string Sha256(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    /// <summary>
    /// What is left on the account as the last answer from the service said, for an answer given from
    /// the cache, which costs nothing and asks nobody: -1 until a call has told.
    /// </summary>
    internal sealed class CreditsSeen {
        int _left = -1;
        public int Left => Volatile.Read(ref _left);
        public void Saw(int creditsLeft) => Volatile.Write(ref _left, creditsLeft);
    }
}
