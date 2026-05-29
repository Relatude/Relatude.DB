using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Xml.Serialization;

namespace Relatude.DB.FileConversion;

internal class RunningConversions {
    class orderedDictionary<K, V> : IDictionary<K, V> where K : notnull {
        private readonly IDictionary<K, LinkedListNode<KeyValuePair<K, V>>> m_Dictionary;
        private readonly LinkedList<KeyValuePair<K, V>> m_LinkedList;
        public orderedDictionary() {
            m_Dictionary = new Dictionary<K, LinkedListNode<KeyValuePair<K, V>>>();
            m_LinkedList = new LinkedList<KeyValuePair<K, V>>();
        }

        public V this[K key] {
            get { return m_Dictionary[key].Value.Value; }
            set { Add(key, value); }
        }

        public int Count => m_Dictionary.Count;
        public virtual bool IsReadOnly => m_Dictionary.IsReadOnly;
        public ICollection<K> Keys => m_Dictionary.Keys;
        public ICollection<V> Values => m_Dictionary.Values.Select(node => node.Value.Value).ToList(); // not efficient
        public bool Add(K item, V value) {
            if (m_Dictionary.ContainsKey(item)) return false;
            var node = m_LinkedList.AddLast(new KeyValuePair<K, V>(item, value));
            m_Dictionary.Add(item, node);
            return true;
        }
        public void Add(KeyValuePair<K, V> item) {
            Add(item.Key, item.Value);
        }
        public void Clear() {
            m_LinkedList.Clear();
            m_Dictionary.Clear();
        }
        public bool Contains(KeyValuePair<K, V> item) => m_Dictionary.TryGetValue(item.Key, out var node) && EqualityComparer<V>.Default.Equals(node.Value.Value, item.Value);
        public bool ContainsKey(K key) => m_Dictionary.ContainsKey(key);
        public void CopyTo(KeyValuePair<K, V>[] array, int arrayIndex) {
            foreach (var kvp in m_LinkedList) {
                if (arrayIndex >= array.Length) break;
                array[arrayIndex++] = kvp;
            }
        }
        public IEnumerator<KeyValuePair<K, V>> GetEnumerator() => m_LinkedList.GetEnumerator();
        public bool Remove(K item) {
            if (m_Dictionary.TryGetValue(item, out var node)) {
                m_Dictionary.Remove(item);
                m_LinkedList.Remove(node);
            }
            return true;
        }

        public bool Remove(KeyValuePair<K, V> item) {
            if (m_Dictionary.TryGetValue(item.Key, out var node) && EqualityComparer<V>.Default.Equals(node.Value.Value, item.Value)) {
                m_Dictionary.Remove(item.Key);
                m_LinkedList.Remove(node);
                return true;
            }
            return false;
        }

        public bool TryGetValue(K key, [MaybeNullWhen(false)] out V value) {
            if (m_Dictionary.TryGetValue(key, out var node)) {
                value = node.Value.Value;
                return true;
            }
            value = default;
            return false;
        }
        void IDictionary<K, V>.Add(K key, V value) {
            Add(key, value);
        }

        IEnumerator IEnumerable.GetEnumerator() {
            return GetEnumerator();
        }
    }
    readonly orderedDictionary<Guid, ProgressEntry> _conversions = [];
    readonly HashSet<Guid> _doingWorkOnEntry = [];
    public void AddIfMissing(Guid key, Func<ProgressEntry> createEntry) {
        lock (_conversions) {
            if (_conversions.ContainsKey(key)) return;
            var entry = createEntry();
            _conversions.Add(key, entry);
        }
    }
    public bool TryGet(Guid key, [MaybeNullWhen(false)] out ProgressEntry entry) {
        lock (_conversions) {
            return _conversions.TryGetValue(key, out entry);
        }
    }
    public bool TryGetWorkIfNotAlreadyWorkingOnEntryOrConverterTooBusy([MaybeNullWhen(false)] out ProgressEntry entry, Func<ProgressEntry, bool> tryReserveWork) {
        lock (_conversions) {
            if (_conversions.Count == 0) {
                entry = null;
                return false;
            }
            // look for next entry that is allowed to start
            foreach (var potentialKey in _conversions.Keys) {
                if (_doingWorkOnEntry.Contains(potentialKey)) continue; // already working on this entry
                if (!_conversions.TryGetValue(potentialKey, out var potentialEntry)) {
                    // this should not happen, but if it does, just skip this key
                    continue;
                }
                if (tryReserveWork(potentialEntry)) {
                    _doingWorkOnEntry.Add(potentialKey);
                    entry = potentialEntry;
                    return true;
                } else {
                    // worker too busy, move to next
                }
            }
            entry = null;
            return false;
        }
    }
    public void UpdateIfExists(ProgressEntry entry) {
        lock (_conversions) {
            var key = entry.FileInfo.IdWithAdjustment.GetKey();
            if (_conversions.ContainsKey(key)) {
                _conversions[key] = entry;
            }
        }
    }
    public void Remove(ProgressEntry entry) {
        lock (_conversions) {
            var key = entry.FileInfo.IdWithAdjustment.GetKey();
            _conversions.Remove(key);
            _doingWorkOnEntry.Remove(key);
        }
    }
    public int Count {
        get {
            lock (_conversions) {
                return _conversions.Count;
            }
        }
    }
    public void RemoveByKey(Guid conversionId) {
        lock (_conversions) {
            _conversions.Remove(conversionId);
            _doingWorkOnEntry.Remove(conversionId);
        }
    }
    public void ClearAll() {
        lock (_conversions) {
            _conversions.Clear();
            _doingWorkOnEntry.Clear();
        }
    }
    public ConversionInfo[] GetAll() {
        lock (_conversions) {
            return [.. _conversions.Values.Select(entry => new ConversionInfo(entry))];
        }
    }
    internal void RegisterNotDoingWorkOnEntry(ProgressEntry entry) {
        lock (_conversions) {
            var key = entry.FileInfo.IdWithAdjustment.GetKey();
            _doingWorkOnEntry.Remove(key);
        }
    }
}
