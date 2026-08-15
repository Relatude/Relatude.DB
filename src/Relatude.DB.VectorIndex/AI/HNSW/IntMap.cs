namespace Relatude.DB.AI.HNSW;

/// <summary>
/// An open-addressing int → int map, replacing a <c>Dictionary&lt;int, int&gt;</c> where one entry
/// per vector lives for the index's whole lifetime: two flat arrays at load factor one half cost
/// about 16 bytes per entry against the dictionary's ~30, which is the difference between the two
/// biggest always-resident structures of a large index. Linear probing with a Fibonacci hash;
/// deletion is by backward shift, so there are no tombstones to accumulate under the index's
/// update-heavy workloads (every upsert of an existing node is a remove and an add).
///
/// <para>One key is special: <see cref="int.MinValue"/> marks an empty slot, so an entry with that
/// exact key is carried in a side field instead of the table. No caller of this index produces it —
/// node ids are positive — but a map that silently corrupted on one specific key would be a trap
/// left for whoever changes that.</para>
/// </summary>
internal sealed class IntMap {
    const int Empty = int.MinValue;

    int[] _keys;
    int[] _values;
    int _count;      // entries in the table (excludes the sentinel-key entry)
    bool _hasMin;    // whether the key int.MinValue itself is present
    int _minValue;

    public IntMap(int capacity = 0) {
        var size = 16;
        while (size < capacity * 2) size *= 2;
        _keys = new int[size];
        _values = new int[size];
        Array.Fill(_keys, Empty);
    }

    public int Count => _count + (_hasMin ? 1 : 0);

    public void EnsureCapacity(int entries) {
        if (entries * 2 > _keys.Length) grow(entries);
    }

    public bool TryGetValue(int key, out int value) {
        if (key == Empty) {
            value = _minValue;
            return _hasMin;
        }
        var mask = _keys.Length - 1;
        var i = indexOf(key, mask);
        while (true) {
            var k = _keys[i];
            if (k == key) {
                value = _values[i];
                return true;
            }
            if (k == Empty) {
                value = 0;
                return false;
            }
            i = (i + 1) & mask;
        }
    }

    /// <summary>Adds the key or overwrites its value.</summary>
    public int this[int key] {
        set {
            if (key == Empty) {
                _hasMin = true;
                _minValue = value;
                return;
            }
            var mask = _keys.Length - 1;
            var i = indexOf(key, mask);
            while (true) {
                var k = _keys[i];
                if (k == key) {
                    _values[i] = value;
                    return;
                }
                if (k == Empty) break;
                i = (i + 1) & mask;
            }
            _keys[i] = key;
            _values[i] = value;
            if (++_count * 2 > _keys.Length) grow(_count);
        }
    }

    public bool Remove(int key) {
        if (key == Empty) {
            var had = _hasMin;
            _hasMin = false;
            return had;
        }
        var mask = _keys.Length - 1;
        var i = indexOf(key, mask);
        while (true) {
            var k = _keys[i];
            if (k == Empty) return false;
            if (k == key) break;
            i = (i + 1) & mask;
        }
        // Backward shift: pull each later entry of the probe cluster into the hole when the hole
        // does not sit before the entry's own ideal slot — the invariant linear probing needs.
        var hole = i;
        var j = i;
        while (true) {
            j = (j + 1) & mask;
            var k = _keys[j];
            if (k == Empty) break;
            var ideal = indexOf(k, mask);
            if (((j - ideal) & mask) >= ((j - hole) & mask)) {
                _keys[hole] = k;
                _values[hole] = _values[j];
                hole = j;
            }
        }
        _keys[hole] = Empty;
        _count--;
        return true;
    }

    static int indexOf(int key, int mask) => (int)((uint)(key * -1640531527) >> 1) & mask;

    void grow(int entries) {
        var oldKeys = _keys;
        var oldValues = _values;
        var size = oldKeys.Length;
        while (size < entries * 2) size *= 2;
        if (size == oldKeys.Length) size *= 2;
        _keys = new int[size];
        _values = new int[size];
        Array.Fill(_keys, Empty);
        _count = 0;
        for (var i = 0; i < oldKeys.Length; i++) {
            if (oldKeys[i] != Empty) this[oldKeys[i]] = oldValues[i];
        }
    }
}
