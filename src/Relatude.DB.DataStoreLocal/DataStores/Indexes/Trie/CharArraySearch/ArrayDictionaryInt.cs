using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Indexes.Trie.CharArraySearch;
// Simple class that uses an array for faster lookups if id is smaller than upper ID.
// the value 0 is not legal, as 0 is used to indicate no record in array
// the actual countvalue in the array is v - 1
// [0] is never used
internal class ArrayDictionaryInt {
    const int _upperId = 1024 * 500; // use array for first 500 000 ( Could be linked to max id )
    const int _initialSize = 1024; // grows on demand up to _upperId, so small indexes stay small
    int[] _arr = new int[_initialSize];
    Dictionary<int, int> _dic = [];
    public int Count;
    void ensureArraySize(int id) { // only called for id < _upperId
        if (id < _arr.Length) return;
        var newSize = _arr.Length;
        while (newSize <= id) newSize *= 2;
        if (newSize > _upperId) newSize = _upperId;
        Array.Resize(ref _arr, newSize);
    }
    public int this[int id] {
        get {
            if (id < _upperId) {
                var v = id < _arr.Length ? _arr[id] : 0;
                if (v == 0) throw new Exception(id + " is not a known record. ");
                return v - 1;
            }
            return _dic[id];
        }
    }
    public bool TryGetValue(int id, out int v) {
        if (id < _upperId) {
            var v0 = id < _arr.Length ? _arr[id] : 0;
            if (v0 == 0) { v = 0; return false; }
            v = v0 - 1;
            return true;
        }
        return _dic.TryGetValue(id, out v);
    }
    public void Add(int id, int v) {
        if (id < _upperId) {
            ensureArraySize(id);
            if (_arr[id] != 0) throw new Exception(id + " is already a known record. ");
            _arr[id] = v + 1;
        } else _dic.Add(id, v);
        Count++;
    }
    public void Remove(int id) {
        if (id < _upperId) {
            if (id >= _arr.Length || _arr[id] == 0) throw new Exception(id + " is not a known record. ");
            _arr[id] = 0;
        } else {
            _dic.Remove(id);
        }
        Count--;
    }
    public bool ContainsKey(int id) => id < _upperId ? id < _arr.Length && _arr[id] != 0 : _dic.ContainsKey(id);
    internal long SumValues() {
        long sum = 0;
        var arrayCount = Count - _dic.Count;
        var found = 0;
        for (int id = 0; id < _arr.Length && found < arrayCount; id++) {
            var v0 = _arr[id];
            if (v0 != 0) {
                sum += v0 - 1;
                found++;
            }
        }
        foreach (var kv in _dic) sum += kv.Value;
        return sum;
    }
    internal void WriteState(IAppendStream stream) {
        stream.WriteVerifiedInt(Count);
        int i = 0;
        for (int id = 0; id < _arr.Length; id++) {
            var v0 = _arr[id];
            if (v0 != 0) {
                stream.WriteInt(id);
                stream.WriteInt(v0 - 1);
                i++;
                if (i == Count) return;
            }
        }
        if (i + _dic.Count != Count) throw new Exception("Count mismatch. ");
        foreach (var kv in _dic) {
            stream.WriteInt(kv.Key);
            stream.WriteInt(kv.Value);
        }
    }
    internal void ReadState(IReadStream stream) {
        var valueCount = stream.ReadVerifiedInt();
        for (int i = 0; i < valueCount; i++) {
            var id = stream.ReadInt();
            var v = stream.ReadInt();
            if (id < _upperId) {
                ensureArraySize(id);
                _arr[id] = v + 1;
            } else {
                _dic.Add(id, v);
            }
        }
        Count = valueCount;
    }
}
