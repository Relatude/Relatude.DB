using System.Diagnostics;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.History;

/// <summary>A log file the history is to be read from, as the files on disk and the settings say now.</summary>
internal sealed record PlannedSource(string Name, string[] Key, bool Secondary, Guid FileId, long Format, int Number, IHistoryLogFile File, bool CopyPrefix, long SkipFrom);

/// <summary>
/// The history reader: follows the log a few seconds behind, in a background thread of its own, and keeps the time
/// index of it in <c>indexes/history</c> (<see cref="HistoryFiles"/>). Nothing on the write path: it reads what the
/// log gained since its last run, through the log's own streams a chunk at a time (the file in use) or by opening a
/// file for each chunk (the others), heads and meta only, and writes its own files only while it writes a batch.
/// The secondary log is the source while it is on, the numbered log files before it for what came before; else the
/// numbered log files kept. The index only ever reflects the log files there are: a file deleted takes its rows with
/// it, a revert cuts them, a backup put back or anything that does not add up has that file read again.
/// </summary>
internal sealed partial class LogHistory : IDisposable {
    readonly DataStoreLocal _store;
    readonly object _runLock = new();
    readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);
    // the index while recording is on; all null while it is off
    HistoryStateFile? _state;
    HistoryTable? _types, _writers, _cultures, _collections, _relations;
    readonly Dictionary<string, (TimestampList changes, TimestampList links)> _ats = [];
    // the node behind each internal id, as the log said it last: relation actions name nodes by internal ids,
    // which are reused, so the reader keeps what it saw (the database's own map is asked for the rest)
    readonly Dictionary<int, (Guid id, int type)> _ids = [];
    const int maxIds = 4_000_000;
    readonly Dictionary<string, (long size, (long version, Guid fileId)? header)> _headers = [];
    // the reader thread
    Thread? _thread;
    volatile bool _stopping;
    readonly AutoResetEvent _wake = new(false);
    CancellationTokenSource _runCts = new();
    // status
    volatile string? _problem;
    DateTime? _lastRunUtc;
    long _bytesBehind, _bytesTotal;
    // a revert's cut, for the next run to apply: the file it cut, where, and whether the secondary log was cut alike
    (string key, long keepEnd, bool secondaryAlike)? _cut;
    // the type rules, compiled
    bool[]? _rules;
    (Datamodel model, bool byDefault)? _rulesFor;
    Dictionary<Guid, bool>? _resolved;
    HashSet<Guid>? _ignored;
    Datamodel? _ignoredFor;

    const long headerLength = LogFileScan.HeaderLength; // where the first transaction of a log file starts
    const long buildingThreshold = 2 * 1024 * 1024; // behind more than this is building, and paced
    const int batchRows = 4096;
    static readonly TimeSpan batchTime = TimeSpan.FromSeconds(1);
    static readonly TimeSpan idle = TimeSpan.FromSeconds(2);

    public LogHistory(DataStoreLocal store) {
        _store = store;
    }

    LogHistorySettings settings => _store.Settings.History ?? new LogHistorySettings();
    HistoryFiles files => new(_store.IOIndex);

    // ------------------------------------------------------------------ the thread

    /// <summary>Starts the reader once the store is open. It runs whether or not recording is on, and does nothing while it is off.</summary>
    public void Start() {
        lock (_runLock) {
            if (_thread != null) return;
            _stopping = false;
            if (_runCts.IsCancellationRequested) _runCts = new();
            _thread = new Thread(loop) { IsBackground = true, Name = "Relatude.DB history reader", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
    }
    /// <summary>Stops the reader: a run under way is cancelled at its next chunk, with no final flush (what it read last is read again).</summary>
    public void Stop() {
        Thread? thread;
        lock (_runLock) {
            thread = _thread;
            _thread = null;
            _stopping = true;
        }
        _runCts.Cancel();
        _wake.Set();
        thread?.Join(TimeSpan.FromSeconds(30));
    }
    /// <summary>Asks for a run now rather than at the next tick.</summary>
    public void Wake() => _wake.Set();
    void loop() {
        while (!_stopping) {
            var behind = false;
            try {
                behind = runOnce(_runCts.Token);
            } catch { }
            if (_stopping) break;
            _wake.WaitOne(behind ? TimeSpan.FromMilliseconds(10) : idle);
        }
    }
    /// <summary>Reads everything the log holds that the index has not, on the calling thread (tests, the rebuild job). Waits for a run under way.</summary>
    public void UpdateNow() {
        while (runOnce(CancellationToken.None)) { }
    }

    // ------------------------------------------------------------------ a run

    // reads on from where it stopped, in every source; answers whether it stopped before the end (to come back soon)
    bool runOnce(CancellationToken cancel) {
        lock (_runLock) {
            if (!settings.Enabled) {
                unload();
                return false;
            }
            if (_store.State != DataStoreState.Open || _store.IsBusyWithLogFiles) return false;
            var stoppedEarly = false;
            try {
                ensureLoaded();
                var plan = planSources();
                reconcile(plan);
                long behind = 0, total = 0;
                for (var i = 0; i < plan.Count && i < _state!.Sources.Count; i++) {
                    if (cancel.IsCancellationRequested) return false;
                    var finished = readSource(i, plan[i], cancel, out var length);
                    stoppedEarly |= !finished;
                    total += Math.Max(0, length - headerLength);
                    behind += Math.Max(0, length - _state.Sources[i].ReadPos);
                }
                _bytesBehind = behind;
                _bytesTotal = total;
                _problem = null;
            } catch (OperationCanceledException) {
                return false;
            } catch (ObjectDisposedException) {
                return false; // the store is closing or reloading
            } catch (Exception err) {
                if (_problem != err.Message) _store.LogWarning("The history reader stopped: " + err.Message, err.ToString());
                _problem = err.Message;
                return false;
            } finally {
                _lastRunUtc = DateTime.UtcNow;
            }
            return stoppedEarly && !cancel.IsCancellationRequested;
        }
    }

    /// <summary>The log files to read, in order: the numbered ones kept, or those before the secondary log and then it.</summary>
    List<PlannedSource> planSources() {
        var wal = _store._wal;
        var io = _store.IO;
        var numbered = new List<(string[] key, int number, long version, Guid fileId, IHistoryLogFile file)>();
        foreach (var key in FileKeyUtility.WAL_GetAllFileKeys(io).OrderBy(k => k.AsKeyString(), StringComparer.OrdinalIgnoreCase)) {
            var parts = key.FileName().Split('.');
            if (parts.Length < 3 || !int.TryParse(parts[1], out var number)) continue;
            var active = key.IsSameKey(wal.FileKey);
            IHistoryLogFile file = active ? new ActiveLogFile(() => _store._wal, false) : new ClosedLogFile(io, key);
            (long version, Guid fileId)? header = active ? (wal.FormatVersion, wal.FileId) : headerOf(io, key, file);
            if (header == null) continue;
            numbered.Add((key, number, header.Value.version, header.Value.fileId, file));
        }
        var plan = new List<PlannedSource>();
        PlannedSource? secondary = null;
        if (_store.Settings.SecondaryBackupLog) {
            var key = FileKeyUtility.WAL_GetSecondaryFileKey();
            IHistoryLogFile? file = null;
            (long version, Guid fileId)? header = null;
            if (wal.HasSecondaryStream) {
                file = new ActiveLogFile(() => _store._wal, true);
                header = (wal.SecondaryFormatVersion, wal.SecondaryFileId);
            } else if (_store.IOLog2.Exists(key)) {
                file = new ClosedLogFile(_store.IOLog2, key);
                header = headerOf(_store.IOLog2, key, file);
            }
            var first = file == null ? 0 : LogTransactions.TimestampAt(file, headerLength);
            if (file != null && header != null && first > 0) {
                var origin = numbered.FindIndex(n => n.fileId == header.Value.fileId);
                var before = origin >= 0 ? numbered.Take(origin).ToList() : numbered;
                foreach (var n in before) plan.Add(numberedSource(n, first));
                // a secondary log starts as a copy of the log file in use when it was turned on: a truncation's copy of
                // every node when that file was made by one, which an older format does not mark
                var copyPrefix = header.Value.version < WALFile._logVersioNumber && !(origin >= 0 && numbered[origin].number == 1);
                secondary = new PlannedSource("secondary", key, true, header.Value.fileId, header.Value.version, 0, file, copyPrefix, 0);
            }
        }
        if (secondary == null) {
            foreach (var n in numbered) plan.Add(numberedSource(n, 0));
        } else {
            plan.Add(secondary);
        }
        return plan;
    }
    static PlannedSource numberedSource((string[] key, int number, long version, Guid fileId, IHistoryLogFile file) n, long skipFrom)
        // a numbered file after the first was made by a truncation, starting with a copy of every node: format 1002 marks it
        => new(n.number.ToString("00000000"), n.key, false, n.fileId, n.version, n.number, n.file, n.version < WALFile._logVersioNumber && n.number > 1, skipFrom);
    (long version, Guid fileId)? headerOf(IIOProvider io, string[] key, IHistoryLogFile file) {
        var size = io.GetFileSizeOrZeroIfUnknown(key);
        var name = key.AsKeyString();
        if (_headers.TryGetValue(name, out var known) && known.size == size) return known.header;
        var header = LogTransactions.Header(file);
        _headers[name] = (size, header);
        return header;
    }

    /// <summary>Brings the sources read in line with the plan: a file gone takes its rows, one replaced is read again, a new one is added.</summary>
    void reconcile(List<PlannedSource> plan) {
        var sources = _state!.Sources;
        var changed = false;
        static bool same(PlannedSource p, HistorySourceState s) => p.Name == s.Name && p.FileId == s.FileId && p.Secondary == s.Secondary && p.SkipFrom == s.SkipFrom;
        foreach (var s in sources.ToList()) {
            if (plan.Any(p => same(p, s))) continue;
            dropSource(s);
            changed = true;
        }
        var k = 0;
        while (k < sources.Count && k < plan.Count && same(plan[k], sources[k])) k++;
        for (var i = sources.Count - 1; i >= k; i--) {
            dropSource(sources[i]);
            changed = true;
        }
        for (var i = k; i < plan.Count; i++) {
            var p = plan[i];
            _lock.EnterWriteLock();
            try {
                sources.Add(new HistorySourceState {
                    Name = p.Name, Key = p.Key.AsKeyString(), Secondary = p.Secondary, FileId = p.FileId, Format = p.Format,
                    ReadPos = headerLength, CopyPrefix = p.CopyPrefix, StartsWithCopy = p.CopyPrefix, SkipFrom = p.SkipFrom,
                    MaxTs = i > 0 ? sources[i - 1].MaxTs : 0,
                });
                _ats[p.Name] = (new(), new());
            } finally {
                _lock.ExitWriteLock();
            }
            changed = true;
        }
        if (changed) saveState();
    }
    void dropSource(HistorySourceState s) {
        _lock.EnterWriteLock();
        try {
            files.DeleteSource(s.Name);
            _state!.Sources.Remove(s);
            _ats.Remove(s.Name);
        } finally {
            _lock.ExitWriteLock();
        }
    }

    // ------------------------------------------------------------------ reading a source

    sealed class Batch(HistorySourceState s) {
        public readonly List<ChangeRow> Rows = [];
        public readonly List<LinkRow> Links = [];
        public long MaxTs = s.MaxTs;
        public bool CopyPrefix = s.CopyPrefix;
        public long LastTxPos = s.LastTxPos;
        public long LastTxTs = s.LastTxTs;
    }

    /// <summary>Reads one source on from where it stopped. False when it stopped before the end (cancelled, paced, busy).</summary>
    bool readSource(int index, PlannedSource planned, CancellationToken cancel, out long length) {
        var s = _state!.Sources[index];
        length = planned.File.Length();
        if (length < 0) return true;
        s.Length = length;
        // where it stopped must still be what it read: a revert cuts the file, a file put back is another
        if (s.ReadPos > length || (s.LastTxPos > 0 && LogTransactions.TimestampAt(planned.File, s.LastTxPos) != s.LastTxTs)) {
            if (_cut is { } cut && (s.Secondary ? cut.secondaryAlike : cut.key == s.Key) && cut.keepEnd <= s.ReadPos) {
                cutSource(index, cut.keepEnd);
            } else {
                restartFrom(index);
            }
            s = _state.Sources[index];
        }
        if (_cut is { } done && (s.Secondary || done.key == s.Key)) _cut = null;
        if (s.ReadPos >= length) return true;
        var window = new LogWindow(planned.File, length);
        var batch = new Batch(s);
        var pos = s.ReadPos;
        var since = Stopwatch.StartNew();
        var work = Stopwatch.StartNew();
        var ignored = ignoredProperties();
        var building = length - pos > buildingThreshold;
        while (true) {
            if (cancel.IsCancellationRequested || _store.IsBusyWithLogFiles) {
                flush(index, batch, pos);
                return false;
            }
            var result = LogTransactions.Read(window, pos, s.Format, ignored, out var tx, out var next);
            if (result == ParseResult.Incomplete) break;
            if (result == ParseResult.Damaged) {
                pos = Math.Max(next, pos + 1);
                batch.LastTxPos = 0; // nothing to check against
                continue;
            }
            take(tx!, planned, batch);
            pos = tx!.End;
            batch.LastTxPos = tx.Start;
            batch.LastTxTs = tx.Timestamp;
            if (batch.Rows.Count + batch.Links.Count >= batchRows || since.Elapsed >= batchTime) {
                flush(index, batch, pos);
                since.Restart();
                if (building && !pace(work, cancel)) return false;
            }
        }
        flush(index, batch, pos);
        return true;
    }
    // the first build is held to a share of one core and of the disk: after each batch, a rest in proportion
    bool pace(Stopwatch work, CancellationToken cancel) {
        var share = settings.BuildSpeed switch { LogHistoryBuildSpeed.High => 1.0, LogHistoryBuildSpeed.Medium => 0.5, _ => 0.25 };
        var rest = TimeSpan.FromTicks((long)(work.Elapsed.Ticks * (1 / share - 1)));
        if (rest > TimeSpan.FromSeconds(4)) rest = TimeSpan.FromSeconds(4);
        work.Restart();
        if (rest <= TimeSpan.Zero) return true;
        cancel.WaitHandle.WaitOne(rest);
        work.Restart();
        return !cancel.IsCancellationRequested;
    }
    void flush(int index, Batch batch, long pos) {
        var s = _state!.Sources[index];
        if (batch.Rows.Count == 0 && batch.Links.Count == 0 && pos == s.ReadPos) return;
        _lock.EnterWriteLock();
        try {
            var f = files;
            if (batch.Rows.Count > 0) {
                var bytes = new byte[batch.Rows.Count * ChangeRow.Size];
                for (var i = 0; i < batch.Rows.Count; i++) batch.Rows[i].Write(bytes.AsSpan(i * ChangeRow.Size));
                f.Append(HistoryFiles.ChangesKey(s.Name), bytes, bytes.Length);
            }
            if (batch.Links.Count > 0) {
                var bytes = new byte[batch.Links.Count * LinkRow.Size];
                for (var i = 0; i < batch.Links.Count; i++) batch.Links[i].Write(bytes.AsSpan(i * LinkRow.Size));
                f.Append(HistoryFiles.LinksKey(s.Name), bytes, bytes.Length);
            }
            var (changes, links) = atsOf(s.Name);
            foreach (var r in batch.Rows) changes.Add(r.At);
            foreach (var l in batch.Links) links.Add(l.At);
            s.Rows += batch.Rows.Count;
            s.Links += batch.Links.Count;
            s.ReadPos = pos;
            s.MaxTs = batch.MaxTs;
            s.CopyPrefix = batch.CopyPrefix;
            s.LastTxPos = batch.LastTxPos;
            s.LastTxTs = batch.LastTxTs;
            // the sources after it carry on from it
            for (var i = index + 1; i < _state.Sources.Count; i++) if (_state.Sources[i].MaxTs < s.MaxTs && _state.Sources[i].ReadPos <= headerLength) _state.Sources[i].MaxTs = s.MaxTs;
            saveState();
        } finally {
            _lock.ExitWriteLock();
        }
        batch.Rows.Clear();
        batch.Links.Clear();
    }
    (TimestampList changes, TimestampList links) atsOf(string name) {
        if (!_ats.TryGetValue(name, out var a)) _ats[name] = a = (new(), new());
        return a;
    }

    /// <summary>Takes one transaction: what it changed, unless it is a copy, a transaction another source gives, or one read before.</summary>
    void take(LoggedTransaction tx, PlannedSource planned, Batch batch) {
        // the nodes behind the internal ids, from every transaction, copies too: they are what relations name
        foreach (var a in tx.Actions) {
            if (a is LoggedNodeAction { Operation: PrimitiveOperation.Add } n) {
                if (_ids.Count > maxIds) _ids.Clear(); // the database's own map is asked for the rest
                _ids[n.Head.IntId] = (n.Head.Id, _types!.Of(n.Head.Type));
            }
        }
        if ((tx.Flags & TransactionLogFlags.Copy) != 0) return;
        if (batch.CopyPrefix) {
            if (isCopyLike(tx)) return;
            batch.CopyPrefix = false;
        }
        if (planned.SkipFrom > 0 && tx.Timestamp >= planned.SkipFrom) return; // the secondary log gives it
        if (tx.Timestamp <= batch.MaxTs) return; // in a file before, or out of order
        batch.MaxTs = tx.Timestamp;
        changesOf(tx, planned.Format, batch);
        linksOf(tx, planned.Format, batch);
    }
    // a log rewrite of an older format starts its file with a copy of every node, 97 a transaction, then of every
    // relation, one a transaction, under new timestamps and with nothing to say so
    static bool isCopyLike(LoggedTransaction tx) => tx.OnlyAdds && (
        (tx.NodeAdds > 0 && tx.NodeAdds <= 97 && tx.RelationAdds == 0 && tx.Reorders == 0)
        || (tx.NodeAdds == 0 && (tx.RelationIds?.Count ?? 0) <= 1));

    void changesOf(LoggedTransaction tx, long format, Batch batch) {
        // a node's first action and its last: an update is written as remove(old) and add(new), and a save that
        // wrote it twice does so twice, the state between never committed
        var first = new Dictionary<Guid, LoggedNodeAction>();
        var last = new Dictionary<Guid, LoggedNodeAction>();
        var order = new List<Guid>();
        foreach (var a in tx.Actions) {
            if (a is not LoggedNodeAction n) continue;
            if (first.TryAdd(n.Head.Id, n)) order.Add(n.Head.Id);
            last[n.Head.Id] = n;
        }
        var rules = rulesTable();
        var system = (tx.Flags & TransactionLogFlags.System) != 0;
        foreach (var id in order) {
            var f = first[id];
            var l = last[id];
            LoggedNodeAction? before = f.Operation == PrimitiveOperation.Remove ? f : null;
            LoggedNodeAction? after = l.Operation == PrimitiveOperation.Add ? l : null;
            if (before == null && after == null) continue; // made and deleted in one save
            var head = (after ?? before)!.Value.Head;
            var type = _types!.Of(head.Type);
            if (!inHistory(rules, type)) continue;
            foreach (var (b, a) in pairs(before?.Head, after?.Head)) {
                LogChangeKinds kind;
                if (b == null) kind = LogChangeKinds.Created | (before == null ? LogChangeKinds.NodeCreated : 0);
                else if (a == null) kind = LogChangeKinds.Removed | (after == null ? LogChangeKinds.NodeRemoved : 0);
                else kind = compare(b, a);
                if (kind == LogChangeKinds.None) continue;
                if (system) kind |= LogChangeKinds.System;
                var writer = WALFile.HasTransactionWriter(format) ? tx.Writer : (a ?? b)!.ChangedBy;
                var rev = (a ?? b)!;
                batch.Rows.Add(new ChangeRow {
                    At = tx.Timestamp,
                    AfterPos = after?.Position ?? 0, AfterLen = after?.Length ?? 0,
                    BeforePos = before?.Position ?? 0, BeforeLen = before?.Length ?? 0,
                    Node = id,
                    Version = rev.RevisionId,
                    Type = (ushort)type,
                    Kind = (ushort)kind,
                    Who = writer == Guid.Empty ? 0 : _writers!.Of(writer),
                    Culture = (ushort)_cultures!.Of(rev.Culture),
                    Collection = (ushort)_collections!.Of(rev.Collection),
                    RevAfter = a == null ? ChangeRow.NoRevision : (byte)a.Type,
                    RevBefore = b == null ? ChangeRow.NoRevision : (byte)b.Type,
                });
            }
        }
    }
    /// <summary>The versions of a node before and after a save, side by side: by revision id, a node's one version without
    /// revisions paired with the revision it became (the one with the same values, else the published one).</summary>
    static IEnumerable<(RevisionHead? before, RevisionHead? after)> pairs(NodeHead? before, NodeHead? after) {
        if (before == null) {
            foreach (var a in after!.Revisions) yield return (null, a);
            yield break;
        }
        if (after == null) {
            foreach (var b in before.Revisions) yield return (b, null);
            yield break;
        }
        var unmatchedAfter = new List<RevisionHead>(after.Revisions);
        var unmatchedBefore = new List<RevisionHead>();
        foreach (var b in before.Revisions) {
            var a = unmatchedAfter.FirstOrDefault(x => x.RevisionId == b.RevisionId);
            if (a != null) {
                unmatchedAfter.Remove(a);
                yield return (b, a);
            } else {
                unmatchedBefore.Add(b);
            }
        }
        // a node given revisions (or its last one taken away): its version is the revision now
        if (!before.Container && after.Container && unmatchedBefore.Count == 1 && unmatchedAfter.Count > 0) {
            var b = unmatchedBefore[0];
            var a = unmatchedAfter.FirstOrDefault(x => x.ValuesHash == b.ValuesHash && x.Culture == b.Culture)
                ?? unmatchedAfter.FirstOrDefault(x => x.Type == RevisionType.Published) ?? unmatchedAfter[0];
            unmatchedBefore.Clear();
            unmatchedAfter.Remove(a);
            yield return (b, a);
        } else if (before.Container && !after.Container && unmatchedAfter.Count == 1 && unmatchedBefore.Count > 0) {
            var a = unmatchedAfter[0];
            var b = unmatchedBefore.FirstOrDefault(x => x.Type == RevisionType.Published) ?? unmatchedBefore[0];
            unmatchedAfter.Clear();
            unmatchedBefore.Remove(b);
            yield return (b, a);
        }
        foreach (var b in unmatchedBefore) yield return (b, null);
        foreach (var a in unmatchedAfter) yield return (null, a);
    }
    static LogChangeKinds compare(RevisionHead b, RevisionHead a) {
        var kind = LogChangeKinds.None;
        if (b.Type != a.Type) {
            if (a.Type == RevisionType.Published) kind |= LogChangeKinds.Published;
            else if (b.Type == RevisionType.Published) kind |= LogChangeKinds.Unpublished;
            else kind |= LogChangeKinds.MetaChanged;
        }
        if (b.Culture != a.Culture) kind |= LogChangeKinds.LanguageMoved;
        if (!b.Deleted && a.Deleted) kind |= LogChangeKinds.MarkedDeleted;
        if (b.Deleted && !a.Deleted) kind |= LogChangeKinds.Unmarked;
        if (b.ValuesHash != a.ValuesHash) kind |= LogChangeKinds.Updated;
        var bm = b.Meta ?? IInnerNodeMeta.Empty;
        var am = a.Meta ?? IInnerNodeMeta.Empty;
        if (b.AddressHash != a.AddressHash || bm.CollectionId != am.CollectionId || bm.ReadAccess != am.ReadAccess || bm.EditAccess != am.EditAccess
            || bm.EditViewAccess != am.EditViewAccess || bm.PublishAccess != am.PublishAccess || bm.ReleaseUtc != am.ReleaseUtc || bm.ExpireUtc != am.ExpireUtc)
            kind |= LogChangeKinds.MetaChanged;
        return kind;
    }
    void linksOf(LoggedTransaction tx, long format, Batch batch) {
        // what a save did to each link in the end: taken away and made again (a list written whole) is no change
        Dictionary<(Guid relation, int source, int target), (int net, long pos)>? net = null;
        List<(Guid, int, int)>? order = null;
        foreach (var a in tx.Actions) {
            if (a is not LoggedRelationAction r) continue;
            net ??= [];
            order ??= [];
            var key = (r.Relation, r.Source, r.Target);
            if (!net.TryGetValue(key, out var v)) {
                order.Add(key);
                v = (0, r.Position);
            }
            net[key] = (v.net + (r.Operation == PrimitiveOperation.Add ? 1 : -1), v.pos);
        }
        if (net == null) return;
        var rules = rulesTable();
        var system = (tx.Flags & TransactionLogFlags.System) != 0;
        var writer = WALFile.HasTransactionWriter(format) ? tx.Writer : Guid.Empty;
        foreach (var key in order!) {
            var (n, pos) = net[key];
            if (n == 0) continue;
            var source = nodeOf(key.Item2);
            var target = nodeOf(key.Item3);
            if (!inHistory(rules, source.type) || !inHistory(rules, target.type)) continue;
            batch.Links.Add(new LinkRow {
                At = tx.Timestamp, Pos = pos,
                Relation = (ushort)_relations!.Of(key.Item1),
                Added = n > 0, System = system,
                Who = writer == Guid.Empty ? 0 : _writers!.Of(writer),
                Source = source.id, SourceType = (ushort)source.type,
                Target = target.id, TargetType = (ushort)target.type,
            });
        }
    }
    (Guid id, int type) nodeOf(int internalId) {
        if (_ids.TryGetValue(internalId, out var known)) return known;
        if (_store.TryResolveNodeForHistory(internalId, out var id, out var typeId)) {
            var found = (id, _types!.Of(typeId));
            _ids[internalId] = found;
            return found;
        }
        return (Guid.Empty, 0);
    }

    // ------------------------------------------------------------------ the index on disk

    void ensureLoaded() {
        if (_state != null) return;
        _lock.EnterWriteLock();
        try {
            var f = files;
            var state = f.ReadState();
            if (state == null) {
                f.DeleteAll();
                state = new HistoryStateFile { StartedUtc = DateTime.UtcNow };
            }
            _types = new(state.Types, ushort.MaxValue - 1);
            _writers = new(state.Writers, int.MaxValue - 1);
            _cultures = new(state.Cultures, ushort.MaxValue - 1);
            _collections = new(state.Collections, ushort.MaxValue - 1);
            _relations = new(state.Relations, ushort.MaxValue - 1);
            _ats.Clear();
            _ids.Clear();
            _rules = null;
            _rulesFor = null;
            for (var i = 0; i < state.Sources.Count; i++) {
                var s = state.Sources[i];
                var changesKey = HistoryFiles.ChangesKey(s.Name);
                var linksKey = HistoryFiles.LinksKey(s.Name);
                var changesLength = f.Length(changesKey);
                var linksLength = f.Length(linksKey);
                if (changesLength < s.Rows * ChangeRow.Size || linksLength < s.Links * LinkRow.Size) {
                    // a rows file shorter than history.json says: this source and the ones after it are read again
                    for (var j = state.Sources.Count - 1; j >= i; j--) f.DeleteSource(state.Sources[j].Name);
                    state.Sources.RemoveRange(i, state.Sources.Count - i);
                    break;
                }
                f.Cut(changesKey, s.Rows * ChangeRow.Size); // longer: a batch written but not recorded, read again from the log
                f.Cut(linksKey, s.Links * LinkRow.Size);
                _ats[s.Name] = (loadAts(f, changesKey, s.Rows, ChangeRow.Size), loadAts(f, linksKey, s.Links, LinkRow.Size));
            }
            _state = state;
            saveState();
        } finally {
            _lock.ExitWriteLock();
        }
    }
    static TimestampList loadAts(HistoryFiles f, string[] key, long count, int size) {
        var list = new TimestampList();
        const int block = 8192;
        for (long i = 0; i < count; i += block) {
            var n = (int)Math.Min(block, count - i);
            var bytes = f.Read(key, i * size, n * size);
            for (var j = 0; j < n; j++) list.Add(BitConverter.ToInt64(bytes, j * size));
        }
        return list;
    }
    void saveState() {
        if (_state == null) return;
        files.WriteState(_state);
    }
    void unload() {
        if (_state == null) return;
        _lock.EnterWriteLock();
        try {
            _state = null;
            _types = _writers = _cultures = _collections = _relations = null;
            _ats.Clear();
            _ids.Clear();
            _rules = null;
            _rulesFor = null;
            _resolved = null;
        } finally {
            _lock.ExitWriteLock();
        }
    }
    /// <summary>A source and every one after it read again from the start.</summary>
    void restartFrom(int index) {
        _lock.EnterWriteLock();
        try {
            var sources = _state!.Sources;
            for (var i = index; i < sources.Count; i++) {
                var s = sources[i];
                files.DeleteSource(s.Name);
                sources[i] = new HistorySourceState {
                    Name = s.Name, Key = s.Key, Secondary = s.Secondary, FileId = s.FileId, Format = s.Format, SkipFrom = s.SkipFrom,
                    ReadPos = headerLength, CopyPrefix = s.StartsWithCopy, StartsWithCopy = s.StartsWithCopy,
                    MaxTs = i > 0 ? sources[i - 1].MaxTs : 0,
                };
                _ats[s.Name] = (new(), new());
            }
            saveState();
        } finally {
            _lock.ExitWriteLock();
        }
    }
    /// <summary>What a revert cut off the end of a source goes from the index: the rows of the transactions after <paramref name="keepEnd"/>.</summary>
    void cutSource(int index, long keepEnd) {
        _lock.EnterWriteLock();
        try {
            var s = _state!.Sources[index];
            var f = files;
            var changesKey = HistoryFiles.ChangesKey(s.Name);
            var linksKey = HistoryFiles.LinksKey(s.Name);
            // rows are in log order: the first whose save ends past the cut, found by halving
            long lo = 0, hi = s.Rows;
            while (lo < hi) {
                var mid = (lo + hi) / 2;
                if (ChangeRow.Read(f.Read(changesKey, mid * ChangeRow.Size, ChangeRow.Size)).End > keepEnd) hi = mid; else lo = mid + 1;
            }
            var rows = lo;
            lo = 0; hi = s.Links;
            while (lo < hi) {
                var mid = (lo + hi) / 2;
                if (LinkRow.Read(f.Read(linksKey, mid * LinkRow.Size, LinkRow.Size)).Pos >= keepEnd) hi = mid; else lo = mid + 1;
            }
            var links = lo;
            f.Cut(changesKey, rows * ChangeRow.Size);
            f.Cut(linksKey, links * LinkRow.Size);
            var (cats, lats) = atsOf(s.Name);
            cats.Cut((int)rows);
            lats.Cut((int)links);
            s.Rows = rows;
            s.Links = links;
            s.ReadPos = Math.Min(s.ReadPos, keepEnd);
            s.LastTxPos = 0;
            s.LastTxTs = 0;
            var previous = index > 0 ? _state.Sources[index - 1].MaxTs : 0;
            s.MaxTs = Math.Max(previous, Math.Max(rows > 0 ? cats[(int)rows - 1] : 0, links > 0 ? lats[(int)links - 1] : 0));
            saveState();
        } finally {
            _lock.ExitWriteLock();
        }
    }
    /// <summary>Throws the index away; the reader builds it again from the log, in the background.</summary>
    public void Rebuild() {
        var cts = _runCts;
        _runCts = new();
        cts.Cancel();
        lock (_runLock) {
            unload();
            files.DeleteAll();
            _headers.Clear();
            _cut = null;
            _bytesBehind = _bytesTotal; // building from the start, until the next run says how far it got
        }
        _wake.Set();
    }
    /// <summary>Called by a revert before it cuts the log: the rows of what it cuts go at the next run.</summary>
    public void NoteRevert(string[] logFileKey, long keepEnd, bool secondaryAlike) {
        _cut = (logFileKey.AsKeyString(), keepEnd, secondaryAlike);
    }

    // ------------------------------------------------------------------ the type rules

    /// <summary>Whether nodes of each type (by its number in the types table) are in history, compiled once for the
    /// datamodel and the settings and read without locks: one array read for each row.</summary>
    bool[] rulesTable() {
        var types = _types;
        if (types == null) return [];
        var model = _store.Datamodel;
        var byDefault = settings.TypesByDefault;
        var rules = _rules;
        if (rules != null && _rulesFor == (model, byDefault) && rules.Length == types.Count) return rules;
        if (_rulesFor != (model, byDefault) || _resolved == null) _resolved = model.ResolveSwitch(t => t.History, nameof(NodeTypeModel.History), byDefault);
        var resolved = _resolved;
        var table = new bool[types.Count];
        for (var i = 0; i < table.Length; i++) table[i] = i == 0 || !resolved.TryGetValue(types[i], out var v) || v;
        _rules = table;
        _rulesFor = (model, byDefault);
        return table;
    }
    static bool inHistory(bool[] rules, int type) => type >= rules.Length || rules[type];

    /// <summary>The engine's own properties (the text index's), which change with an index-only write that is no change.</summary>
    HashSet<Guid> ignoredProperties() {
        var model = _store.Datamodel;
        if (_ignored != null && _ignoredFor == model) return _ignored;
        var set = new HashSet<Guid>();
        foreach (var t in model.NodeTypes.Values) foreach (var p in t.AllProperties.Values) if (p.Internal) set.Add(p.Id);
        _ignored = set;
        _ignoredFor = model;
        return set;
    }

    public void Dispose() {
        Stop();
        _wake.Dispose();
        _lock.Dispose();
    }
}
