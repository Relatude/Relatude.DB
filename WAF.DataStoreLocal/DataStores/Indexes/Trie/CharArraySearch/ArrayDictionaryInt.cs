using WAF.IO;

namespace WAF.DataStores.Indexes.Trie.CharArraySearch;
// Simple class that uses an array for faster lookups if id is smaller than upper ID.
// the value 0 is not legal, as 0 is used to indicate no record in array
// the actual countvalue in the array is v - 1
// [0] is never used
internal class ArrayDictionaryInt {
    const int _upperId = 1024 * 500; // use array for first 500 000 mem use is only 4mb ( Could be linked to max id )
    int[] _arr = new int[_upperId];
    Dictionary<int, int> _dic = [];
    public int Count;
    public int this[int id] {
        get {
            if (id < _upperId) {
                var v = _arr[id];
                if (v == 0) throw new Exception(id + " is not a known record. ");
                return v - 1;
            }
            return _dic[id];
        }
    }
    public void Add(int id, int v) {
        if (id < _upperId) {
            if (_arr[id] != 0) throw new Exception(id + " is already a known record. ");
            _arr[id] = v + 1;
        } else _dic.Add(id, v);
        Count++;
    }
    public void Remove(int id) {
        if (id < _upperId) {
            if (_arr[id] == 0) throw new Exception(id + " is not a known record. ");
            _arr[id] = 0;
        } else {
            _dic.Remove(id);
        }
        Count--;
    }
    public bool ContainsKey(int id) => id < _upperId ? _arr[id] != 0 : _dic.ContainsKey(id);
    internal void WriteState(IAppendStream stream) {
        stream.WriteVerifiedInt(Count);
        int i = 0;
        for (int id = 0; id < _upperId; id++) {
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
                _arr[id] = v + 1;
            } else {
                _dic.Add(id, v);
            }
        }
        Count = valueCount;
    }
}
