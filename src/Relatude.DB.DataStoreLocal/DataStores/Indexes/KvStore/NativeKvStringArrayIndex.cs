using Relatude.DB.DataStores.Sets;
using Relatude.DB.Datastores.Indexes.BTreeIndex;

namespace Relatude.DB.DataStores.Indexes.KvStore;

internal class NativeKvStringArrayIndex : PersistedStringArrayIndexBase {
    readonly ISortedIndex<byte[]> _index;
    public NativeKvStringArrayIndex(string uniqueKey, NativeKvIndexStore store, IStorageEngine engine, SetRegister sets, string friendlyName)
        : base(store, engine.OpenOrCreateIndex<byte[]>(uniqueKey).GetTimestamp() == 0, sets, uniqueKey, friendlyName) {
        _index = engine.OpenOrCreateIndex<byte[]>(uniqueKey); // idempotent: returns the same open index as the base check above
    }
    protected override IEnumerable<KeyValuePair<int, string[]>> ReadAllPersisted() {
        foreach (var kv in _index.Entries) yield return new(kv.Key, decode(kv.Value));
    }
    protected override void PersistAdd(int nodeId, string[] value) => _index.Set(nodeId, encode(value));
    protected override void PersistRemove(int nodeId) => _index.Remove(nodeId);
    // the engine maps one value per id, so the array is packed into a single binary value:
    // a 7-bit encoded element count followed by length-prefixed UTF-8 strings
    static byte[] encode(string[] value) {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write7BitEncodedInt(value.Length);
        foreach (var s in value) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
    static string[] decode(byte[] bytes) {
        using var ms = new MemoryStream(bytes);
        using var r = new BinaryReader(ms);
        var value = new string[r.Read7BitEncodedInt()];
        for (var i = 0; i < value.Length; i++) value[i] = r.ReadString();
        return value;
    }
}
