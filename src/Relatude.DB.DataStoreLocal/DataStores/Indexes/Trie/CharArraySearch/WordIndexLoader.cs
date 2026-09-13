using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Relatude.DB.Common;

namespace Relatude.DB.DataStores.Indexes.Trie.CharArraySearch;

/// <summary>
/// Fills an empty <see cref="CharArrayTrie"/> from a replayed log in bulk, on several threads,
/// instead of one document at a time on the thread that replays the log.
///
/// Indexing a document into the trie costs a descent per word - a scan of the children at every
/// character - and a re-allocation of the hit list per posting. When the state is rebuilt from the
/// log that is done for every document of the store, and it was measured to be about nine tenths
/// of the whole replay. Here the replaying thread only hands each document to a bounded queue.
/// Worker threads take the documents in batches, tokenize them and append their postings to
/// per-word lists in a dictionary of their own, so no word is ever shared between threads while
/// the log is read. When the replay is over, the workers' vocabularies are merged word by word,
/// sorted, and the trie is built from them in a single pass (<see cref="CharArrayTrie.BulkLoad"/>),
/// with every hit list at its final size.
///
/// Order: a document's words may be gathered by any worker in any order, which is harmless for a
/// node that is only ever added. A node that is removed during the load (a delete, or the old
/// version of an update) needs its operations applied in log order, so every operation carries a
/// sequence number: the first add of a node is a plain hit, its removes and any later adds are
/// events with the sequence number, and the merge replays the events of each node in order to
/// decide whether the node ends up in the hit list and with which count. Events are rare - a log
/// is mostly adds - so almost no posting pays for the number.
///
/// Memory: the queue is bounded, so the replaying thread waits for the workers instead of parking
/// the whole log in memory, and only the text of the queued documents is held. The postings are
/// the same as the trie's and are handed over by reference; a word held by several workers is
/// merged into one list. The workers' dictionaries are dropped before the trie is built.
///
/// One loader serves one load: <see cref="Complete"/> ends it.
/// </summary>
internal sealed class WordIndexLoader : IDisposable {
    /// <summary>Worker threads per load. 0 means the default: one less than the processors, at
    /// most 8 - beyond that the merge grows faster than the tokenizing shrinks.</summary>
    public static int WorkerCount { get; set; } = 0;
    static int defaultWorkerCount() => Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
    const int BatchSize = 64; // documents per queue item
    const int MaxQueuedBatches = 32; // the replaying thread waits beyond this: about 2000 documents of text in flight
    /// <summary>Documents gathered on the replaying thread itself before the workers are started.
    /// A store has a word index per text-indexed property, most of them small; threads are only
    /// worth starting for an index that turns out to have volume.</summary>
    const int InlineDocuments = 256;

    readonly record struct Item(int Seq, int NodeId, string Text, bool Remove, bool Versioned);
    readonly record struct DocOp(int Seq, int NodeId, int WordCount); // WordCount -1: the document was removed
    readonly record struct Event(int Seq, int NodeId, byte Hits, bool Remove);

    /// <summary>The hit list of one word while it is being gathered by one worker: plain hits
    /// grow geometrically, events are kept apart with their sequence numbers.</summary>
    sealed class Postings {
        WordHit[] _items = [];
        int _count;
        List<Event>? _events;
        public bool IsEmpty => _count == 0 && _events == null;
        public void Add(int nodeId, byte hits) {
            if (_count == _items.Length) {
                // doubling while small, 1.5x once large: the big lists are where slack would cost memory
                Array.Resize(ref _items, _count == 0 ? 1 : _count < 1024 ? _count * 2 : _count + (_count >> 1));
            }
            _items[_count++] = new(nodeId, hits);
        }
        public void AddEvent(int seq, int nodeId, byte hits, bool remove) => (_events ??= []).Add(new(seq, nodeId, hits, remove));
        /// <summary>The hits of one word from every worker that saw it, as the trie keeps them:
        /// exactly sized, events applied, or null when no hit is left. Releases the sources.</summary>
        public static HitCounts? Merge(List<Postings> sources) {
            var total = 0;
            List<Event>? events = null;
            foreach (var s in sources) {
                total += s._count;
                if (s._events != null) (events ??= []).AddRange(s._events);
            }
            WordHit[] hits;
            if (sources.Count == 1) {
                var s = sources[0];
                hits = s._count == s._items.Length ? s._items : s._items[..s._count];
            } else {
                hits = new WordHit[total];
                var pos = 0;
                foreach (var s in sources) {
                    Array.Copy(s._items, 0, hits, pos, s._count);
                    pos += s._count;
                }
            }
            foreach (var s in sources) {
                s._items = [];
                s._count = 0;
                s._events = null;
            }
            if (events != null) hits = resolve(hits, events);
            return hits.Length == 0 ? null : new HitCounts(hits);
        }
        /// <summary>Replays the events of every node they concern in log order, starting from the
        /// node's plain hit if it has one: a remove takes the node out, an add puts it in with its
        /// count. The plain hit is always the oldest operation, because an add only becomes an
        /// event after a remove, and the remove came after the first add.</summary>
        static WordHit[] resolve(WordHit[] hits, List<Event> events) {
            events.Sort((a, b) => a.NodeId != b.NodeId ? a.NodeId.CompareTo(b.NodeId) : a.Seq.CompareTo(b.Seq));
            var indexOfPlainHit = new Dictionary<int, int>();
            for (var i = 0; i < hits.Length; i++) indexOfPlainHit[hits[i].NodeId] = i;
            var removed = new HashSet<int>();
            var appended = new List<WordHit>();
            for (var i = 0; i < events.Count;) {
                var node = events[i].NodeId;
                var hasPlainHit = indexOfPlainHit.TryGetValue(node, out var index);
                var present = hasPlainHit;
                var count = hasPlainHit ? hits[index].Hits : (byte)0;
                for (; i < events.Count && events[i].NodeId == node; i++) {
                    if (events[i].Remove) present = false;
                    else { present = true; count = events[i].Hits; }
                }
                if (hasPlainHit) {
                    if (present) hits[index] = new(node, count);
                    else removed.Add(node);
                } else if (present) {
                    appended.Add(new(node, count));
                }
            }
            if (removed.Count == 0 && appended.Count == 0) return hits;
            var result = new WordHit[hits.Length - removed.Count + appended.Count];
            var n = 0;
            foreach (var h in hits) if (!removed.Contains(h.NodeId)) result[n++] = h;
            foreach (var h in appended) result[n++] = h;
            return result;
        }
    }

    /// <summary>One thread's share of the load: the words it saw with their postings, and the
    /// documents it saw with their word counts.</summary>
    sealed class Worker {
        public readonly Dictionary<string, Postings> Words = new(StringComparer.Ordinal);
        public readonly List<DocOp> DocOps = [];
        public Exception? Error;
        readonly int _minWordLength;
        readonly int _maxWordLength;
        public Worker(int minWordLength, int maxWordLength) {
            _minWordLength = minWordLength;
            _maxWordLength = maxWordLength;
        }
        public void Run(BlockingCollection<Item[]> queue) {
            foreach (var batch in queue.GetConsumingEnumerable()) {
                foreach (var item in batch) Process(item);
            }
        }
        public void Process(in Item item) {
            // a failing document is skipped and reported at the end; the queue keeps draining,
            // so the replaying thread can never block on a dead worker
            try { process(item); } catch (Exception err) { Error ??= err; }
        }
        void process(in Item item) {
            var entries = IndexUtil.CleanToStrings(item.Text, _minWordLength, _maxWordLength, out var wordCount);
            if (item.Remove) {
                DocOps.Add(new(item.Seq, item.NodeId, -1));
                foreach (var kv in entries) postings(kv.Key).AddEvent(item.Seq, item.NodeId, kv.Value, remove: true);
            } else {
                DocOps.Add(new(item.Seq, item.NodeId, wordCount));
                if (item.Versioned) foreach (var kv in entries) postings(kv.Key).AddEvent(item.Seq, item.NodeId, kv.Value, remove: false);
                else foreach (var kv in entries) postings(kv.Key).Add(item.NodeId, kv.Value);
            }
        }
        Postings postings(string word) {
            ref var p = ref CollectionsMarshal.GetValueRefOrAddDefault(Words, word, out _);
            return p ??= new Postings();
        }
    }

    readonly CharArrayTrie _target;
    readonly Worker[] _workers;
    readonly Thread[] _threads;
    bool _threadsStarted;
    readonly BlockingCollection<Item[]> _queue = new(MaxQueuedBatches);
    Item[] _batch = new Item[BatchSize];
    int _batchCount;
    int _seq;
    readonly HashSet<int> _removedNodes = []; // a later add of one of these is an event, not a plain hit
    bool _completed;

    public WordIndexLoader(CharArrayTrie target) : this(target, WorkerCount) { }
    internal WordIndexLoader(CharArrayTrie target, int workerCount) {
        if (!target.IsEmpty) throw new InvalidOperationException("A bulk load can only fill an empty index. ");
        _target = target;
        if (workerCount <= 0) workerCount = defaultWorkerCount();
        _workers = new Worker[workerCount];
        _threads = new Thread[workerCount];
        for (var i = 0; i < workerCount; i++) _workers[i] = new Worker(target.MinWordLength, target.MaxWordLength);
    }
    void startThreads() {
        _threadsStarted = true;
        for (var i = 0; i < _workers.Length; i++) {
            var worker = _workers[i];
            _threads[i] = new Thread(() => worker.Run(_queue)) { IsBackground = true, Name = "Relatude.DB word index loader " + i };
            _threads[i].Start();
        }
    }
    /// <summary>The document was added: its words go to the hit lists, its word count is kept for BM25.</summary>
    public void Add(int nodeId, string text) {
        ensureOpen();
        if (string.IsNullOrEmpty(text)) return;
        enqueue(new Item(++_seq, nodeId, text, Remove: false, Versioned: _removedNodes.Contains(nodeId)));
    }
    /// <summary>The document was removed (a deleted node, or the old version of an updated one):
    /// its hits are taken out again. A word left without hits is not kept.</summary>
    public void Remove(int nodeId, string text) {
        ensureOpen();
        _removedNodes.Add(nodeId);
        if (string.IsNullOrEmpty(text)) return;
        enqueue(new Item(++_seq, nodeId, text, Remove: true, Versioned: true));
    }
    void ensureOpen() {
        if (_completed) throw new InvalidOperationException("The load is complete. ");
    }
    void enqueue(in Item item) {
        if (!_threadsStarted) {
            // the first documents are gathered right here, into the first worker's share; the
            // threads start once the index has shown it has enough volume to be worth them
            if (_seq <= InlineDocuments) {
                _workers[0].Process(item);
                return;
            }
            startThreads();
        }
        _batch[_batchCount++] = item;
        if (_batchCount == BatchSize) flushBatch();
    }
    void flushBatch() {
        if (_batchCount == 0) return;
        _queue.Add(_batchCount == BatchSize ? _batch : _batch[.._batchCount]); // waits while the queue is full
        _batch = new Item[BatchSize];
        _batchCount = 0;
    }
    /// <summary>Builds the index from everything gathered. The loader is spent afterwards.</summary>
    public void Complete() {
        ensureOpen();
        _completed = true;
        flushBatch();
        _queue.CompleteAdding();
        if (_threadsStarted) foreach (var t in _threads) t.Join();
        foreach (var w in _workers) {
            if (w.Error != null) throw new Exception("Bulk loading the word index failed. " + w.Error.Message, w.Error);
        }
        // documents: the last operation on a node decides whether it is in the index and its word count
        var docs = new Dictionary<int, DocOp>();
        foreach (var w in _workers) {
            foreach (var op in w.DocOps) {
                if (!docs.TryGetValue(op.NodeId, out var current) || current.Seq < op.Seq) docs[op.NodeId] = op;
            }
            w.DocOps.Clear();
        }
        var docWordCounts = docs.Values.Where(d => d.WordCount >= 0).Select(d => new KeyValuePair<int, int>(d.NodeId, d.WordCount)).ToList();
        docs.Clear();
        // words: every worker splits its vocabulary by first character, then the words of each
        // character from all workers are sorted and merged - both steps in parallel. The workers'
        // dictionaries are cleared as they are split, before the build allocates the trie.
        var split = new Dictionary<char, List<(string word, Postings postings)>>[_workers.Length];
        Parallel.For(0, _workers.Length, i => {
            var byChar = split[i] = [];
            foreach (var (word, postings) in _workers[i].Words) {
                if (postings.IsEmpty) continue;
                ref var list = ref CollectionsMarshal.GetValueRefOrAddDefault(byChar, word[0], out _);
                (list ??= []).Add((word, postings));
            }
            _workers[i].Words.Clear();
        });
        var chars = split.SelectMany(d => d.Keys).Distinct().Order().ToArray();
        var buckets = new (string[] words, HitCounts?[] hits, int count)[chars.Length];
        Parallel.For(0, chars.Length, b => buckets[b] = mergeBucket(chars[b], split));
        var count = 0;
        foreach (var b in buckets) count += b.count;
        var words = new string[count];
        var hits = new HitCounts?[count];
        var pos = 0;
        for (var b = 0; b < buckets.Length; b++) {
            Array.Copy(buckets[b].words, 0, words, pos, buckets[b].count);
            Array.Copy(buckets[b].hits, 0, hits, pos, buckets[b].count);
            pos += buckets[b].count;
            buckets[b] = default;
        }
        _target.BulkLoad(words, hits, count, docWordCounts);
    }
    /// <summary>The words starting with <paramref name="c"/> from every worker, sorted, equal words
    /// merged into one hit list, words left without hits dropped.</summary>
    static (string[] words, HitCounts?[] hits, int count) mergeBucket(char c, Dictionary<char, List<(string word, Postings postings)>>[] split) {
        var total = 0;
        foreach (var d in split) if (d.TryGetValue(c, out var list)) total += list.Count;
        var keys = new string[total];
        var values = new Postings[total];
        var n = 0;
        foreach (var d in split) {
            if (!d.TryGetValue(c, out var list)) continue;
            foreach (var (word, postings) in list) {
                keys[n] = word;
                values[n++] = postings;
            }
            list.Clear();
        }
        Array.Sort(keys, values, StringComparer.Ordinal);
        var words = new string[total];
        var hits = new HitCounts?[total];
        var count = 0;
        var same = new List<Postings>();
        for (var i = 0; i < total;) {
            var j = i + 1;
            while (j < total && keys[j] == keys[i]) j++;
            same.Clear();
            for (var k = i; k < j; k++) same.Add(values[k]);
            var merged = Postings.Merge(same);
            if (merged != null) {
                words[count] = keys[i];
                hits[count++] = merged;
            }
            i = j;
        }
        return (words, hits, count);
    }
    /// <summary>Drops a load that will not be completed: the workers stop and nothing is built.</summary>
    public void Dispose() {
        if (_completed) return;
        _completed = true;
        _queue.CompleteAdding();
        if (_threadsStarted) foreach (var t in _threads) t.Join();
    }
}
