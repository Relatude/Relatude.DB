using System.Buffers.Binary;
using System.Text;
using Relatude.DB.Datamodels;
using Relatude.DB.Datastores.Indexes.BTreeIndex;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Relations;
using Relatude.DB.Transactions;

namespace Relatude.DB.DataStores.StateStores;

/// <summary>
/// The native KV engine as state store: every map is a hash or sorted index in one B+tree file,
/// published with each transaction and made durable after each WAL flush, like the value indexes
/// of <see cref="KvStore.NativeKvIndexStore"/>. Memory use is the engine's cache budget, not the node count.
/// </summary>
public sealed class NativeKvStateStore : IndexEngineBase, IStateStore {
    readonly BPlusTreeStorageEngine _storage;
    readonly ISortedIntIndex<string> _settings;
    bool _disposed;
    long _budgetBytes;
    internal enum SettingKey : int { WalId = 1, LastId = 2, AddressCultures = 3 }
    /// <param name="folderPath">Folder for the state file, or null for a memory only engine (tests).</param>
    /// <param name="maxMemoryBytes">Cache budget; negative keeps the built-in sizes.</param>
    public NativeKvStateStore(string? folderPath, long maxMemoryBytes = -1) {
        string? filePath = null;
        if (folderPath != null) {
            Directory.CreateDirectory(folderPath);
            filePath = Path.Combine(folderPath, "state.db");
        }
        const long minBytes = 256L * 1024;
        var options = maxMemoryBytes < 0
            ? new BPlusTreeEngineOptions { PageCacheBytes = 64L * 1024 * 1024, PendingWriteBytes = 32L * 1024 * 1024 }
            : new BPlusTreeEngineOptions { PageCacheBytes = Math.Max(minBytes, maxMemoryBytes / 3 * 2), PendingWriteBytes = Math.Max(minBytes, maxMemoryBytes / 3) };
        _budgetBytes = maxMemoryBytes;
        _storage = new BPlusTreeStorageEngine(filePath, options);
        _settings = _storage.OpenOrCreateSortedIntIndex<string>("settings");
    }
    public override string Name => "Native state store";
    public IIndexEngine Engine => this;
    public ISegmentMap CreateSegmentMap() => new KvSegmentMap(_storage.OpenOrCreateIntHashIndex<byte[]>("segments"));
    public IGuidMap CreateGuidMap() => new KvGuidMap(this, _storage.OpenOrCreateGuidHashIndex<int>("ids"), _storage.OpenOrCreateIntHashIndex<Guid>("guids"));
    public IAddressMap CreateAddressMap() => new KvAddressMap(this, _storage.OpenOrCreateSortedUlongIndex<string>("addresses"));
    public IRelationIndex CreateRelationIndex(RelationModel relation) => new KvRelationIndex(_storage, relation.Id, relation.RelationType);
    internal string? GetSetting(SettingKey key) => _settings.TryGetValue((int)key, out var value) ? value : null;
    internal void SetSetting(SettingKey key, string? value) {
        if (value == null) _settings.Remove((int)key);
        else _settings.Set((int)key, value);
    }
    protected override void BeginTransactionCore() => _storage.BeginTransaction();
    protected override void CommitTransactionCore(long timestamp) => _storage.PublishTransaction(timestamp);
    protected override void MakeDurableCore() => _storage.MakeDurable(deepDiskFlush: false);
    protected override void RollbackTransactionCore() => _storage.RollbackTransaction();
    protected override Guid ReadWalFileId() => Guid.TryParse(GetSetting(SettingKey.WalId), out var id) ? id : Guid.Empty;
    protected override void WriteWalFileId(Guid walFileId, long? timestamp) {
        _storage.BeginTransaction();
        try {
            _settings.Set((int)SettingKey.WalId, walFileId.ToString());
            _storage.CommitTransaction(timestamp ?? _storage.GetTimestamp(), deepDiskFlush: false);
        } catch {
            try { _storage.RollbackTransaction(); } catch { }
            throw;
        }
    }
    public override long GetTimestamp() => _storage.GetTimestamp();
    public override long? GetMemoryUsage() => _storage.GetMemoryUsage();
    public override long GetMemoryBudget() => _budgetBytes;
    public override bool TrySetMemoryBudget(long bytes) {
        _storage.SetMemoryBudget(bytes);
        _budgetBytes = bytes;
        return true;
    }
    public override long GetTotalDiskSpace() => _storage.GetTotalDiskSpace();
    public override void OptimizeDisk() { }
    protected override void DeleteUnopenedIndexesCore() => _storage.DeleteUnopenedIndexes();
    protected override void ResetAllDataCore() => _storage.DeleteAll();
    protected override void DisposeCore() {
        if (_disposed) return;
        _disposed = true;
        _storage.Dispose();
    }
}
sealed class KvSegmentMap(IIntIndex<byte[]> index) : ISegmentMap {
    public bool PersistedByEngine => true;
    public int Count => index.Count;
    public bool TryGet(int id, out NodeSegment segment) {
        if (index.TryGetValue(id, out var bytes)) {
            segment = decode(bytes);
            return true;
        }
        segment = default;
        return false;
    }
    public bool Contains(int id) => index.ContainsKey(id);
    public void Set(int id, NodeSegment segment) => index.Set(id, encode(segment));
    public bool Remove(int id) => index.Remove(id);
    public void EnsureCapacity(int count) { }
    public IEnumerable<KeyValuePair<int, NodeSegment>> Entries => index.Entries.Select(e => new KeyValuePair<int, NodeSegment>(e.Key, decode(e.Value)));
    static byte[] encode(NodeSegment segment) {
        var bytes = new byte[12];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, segment.AbsolutePosition);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), segment.Length);
        return bytes;
    }
    static NodeSegment decode(byte[] bytes) => new(BinaryPrimitives.ReadInt64LittleEndian(bytes), BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)));
}
sealed class KvGuidMap(NativeKvStateStore store, IGuidIndex<int> ids, IIntIndex<Guid> guids) : IGuidMap {
    int? _lastId;
    public bool PersistedByEngine => true;
    public int Count => ids.Count;
    public int LastId {
        get => _lastId ??= int.TryParse(store.GetSetting(NativeKvStateStore.SettingKey.LastId), out var value) ? value : 0;
        set {
            if (_lastId == value) return;
            _lastId = value;
            store.SetSetting(NativeKvStateStore.SettingKey.LastId, value.ToString());
        }
    }
    public bool TryGetId(Guid guid, out int id) => ids.TryGetValue(guid, out id);
    public bool TryGetGuid(int id, out Guid guid) => guids.TryGetValue(id, out guid);
    public void Add(int id, Guid guid) {
        ids.Set(guid, id);
        guids.Set(id, guid);
    }
    public void Remove(int id, Guid guid) {
        ids.Remove(guid);
        guids.Remove(id);
    }
    public void EnsureCapacity(int count) { }
    public IEnumerable<KeyValuePair<int, Guid>> Entries => guids.Entries;
}
sealed class KvAddressMap(NativeKvStateStore store, ISortedUlongIndex<string> index) : IAddressMap {
    const int maxAddressBytes = 900; // a sorted index keeps the value inside the tree key, which must fit a page
    public bool PersistedByEngine => true;
    public int Count => index.Count;
    public string? Meta {
        get => store.GetSetting(NativeKvStateStore.SettingKey.AddressCultures);
        set => store.SetSetting(NativeKvStateStore.SettingKey.AddressCultures, value);
    }
    public bool TryGet(long key, out string address) => index.TryGetValue((ulong)key, out address!);
    public void Set(long key, string address) {
        if (Encoding.UTF8.GetByteCount(address) > maxAddressBytes)
            throw new ExceptionWithoutIntegrityLoss("The address is longer than the " + maxAddressBytes + " bytes the native state store can hold: " + address);
        index.Set((ulong)key, address);
    }
    public bool Remove(long key) => index.Remove((ulong)key);
    public long[] GetOwners(string address) => index.GetIds(address).Select(key => (long)key).ToArray();
    public IEnumerable<KeyValuePair<long, string>> Entries => index.Entries.Select(e => new KeyValuePair<long, string>((long)e.Key, e.Value));
}
