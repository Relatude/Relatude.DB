using Relatude.DB.DataStores.Sets;
using Relatude.DB.Datastores.Indexes.BTreeIndex;

namespace Relatude.DB.DataStores.Indexes.KvStore;

internal class NativeKvIntArrayIndex : PersistedIntArrayIndexBase {
    readonly IIntIndex<byte[]> _index;
    public NativeKvIntArrayIndex(string uniqueKey, NativeKvIndexStore store, IStorageEngine engine, SetRegister sets, string friendlyName)
        // hash layout: no ordering is used here and it puts no bound on the packed array's size (see NativeKvArrayIndexName)
        : base(store, engine.OpenOrCreateIntHashIndex<byte[]>(NativeKvArrayIndexName.For(uniqueKey)).GetTimestamp() == 0, sets, uniqueKey, friendlyName) {
        _index = engine.OpenOrCreateIntHashIndex<byte[]>(NativeKvArrayIndexName.For(uniqueKey)); // idempotent: returns the same open index as the base check above
    }
    protected override IEnumerable<KeyValuePair<int, int[]>> ReadAllPersisted() {
        foreach (var kv in _index.Entries) yield return new(kv.Key, decode(kv.Value));
    }
    protected override void PersistAdd(int nodeId, int[] value) => _index.Set(nodeId, encode(value));
    protected override void PersistRemove(int nodeId) => _index.Remove(nodeId);
    // the engine maps one value per id, so the array is packed into a single binary value:
    // 4 raw bytes per int (element count = length / 4)
    static byte[] encode(int[] value) {
        var bytes = new byte[value.Length * 4];
        Buffer.BlockCopy(value, 0, bytes, 0, bytes.Length);
        return bytes;
    }
    static int[] decode(byte[] bytes) {
        var value = new int[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, value, 0, bytes.Length);
        return value;
    }
}
