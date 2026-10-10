using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Relatude.DB.Datastores.Indexes.BTreeIndex;

namespace Relatude.DB.Common;

/// <summary>
/// A service's answers kept in a Native KV file of their own, beside the AI embedding cache
/// (<c>indexes/native.{service}.cache.bin</c>), so they outlive the process: a reindex, a restart, the
/// same text translated by another request, all answered on this machine.
/// <para>An entry is found by the first 8 bytes of the SHA-256 of its key, and holds the next 8, so two
/// keys that share the first are told apart rather than answered with each other's value. It holds
/// when it was written, and is a miss once older than the age limit; it is written over when the
/// service is asked again. A value of more than a kilobyte is stored deflated - the text of a file
/// can be long.</para>
/// <para>Like the embedding cache, a file it cannot read is a cache lost, not a failure: it is emptied
/// and started again.</para>
/// </summary>
public sealed class NativeKvServiceAnswerCache : IServiceAnswerCache {
    const byte _version = 1;
    const byte _deflated = 1;
    const int _headerBytes = 1 + 1 + 8 + 8; // version, flags, written (ticks), the key's check
    const int _compressFrom = 1024;

    readonly object _lock = new();
    readonly BPlusTreeStorageEngine _storage;
    readonly IUlongIndex<byte[]> _answers;
    readonly TimeSpan _maxAge;
    bool _disposed;

    /// <param name="filePath">The cache file; null keeps it in memory only.</param>
    /// <param name="maxAge">How long an answer is good for; <see cref="ServiceAnswerCacheDefaults.MaxAge"/> when not given.</param>
    public NativeKvServiceAnswerCache(string? filePath, TimeSpan? maxAge = null) {
        _maxAge = maxAge ?? ServiceAnswerCacheDefaults.MaxAge;
        _storage = new(filePath, new() {
            PageCacheBytes = 2L * 1024 * 1024,
            PendingWriteBytes = 4L * 1024 * 1024,
            ValueCacheEntries = 0,
        });
        try {
            _answers = _storage.OpenOrCreateUlongHashIndex<byte[]>("answers");
        } catch (InvalidOperationException) {
            // an older layout; it is only a cache, so it is discarded and started again
            _storage.DeleteAll();
            _answers = _storage.OpenOrCreateUlongHashIndex<byte[]>("answers");
        }
    }

    public bool TryGet(string key, [MaybeNullWhen(false)] out string value) {
        ArgumentNullException.ThrowIfNull(key);
        var (id, check) = hash(key);
        byte[] stored;
        lock (_lock) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_answers.TryGetValue(id, out stored)) {
                value = null;
                return false;
            }
        }
        value = read(stored, check);
        return value != null;
    }

    public void Set(string key, string value) => SetMany([new(key, value)]);

    public void SetMany(IReadOnlyCollection<KeyValuePair<string, string>> entries) {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) return;
        var now = DateTime.UtcNow;
        // encoded outside the lock: deflating a long text is the slow part
        var encoded = entries.Select(e => {
            ArgumentNullException.ThrowIfNull(e.Key);
            ArgumentNullException.ThrowIfNull(e.Value);
            var (id, check) = hash(e.Key);
            return (id, bytes: write(e.Value, check, now));
        }).ToArray();
        lock (_lock) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            transaction(() => {
                foreach (var (id, bytes) in encoded) _answers.Set(id, bytes);
            });
        }
    }

    public void Remove(string key) {
        ArgumentNullException.ThrowIfNull(key);
        var (id, _) = hash(key);
        lock (_lock) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_answers.ContainsKey(id)) transaction(() => _answers.Remove(id));
        }
    }

    public void ClearAll() {
        lock (_lock) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _storage.DeleteAll();
        }
    }

    /// <summary>How many answers the file holds, those past their age included.</summary>
    public int Count {
        get {
            lock (_lock) return _answers.Count;
        }
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _storage.Dispose();
            _disposed = true;
        }
    }

    void transaction(Action action) {
        _storage.BeginTransaction();
        try {
            action();
            _storage.CommitTransaction(DateTime.UtcNow.Ticks, false);
            _storage.MakeDurable(true);
        } catch {
            if (_storage.IsInTransaction) _storage.RollbackTransaction();
            throw;
        }
    }

    static (ulong Id, ulong Check) hash(string key) {
        Span<byte> sha = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(key), sha);
        return (BinaryPrimitives.ReadUInt64LittleEndian(sha), BinaryPrimitives.ReadUInt64LittleEndian(sha[8..]));
    }

    static byte[] write(string value, ulong check, DateTime now) {
        var payload = Encoding.UTF8.GetBytes(value);
        byte flags = 0;
        if (payload.Length >= _compressFrom) {
            using var deflated = new MemoryStream();
            using (var deflate = new DeflateStream(deflated, CompressionLevel.Fastest, leaveOpen: true)) deflate.Write(payload);
            if (deflated.Length < payload.Length) {
                payload = deflated.ToArray();
                flags = _deflated;
            }
        }
        var bytes = new byte[_headerBytes + payload.Length];
        bytes[0] = _version;
        bytes[1] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(2), now.Ticks);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(10), check);
        payload.CopyTo(bytes, _headerBytes);
        return bytes;
    }

    /// <summary>The value an entry holds, or null when it is another key's, past its age, or not one this version wrote.</summary>
    string? read(byte[] stored, ulong check) {
        if (stored.Length < _headerBytes || stored[0] != _version) return null;
        if (BinaryPrimitives.ReadUInt64LittleEndian(stored.AsSpan(10)) != check) return null;
        var written = BinaryPrimitives.ReadInt64LittleEndian(stored.AsSpan(2));
        if (written <= 0 || written > DateTime.MaxValue.Ticks || DateTime.UtcNow - new DateTime(written, DateTimeKind.Utc) > _maxAge) return null;
        var payload = stored.AsSpan(_headerBytes);
        if ((stored[1] & _deflated) == 0) return Encoding.UTF8.GetString(payload);
        try {
            using var deflate = new DeflateStream(new MemoryStream(stored, _headerBytes, stored.Length - _headerBytes), CompressionMode.Decompress);
            using var inflated = new MemoryStream();
            deflate.CopyTo(inflated);
            return Encoding.UTF8.GetString(inflated.GetBuffer(), 0, (int)inflated.Length);
        } catch (InvalidDataException) {
            return null;
        }
    }
}
