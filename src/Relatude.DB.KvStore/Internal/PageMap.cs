using System.Runtime.CompilerServices;

namespace Relatude.DB.Datastores.Indexes.BTreeIndex.Internal;

/// <summary>
/// The write transaction's pageId â†’ page map: open addressing with linear probing and a
/// Fibonacci-mixed hash. It is probed several times per index operation (every page read inside
/// a transaction checks it before the committed store), which is exactly the lookup a generic
/// dictionary spends most of its time around rather than in. Single-threaded by contract — it
/// lives and dies with one transaction on one thread.
/// Keys are stored as pageId + 1 so 0 can mean "empty slot" (page ids 0 and 1 are the meta pages
/// and never appear here, but the map does not rely on that).
/// </summary>
internal sealed class PageMap
{
    private uint[] _keys;       // pageId + 1; 0 = empty
    private byte[]?[] _values;
    private int _count;
    private int _shift;         // 32 - log2(capacity): Fibonacci hash to slot

    public PageMap(int initialCapacity = 1024)
    {
        int cap = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, initialCapacity));
        _keys = new uint[cap];
        _values = new byte[]?[cap];
        _shift = 32 - System.Numerics.BitOperations.Log2((uint)cap);
    }

    public int Count => _count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Slot(uint pageId) => (int)((pageId + 1) * 2654435769u >> _shift);

    public bool TryGetValue(uint pageId, out byte[] page)
    {
        uint[] keys = _keys;
        uint key = pageId + 1;
        int mask = keys.Length - 1;
        for (int i = Slot(pageId); ; i = (i + 1) & mask)
        {
            uint k = keys[i];
            if (k == key)
            {
                page = _values[i]!;
                return true;
            }
            if (k == 0)
            {
                page = null!;
                return false;
            }
        }
    }

    /// <summary>Adds or replaces the mapping for <paramref name="pageId"/>.</summary>
    public void Set(uint pageId, byte[] page)
    {
        if (_count >= _keys.Length - (_keys.Length >> 2)) // grow at 75%: probes stay short
            Grow();
        uint[] keys = _keys;
        uint key = pageId + 1;
        int mask = keys.Length - 1;
        for (int i = Slot(pageId); ; i = (i + 1) & mask)
        {
            uint k = keys[i];
            if (k == 0)
            {
                keys[i] = key;
                _values[i] = page;
                _count++;
                return;
            }
            if (k == key)
            {
                _values[i] = page;
                return;
            }
        }
    }

    /// <summary>Removes the mapping; returns false if absent. Backward-shift deletion keeps probe chains intact without tombstones.</summary>
    public bool Remove(uint pageId)
    {
        uint[] keys = _keys;
        uint key = pageId + 1;
        int mask = keys.Length - 1;
        int i = Slot(pageId);
        while (true)
        {
            uint k = keys[i];
            if (k == 0)
                return false;
            if (k == key)
                break;
            i = (i + 1) & mask;
        }
        // Backward-shift: pull every displaced follower into the hole so probes never break.
        int hole = i;
        int j = (i + 1) & mask;
        while (keys[j] != 0)
        {
            int home = Slot(keys[j] - 1);
            // j's entry may move into the hole only if the hole does not lie "before" its home
            // slot along the probe path (standard Robin-Hood/backward-shift condition).
            bool between = hole <= j
                ? home <= hole || home > j
                : home <= hole && home > j;
            if (between)
            {
                keys[hole] = keys[j];
                _values[hole] = _values[j];
                hole = j;
            }
            j = (j + 1) & mask;
        }
        keys[hole] = 0;
        _values[hole] = null;
        _count--;
        return true;
    }

    private void Grow()
    {
        uint[] oldKeys = _keys;
        byte[]?[] oldValues = _values;
        int newCap = oldKeys.Length * 2;
        _keys = new uint[newCap];
        _values = new byte[]?[newCap];
        _shift = 32 - System.Numerics.BitOperations.Log2((uint)newCap);
        _count = 0;
        for (int i = 0; i < oldKeys.Length; i++)
        {
            if (oldKeys[i] != 0)
                Set(oldKeys[i] - 1, oldValues[i]!);
        }
    }

    /// <summary>Every mapping, as an array (the shape the pager's write path wants anyway).</summary>
    public KeyValuePair<uint, byte[]>[] ToArray()
    {
        var result = new KeyValuePair<uint, byte[]>[_count];
        int w = 0;
        uint[] keys = _keys;
        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i] != 0)
                result[w++] = new KeyValuePair<uint, byte[]>(keys[i] - 1, _values[i]!);
        }
        return result;
    }

    public IEnumerable<uint> Keys
    {
        get
        {
            uint[] keys = _keys;
            for (int i = 0; i < keys.Length; i++)
            {
                if (keys[i] != 0)
                    yield return keys[i] - 1;
            }
        }
    }
}
