using System.Globalization;
using Relatude.DB.Datamodels;
using Relatude.DB.IO;
using Relatude.DB.Serialization;

namespace Relatude.DB.DataStores.History;

internal sealed partial class LogHistory {

    // ------------------------------------------------------------------ status

    public LogHistoryStatus Status() {
        var enabled = settings.Enabled;
        _lock.EnterReadLock();
        try {
            var sources = _state?.Sources ?? [];
            long changes = 0, links = 0;
            long? first = null, latest = null;
            var list = new List<LogHistorySourceStatus>();
            foreach (var s in sources) {
                changes += s.Rows;
                links += s.Links;
                var (cats, lats) = _ats.TryGetValue(s.Name, out var a) ? a : (new TimestampList(), new TimestampList());
                long? f = null, l = null;
                if (cats.Count > 0) { f = cats[0]; l = cats[cats.Count - 1]; }
                if (lats.Count > 0) { f = f == null ? lats[0] : Math.Min(f.Value, lats[0]); l = l == null ? lats[lats.Count - 1] : Math.Max(l.Value, lats[lats.Count - 1]); }
                if (f != null && (first == null || f < first)) first = f;
                if (l != null && (latest == null || l > latest)) latest = l;
                list.Add(new LogHistorySourceStatus {
                    Key = s.Key, Length = s.Length, ReadTo = s.ReadPos, Changes = s.Rows, Links = s.Links,
                    FirstUtc = f == null ? null : new DateTime(f.Value, DateTimeKind.Utc), LastUtc = l == null ? null : new DateTime(l.Value, DateTimeKind.Utc),
                });
            }
            var behind = enabled ? _bytesBehind : 0;
            var total = _bytesTotal;
            return new LogHistoryStatus {
                Supported = true,
                Enabled = enabled,
                Source = sources.Any(s => s.Secondary) ? "secondary" : "files",
                ReachesBackToUtc = first == null ? null : new DateTime(first.Value, DateTimeKind.Utc),
                LatestUtc = latest == null ? null : new DateTime(latest.Value, DateTimeKind.Utc),
                Changes = changes,
                Links = links,
                IndexBytes = _state == null && !enabled ? 0 : safeSize(),
                Building = enabled && (_state == null || behind > buildingThreshold),
                Percent = !enabled ? 0 : total <= 0 ? (_state == null ? 0 : 100) : (int)Math.Clamp(100 * (total - behind) / total, 0, 100),
                BytesBehind = behind,
                LastRunUtc = _lastRunUtc,
                Problem = _problem,
                Sources = [.. list],
            };
        } finally {
            _lock.ExitReadLock();
        }
    }
    long safeSize() {
        try { return files.Size(_state?.Sources.Select(s => s.Name) ?? []); } catch { return 0; }
    }

    // ------------------------------------------------------------------ reading rows

    /// <summary>A source as a query sees it: the rows there were when it began.</summary>
    sealed record View(HistorySourceState Source, int Rows, int Links, TimestampList Changes, TimestampList LinksAt);

    List<View> views() {
        var list = new List<View>();
        if (_state == null) return list;
        foreach (var s in _state.Sources) {
            if (!_ats.TryGetValue(s.Name, out var a)) continue;
            list.Add(new View(s, Math.Min(a.changes.Count, (int)s.Rows), Math.Min(a.links.Count, (int)s.Links), a.changes, a.links));
        }
        return list;
    }

    /// <summary>The rows a query reads, a block at a time, each block read with one open of the rows file.</summary>
    sealed class RowReader(HistoryFiles files, string source) {
        const int block = 512;
        byte[]? _changes, _links;
        long _changesStart = -1, _linksStart = -1;
        int _changesCount, _linksCount;
        public ChangeRow Change(long index, long count) {
            if (_changes == null || index < _changesStart || index >= _changesStart + _changesCount) {
                _changesStart = Math.Max(0, index - block / 2);
                _changesCount = (int)Math.Min(block, count - _changesStart);
                _changes = files.Read(HistoryFiles.ChangesKey(source), _changesStart * ChangeRow.Size, _changesCount * ChangeRow.Size);
            }
            return ChangeRow.Read(_changes.AsSpan((int)(index - _changesStart) * ChangeRow.Size, ChangeRow.Size));
        }
        public LinkRow Link(long index, long count) {
            if (_links == null || index < _linksStart || index >= _linksStart + _linksCount) {
                _linksStart = Math.Max(0, index - block / 2);
                _linksCount = (int)Math.Min(block, count - _linksStart);
                _links = files.Read(HistoryFiles.LinksKey(source), _linksStart * LinkRow.Size, _linksCount * LinkRow.Size);
            }
            return LinkRow.Read(_links.AsSpan((int)(index - _linksStart) * LinkRow.Size, LinkRow.Size));
        }
    }

    /// <summary>One save as the index holds it: every change row and link row with its timestamp.</summary>
    sealed class Save(long at, View view) {
        public readonly long At = at;
        public readonly View View = view;
        public readonly List<ChangeRow> Changes = [];
        public readonly List<LinkRow> Links = [];
    }

    /// <summary>The saves with timestamps in [from, to), newest first or oldest first, across the sources in order.</summary>
    IEnumerable<Save> saves(List<View> views, long from, long to, bool newestFirst) {
        var f = files;
        var order = newestFirst ? Enumerable.Range(0, views.Count).Reverse() : Enumerable.Range(0, views.Count);
        foreach (var vi in order) {
            var v = views[vi];
            var reader = new RowReader(f, v.Source.Name);
            int clo = v.Changes.LowerBound(from, v.Rows), chi = v.Changes.LowerBound(to, v.Rows);
            int llo = v.LinksAt.LowerBound(from, v.Links), lhi = v.LinksAt.LowerBound(to, v.Links);
            if (newestFirst) {
                int ci = chi - 1, li = lhi - 1;
                while (ci >= clo || li >= llo) {
                    var at = Math.Max(ci >= clo ? v.Changes[ci] : long.MinValue, li >= llo ? v.LinksAt[li] : long.MinValue);
                    var save = new Save(at, v);
                    var cEnd = ci;
                    while (ci >= clo && v.Changes[ci] == at) ci--;
                    for (var i = ci + 1; i <= cEnd; i++) save.Changes.Add(reader.Change(i, v.Rows));
                    var lEnd = li;
                    while (li >= llo && v.LinksAt[li] == at) li--;
                    for (var i = li + 1; i <= lEnd; i++) save.Links.Add(reader.Link(i, v.Links));
                    yield return save;
                }
            } else {
                int ci = clo, li = llo;
                while (ci < chi || li < lhi) {
                    var at = Math.Min(ci < chi ? v.Changes[ci] : long.MaxValue, li < lhi ? v.LinksAt[li] : long.MaxValue);
                    var save = new Save(at, v);
                    while (ci < chi && v.Changes[ci] == at) save.Changes.Add(reader.Change(ci++, v.Rows));
                    while (li < lhi && v.LinksAt[li] == at) save.Links.Add(reader.Link(li++, v.Links));
                    yield return save;
                }
            }
        }
    }

    // ------------------------------------------------------------------ filters

    /// <summary>A query's filters as numbers of the tables, so a row is tested with array reads and set lookups.</summary>
    sealed class Filter {
        public bool[] Rules = [];
        public HashSet<int>? Types, Writers, Cultures, Collections, Relations;
        public HashSet<Guid>? Nodes;
        public LogChangeKinds Kinds;
        public bool System, WithLinks;
        public bool Change(in ChangeRow r) {
            if (r.Type < Rules.Length && !Rules[r.Type]) return false;
            var kind = (LogChangeKinds)r.Kind;
            if (!System && (kind & LogChangeKinds.System) != 0) return false;
            if ((kind & Kinds & ~LogChangeKinds.System) == 0) return false;
            if (Types != null && !Types.Contains(r.Type)) return false;
            if (Writers != null && !Writers.Contains(r.Who)) return false;
            if (Nodes != null && !Nodes.Contains(r.Node)) return false;
            if (Cultures != null && !Cultures.Contains(r.Culture)) return false;
            if (Collections != null && !Collections.Contains(r.Collection)) return false;
            return true;
        }
        public bool Link(in LinkRow l) {
            if (!WithLinks) return false;
            if ((l.SourceType < Rules.Length && !Rules[l.SourceType]) || (l.TargetType < Rules.Length && !Rules[l.TargetType])) return false;
            if (!System && l.System) return false;
            if ((Kinds & (l.Added ? LogChangeKinds.Linked : LogChangeKinds.Unlinked)) == 0) return false;
            if (Cultures != null || Collections != null) return false; // a link is in no language or site
            if (Types != null && !Types.Contains(l.SourceType) && !Types.Contains(l.TargetType)) return false;
            if (Writers != null && !Writers.Contains(l.Who)) return false;
            if (Nodes != null && !Nodes.Contains(l.Source) && !Nodes.Contains(l.Target)) return false;
            if (Relations != null && !Relations.Contains(l.Relation)) return false;
            return true;
        }
    }
    Filter filterOf(Guid[]? nodeTypes, Guid[]? writers, Guid[]? nodes, Guid[]? cultures, Guid[]? collections, Guid[]? relations,
        LogChangeKinds kinds, bool system, bool withLinks) {
        var f = new Filter { Rules = rulesTable(), Kinds = kinds, System = system, WithLinks = withLinks };
        if (nodeTypes is { Length: > 0 }) {
            var model = _store.Datamodel;
            var wanted = nodeTypes.ToHashSet();
            f.Types = [];
            foreach (var t in model.NodeTypes.Values) {
                if (!wanted.Any(w => t.Id == w || t.ThisAndAllInheritedTypes.ContainsKey(w))) continue;
                if (_types!.TryGet(t.Id, out var n)) f.Types.Add(n);
            }
            foreach (var w in wanted) if (_types!.TryGet(w, out var n)) f.Types.Add(n); // a type the model no longer has
        }
        f.Writers = numbers(writers, _writers!);
        f.Cultures = numbers(cultures, _cultures!);
        f.Collections = numbers(collections, _collections!);
        f.Relations = numbers(relations, _relations!);
        if (nodes is { Length: > 0 }) f.Nodes = nodes.ToHashSet();
        return f;
    }
    static HashSet<int>? numbers(Guid[]? ids, HistoryTable table) {
        if (ids is not { Length: > 0 }) return null;
        var set = new HashSet<int>();
        foreach (var id in ids) {
            if (id == Guid.Empty) set.Add(0);
            else if (table.TryGet(id, out var n)) set.Add(n);
        }
        return set;
    }

    // ------------------------------------------------------------------ a page of changes

    public LogChangePage Read(LogChangeQuery q, Func<HistorySourceState, IHistoryLogFile?> fileOf) {
        var take = Math.Clamp(q.Take, 1, 500);
        long from = q.FromUtc?.ToUniversalTime().Ticks ?? long.MinValue;
        long to = q.ToUtc?.ToUniversalTime().Ticks ?? long.MaxValue;
        if (q.Transaction is { } only) { from = Math.Max(from, only); to = Math.Min(to, only + 1); }
        long? cursorAt = null;
        var cursorSkip = 0;
        if (!string.IsNullOrEmpty(q.Cursor)) {
            var parts = q.Cursor.Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var at) || !int.TryParse(parts[1], out cursorSkip))
                throw new ArgumentException("The cursor is not one a page of history gave. ");
            cursorAt = at;
            if (q.NewestFirst) to = Math.Min(to, at + 1); else from = Math.Max(from, at);
        }
        var changes = new List<(LoggedChange change, ChangeRow row, View view)>();
        var links = new List<LoggedRelationChange>();
        string? next = null;
        var count = 0;
        _lock.EnterReadLock();
        try {
            if (_state == null) return new LogChangePage();
            var filter = filterOf(q.NodeTypes, q.Writers, q.Nodes, q.Cultures, q.Collections, q.Relations, q.Kinds, q.IncludeSystemWrites, q.WithLinks);
            foreach (var save in saves(views(), from, to, q.NewestFirst)) {
                var entries = entriesOf(save, filter, q);
                var skip = cursorAt == save.At ? cursorSkip : 0;
                for (var i = skip; i < entries.Count; i++) {
                    if (count == take) {
                        next = save.At.ToString(CultureInfo.InvariantCulture) + ":" + i;
                        break;
                    }
                    var e = entries[i];
                    if (e.change != null) changes.Add((e.change, e.row, save.View));
                    else links.Add(e.link!);
                    count++;
                }
                if (next != null) break;
                if (count == take) { // the page ends with a save: the next one starts after it
                    next = save.At.ToString(CultureInfo.InvariantCulture) + ":" + entries.Count;
                    break;
                }
            }
        } finally {
            _lock.ExitReadLock();
        }
        var result = changes.Select(c => c.change).ToArray();
        if (q.WithDetails) result = [.. changes.Select(c => withDetails(c.change, c.row, c.view.Source, fileOf))];
        else result = [.. changes.Select(c => c.change with { VersionKept = kept(c.row, c.view.Source, fileOf) })];
        return new LogChangePage { Changes = result, Links = [.. links], Next = next };
    }
    sealed record Entry(LoggedChange? change, ChangeRow row, LoggedRelationChange? link);
    /// <summary>What one save shows as: its rows that pass the filter, or with collapsing one entry per node and one for a big save.</summary>
    List<Entry> entriesOf(Save save, Filter filter, LogChangeQuery q) {
        var result = new List<Entry>();
        var inTransaction = save.Changes.Count;
        var changes = save.Changes.Where(r => filter.Change(r)).ToList();
        var links = save.Links.Where(l => filter.Link(l)).ToList();
        if (!q.Collapse) {
            foreach (var r in changes) result.Add(new Entry(changeOf(r, inTransaction, save.View), r, null));
            foreach (var l in links) result.Add(new Entry(null, default, linkOf(l, save.Links.Count)));
            return result;
        }
        // one entry per node, its versions together
        var byNode = new List<(ChangeRow first, LogChangeKinds kinds, int versions)>();
        var index = new Dictionary<Guid, int>();
        foreach (var r in changes) {
            if (index.TryGetValue(r.Node, out var i)) {
                var e = byNode[i];
                byNode[i] = (e.first, e.kinds | (LogChangeKinds)r.Kind, e.versions + 1);
            } else {
                index[r.Node] = byNode.Count;
                byNode.Add((r, (LogChangeKinds)r.Kind, 1));
            }
        }
        var above = Math.Max(1, q.CollapseAbove);
        if (byNode.Count > above) {
            var first = byNode[0];
            var types = byNode.GroupBy(n => n.first.Type).Select(g => new LogTypeCount(_types![g.Key], g.Count())).OrderByDescending(t => t.Count).ToArray();
            var all = byNode.Aggregate(LogChangeKinds.None, (k, n) => k | n.kinds);
            result.Add(new Entry(changeOf(first.first, inTransaction, save.View) with { Kind = all, Versions = first.versions, Others = byNode.Count - 1, TypeCounts = types }, first.first, null));
        } else {
            foreach (var n in byNode) result.Add(new Entry(changeOf(n.first, inTransaction, save.View) with { Kind = n.kinds, Versions = n.versions }, n.first, null));
        }
        if (links.Count > above && byNode.Count > above) return result; // a big save's links are part of it
        foreach (var l in links) result.Add(new Entry(null, default, linkOf(l, save.Links.Count)));
        return result;
    }
    LoggedChange changeOf(in ChangeRow r, int inTransaction, View view) => new(
        r.At, new DateTime(r.At, DateTimeKind.Utc), r.Node, _types![r.Type], r.Version, (LogChangeKinds)r.Kind,
        r.Who == 0 ? null : _writers![r.Who],
        r.Culture == 0 ? null : _cultures![r.Culture],
        r.Collection == 0 ? null : _collections![r.Collection],
        r.RevAfter == ChangeRow.NoRevision ? null : (RevisionType)r.RevAfter,
        r.RevBefore == ChangeRow.NoRevision ? null : (RevisionType)r.RevBefore,
        inTransaction) { Source = view.Source.Name };
    LoggedRelationChange linkOf(in LinkRow l, int inTransaction) => new(
        l.At, new DateTime(l.At, DateTimeKind.Utc), _relations![l.Relation], l.Source, l.Target, l.Added, l.Who == 0 ? null : _writers![l.Who], inTransaction) {
        SourceType = _types![l.SourceType], TargetType = _types![l.TargetType],
    };

    static bool kept(in ChangeRow r, HistorySourceState source, Func<HistorySourceState, IHistoryLogFile?> fileOf) {
        try {
            var file = fileOf(source);
            return file != null && file.Length() >= r.End;
        } catch {
            return false;
        }
    }
    /// <summary>The name and the changed properties, read from the log by the positions the row holds.</summary>
    LoggedChange withDetails(LoggedChange change, ChangeRow row, HistorySourceState source, Func<HistorySourceState, IHistoryLogFile?> fileOf) {
        IHistoryLogFile? file;
        try { file = fileOf(source); } catch { file = null; }
        if (file == null || file.Length() < row.End) return change with { VersionKept = false };
        var ignored = ignoredProperties();
        (Dictionary<Guid, ulong> properties, string? name)? read(long pos, int len) {
            if (len <= 0) return null;
            var buffer = new byte[len];
            try {
                if (!file.Read(pos, len, buffer)) return null;
                return HistoryDecoder.Properties(buffer, row.Version, ignored);
            } catch {
                return null;
            }
        }
        var after = read(row.AfterPos, row.AfterLen);
        var before = read(row.BeforePos, row.BeforeLen);
        Guid[]? changed = null;
        if (after != null && before != null) {
            var a = after.Value.properties;
            var b = before.Value.properties;
            changed = [.. a.Keys.Union(b.Keys).Where(id => !a.TryGetValue(id, out var x) || !b.TryGetValue(id, out var y) || x != y)];
        }
        return change with { Name = after?.name ?? before?.name, ChangedProperties = changed, VersionKept = true };
    }

    // ------------------------------------------------------------------ activity

    public LogActivity Analyse(LogActivityQuery q) {
        var fromUtc = q.FromUtc.ToUniversalTime();
        var toUtc = q.ToUtc.ToUniversalTime();
        if (toUtc <= fromUtc) throw new ArgumentException("The period ends before it starts. ");
        var starts = new List<DateTime>();
        for (var t = floor(fromUtc, q.Interval); t < toUtc && starts.Count < 10000; t = step(t, q.Interval)) starts.Add(t);
        var buckets = starts.Select(s => new Acc()).ToArray();
        var totals = new Acc();
        var groupTotals = new Dictionary<string, int>();
        _lock.EnterReadLock();
        try {
            if (_state != null) {
                var filter = filterOf(q.NodeTypes, q.Writers, q.Nodes, q.Cultures, q.Collections, q.Relations, q.Kinds, q.IncludeSystemWrites, q.WithLinks);
                foreach (var save in saves(views(), fromUtc.Ticks, toUtc.Ticks, false)) {
                    var at = new DateTime(save.At, DateTimeKind.Utc);
                    var b = bucketOf(starts, at);
                    if (b < 0) continue;
                    var any = false;
                    foreach (var r in save.Changes) {
                        if (!filter.Change(r)) continue;
                        any = true;
                        var key = q.By == LogActivityBy.None ? null : groupKey(r, q.By);
                        foreach (var acc in new[] { buckets[b], totals }) acc.Change(r, key);
                        if (key != null) groupTotals[key] = groupTotals.GetValueOrDefault(key) + 1;
                    }
                    foreach (var l in save.Links) {
                        if (!filter.Link(l)) continue;
                        any = true;
                        buckets[b].Link(l);
                        totals.Link(l);
                    }
                    if (any) {
                        buckets[b].Saves++;
                        totals.Saves++;
                    }
                }
            }
        } finally {
            _lock.ExitReadLock();
        }
        var top = groupTotals.OrderByDescending(g => g.Value).Take(Math.Max(1, q.TopGroups)).Select(g => g.Key).ToHashSet();
        var groups = groupTotals.Where(g => top.Contains(g.Key)).OrderByDescending(g => g.Value).Select(g => new LogActivityGroup(g.Key, g.Value)).ToList();
        var other = groupTotals.Where(g => !top.Contains(g.Key)).Sum(g => g.Value);
        if (other > 0) groups.Add(new LogActivityGroup("other", other));
        return new LogActivity {
            Interval = q.Interval, FromUtc = fromUtc, ToUtc = toUtc, By = q.By,
            Totals = totals.Counts(),
            Groups = [.. groups],
            Buckets = [.. starts.Select((s, i) => new LogActivityBucket {
                StartUtc = s,
                Counts = buckets[i].Counts(),
                Groups = q.By == LogActivityBy.None ? null : buckets[i].GroupsOf(top),
            })],
        };
    }
    sealed class Acc {
        public int Changes, Saves, Links;
        public readonly HashSet<int> People = [];
        public readonly HashSet<Guid> Nodes = [];
        public readonly Dictionary<string, int> Groups = [];
        public void Change(in ChangeRow r, string? key) {
            Changes++;
            People.Add(r.Who);
            Nodes.Add(r.Node);
            if (key != null) Groups[key] = Groups.GetValueOrDefault(key) + 1;
        }
        public void Link(in LinkRow l) {
            Links++;
            People.Add(l.Who);
        }
        public LogActivityCounts Counts() => new() { Changes = Changes, Saves = Saves, Links = Links, Nodes = Nodes.Count, People = People.Count(p => p != 0) };
        public Dictionary<string, int> GroupsOf(HashSet<string> top) {
            var result = new Dictionary<string, int>();
            foreach (var (k, v) in Groups) {
                var key = top.Contains(k) ? k : "other";
                result[key] = result.GetValueOrDefault(key) + v;
            }
            return result;
        }
    }
    string groupKey(in ChangeRow r, LogActivityBy by) => by switch {
        LogActivityBy.Kind => kindName((LogChangeKinds)r.Kind),
        LogActivityBy.Type => _types![r.Type].ToString(),
        LogActivityBy.Writer => (r.Who == 0 ? Guid.Empty : _writers![r.Who]).ToString(),
        LogActivityBy.Culture => (r.Culture == 0 ? Guid.Empty : _cultures![r.Culture]).ToString(),
        LogActivityBy.Collection => (r.Collection == 0 ? Guid.Empty : _collections![r.Collection]).ToString(),
        _ => "",
    };
    /// <summary>The one kind a change is counted as in a chart, the weightiest it has: a node made or deleted is NodeCreated or NodeRemoved, a version of one Created or Removed.</summary>
    static string kindName(LogChangeKinds k) {
        foreach (var kind in new[] { LogChangeKinds.NodeRemoved, LogChangeKinds.Removed, LogChangeKinds.MarkedDeleted, LogChangeKinds.Unmarked, LogChangeKinds.NodeCreated, LogChangeKinds.Created,
            LogChangeKinds.Published, LogChangeKinds.Unpublished, LogChangeKinds.LanguageMoved, LogChangeKinds.Updated, LogChangeKinds.MetaChanged }) {
            if ((k & kind) != 0) return kind.ToString();
        }
        return LogChangeKinds.Updated.ToString();
    }
    static DateTime floor(DateTime t, LogActivityInterval interval) => interval switch {
        LogActivityInterval.Hour => new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc),
        LogActivityInterval.Day => new DateTime(t.Year, t.Month, t.Day, 0, 0, 0, DateTimeKind.Utc),
        LogActivityInterval.Week => new DateTime(t.Year, t.Month, t.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(-(((int)t.DayOfWeek + 6) % 7)),
        _ => new DateTime(t.Year, t.Month, 1, 0, 0, 0, DateTimeKind.Utc),
    };
    static DateTime step(DateTime t, LogActivityInterval interval) => interval switch {
        LogActivityInterval.Hour => t.AddHours(1),
        LogActivityInterval.Day => t.AddDays(1),
        LogActivityInterval.Week => t.AddDays(7),
        _ => t.AddMonths(1),
    };
    static int bucketOf(List<DateTime> starts, DateTime at) {
        int lo = 0, hi = starts.Count - 1, found = -1;
        while (lo <= hi) {
            var mid = (lo + hi) >>> 1;
            if (starts[mid] <= at) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return found;
    }

    // ------------------------------------------------------------------ one version by time

    /// <summary>The node as the save at <paramref name="timestamp"/> left it (as it was, when the save deleted it), read from the log; null when the index or the file does not have it.</summary>
    public (string source, INodeDataInternal node)? VersionAt(Guid nodeId, long timestamp, Func<HistorySourceState, IHistoryLogFile?> fileOf) {
        ChangeRow? found = null;
        HistorySourceState? source = null;
        _lock.EnterReadLock();
        try {
            if (_state == null) return null;
            var f = files;
            foreach (var v in views()) {
                var i = v.Changes.LowerBound(timestamp, v.Rows);
                if (i >= v.Rows || v.Changes[i] != timestamp) continue;
                var reader = new RowReader(f, v.Source.Name);
                for (; i < v.Rows && v.Changes[i] == timestamp; i++) {
                    var r = reader.Change(i, v.Rows);
                    if (r.Node != nodeId) continue;
                    if (found == null || (found.Value.AfterLen == 0 && r.AfterLen > 0)) {
                        found = r;
                        source = v.Source;
                    }
                }
                if (found != null) break;
            }
        } finally {
            _lock.ExitReadLock();
        }
        if (found == null || source == null) return null;
        var row = found.Value;
        var pos = row.AfterLen > 0 ? row.AfterPos : row.BeforePos;
        var len = row.AfterLen > 0 ? row.AfterLen : row.BeforeLen;
        var file = fileOf(source);
        if (file == null || len <= 0) return null;
        var buffer = new byte[len];
        if (!file.Read(pos, len, buffer)) return null;
        var node = FromBytes.NodeData(_store.Datamodel, new MemoryStream(buffer, false), null);
        return (source.Key, node);
    }
}
