using Relatude.DB.DataStores.Sets;
using Relatude.DB.Datastores.Indexes.BTreeIndex;

namespace Relatude.DB.DataStores.Indexes.KvStore;

internal class NativeKvGuidArrayIndex : PersistedGuidArrayIndexBase {
    readonly ISortedIntIndex<byte[]> _index;
    public NativeKvGuidArrayIndex(string uniqueKey, NativeKvIndexStore store, IStorageEngine engine, SetRegister sets, string friendlyName)
        : base(store, engine.OpenOrCreateSortedIntIndex<byte[]>(uniqueKey).GetTimestamp() == 0, sets, uniqueKey, friendlyName) {
        _index = engine.OpenOrCreateSortedIntIndex<byte[]>(uniqueKey); // idempotent: returns the same open index as the base check above
    }
    protected override IEnumerable<KeyValuePair<int, Guid[]>> ReadAllPersisted() {
        foreach (var kv in _index.Entries) yield return new(kv.Key, decode(kv.Value));
    }
    protected override void PersistAdd(int nodeId, Guid[] value) => _index.Set(nodeId, encode(value));
    protected override void PersistRemove(int nodeId) => _index.Remove(nodeId);
    // the engine maps one value per id, so the array is packed into a single binary value:
    // 16 raw bytes per guid (element count = length / 16)
    static byte[] encode(Guid[] value) {
        var bytes = new byte[value.Length * 16];
        for (var i = 0; i < value.Length; i++) value[i].TryWriteBytes(bytes.AsSpan(i * 16, 16));
        return bytes;
    }
    static Guid[] decode(byte[] bytes) {
        var value = new Guid[bytes.Length / 16];
        for (var i = 0; i < value.Length; i++) value[i] = new Guid(bytes.AsSpan(i * 16, 16));
        return value;
    }
}
