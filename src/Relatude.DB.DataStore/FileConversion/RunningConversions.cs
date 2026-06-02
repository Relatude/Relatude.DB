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
    class history<T>(TimeSpan maxAge, int maxCount) {
        class itemWithTime(T item) {
            public T Item { get; set; } = item;
            public DateTime Time { get; } = DateTime.UtcNow;
        }
        readonly LinkedList<itemWithTime> _items = [];
        public void Add(T item) {
            RemoveExpired();
            _items.AddLast(new itemWithTime(item));
        }
        public void RemoveExpired() {
            var cutoff = DateTime.UtcNow - maxAge;
            while (_items.First is { } first && first.Value.Time < cutoff) _items.RemoveFirst();
            while (_items.Count >= maxCount) _items.RemoveFirst();
        }
        public IEnumerable<T> GetAll() {
            RemoveExpired();
            foreach (var item in _items) {
                yield return item.Item;
            }
        }
    }
    readonly orderedDictionary<Guid, ProgressEntry> _conversions = [];
    readonly HashSet<Guid> _doingWorkOnEntry = [];
    readonly history<FileConversion> _history = new(TimeSpan.FromSeconds(60), 1000);
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
    void addCounts(ConversionStatus status) {
        switch (status) {
            case ConversionStatus.Completed: _completed++; break;
            case ConversionStatus.Failed: _failed++; break;
            case ConversionStatus.Canceled: _canceled++; break;
            default: break;
        }
    }
    public void Remove(ProgressEntry entry, ConversionStatus reason, string? desc) {
        lock (_conversions) {
            var key = entry.FileInfo.IdWithAdjustment.GetKey();
            _conversions.Remove(key);
            _doingWorkOnEntry.Remove(key);
            addCounts(reason);
            _history.Add(new(entry, reason, desc));
        }
    }
    public int Count {
        get {
            lock (_conversions) {
                return _conversions.Count;
            }
        }
    }
    int _completed = 0;
    int _failed = 0;
    int _canceled = 0;

    public int Completed {
        get {
            lock (_conversions) {
                return _completed;
            }
        }
    }
    public int Failed {
        get {
            lock (_conversions) {
                return _failed;
            }
        }
    }
    public int Canceled {
        get {
            lock (_conversions) {
                return _canceled;
            }
        }
    }

    public void RemoveByKey(Guid conversionId, ConversionStatus reason, string? desc) {
        lock (_conversions) {
            if (_conversions.TryGetValue(conversionId, out var entry)) {
                _conversions.Remove(conversionId);
                addCounts(reason);
                _history.Add(new(entry, reason, desc));
            }
            _doingWorkOnEntry.Remove(conversionId);
        }
    }
    public void ClearAll() {
        lock (_conversions) {
            _conversions.Clear();
            _doingWorkOnEntry.Clear();
        }
    }
    public FileConversion[] GetAll() {
        lock (_conversions) {
            LinkedList<FileConversion> result = [];
            foreach (var conversion in _history.GetAll()) {
                result.AddFirst(conversion);
            }
            foreach (var conversion in _conversions) {
                var inWork = _doingWorkOnEntry.Contains(conversion.Key);
                var status = inWork ? ConversionStatus.Running : ConversionStatus.Queued;
                result.AddFirst(new FileConversion(conversion.Value, status, conversion.Value.ProgressInfo.Message));
            }
            return [.. result];
        }
    }
    internal void RegisterNotDoingWorkOnEntry(ProgressEntry entry) {
        lock (_conversions) {
            var key = entry.FileInfo.IdWithAdjustment.GetKey();
            _doingWorkOnEntry.Remove(key);
        }
    }

    internal void RemoveExpired() {
        lock (_conversions) {
            _history.RemoveExpired();
        }
    }
}
