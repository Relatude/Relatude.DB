using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores;
using Relatude.DB.Serialization;
using System.Diagnostics;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// How big the nodes of one database are, as stored: the storage page's node size dialog.
///
/// Every node has a segment in the log file - where its current bytes start and how many there are -
/// so the size of every node is known without reading any of them. The overview is built from those
/// segments alone: the spread of sizes, per type and in total, and where in the log file the nodes
/// that are still current sit. Only the lists read nodes, and only a few: the largest of a selection
/// (to name them), one node taken apart property by property, and a sample of one type's nodes to say
/// which of its properties the bytes are in.
/// </summary>
sealed class UINodeSizes {
    readonly RelatudeDBServer _server;
    internal UINodeSizes(RelatudeDBServer server) => _server = server;

    internal void Register(UICommands commands) {
        commands.Register("node-sizes", ctx => overview(ctx.Payload<StorePayload>()));
        commands.Register("node-sizes-list", ctx => largest(ctx.Payload<ListPayload>()));
        commands.Register("node-sizes-node", ctx => node(ctx.Payload<NodePayload>()));
        commands.Register("node-sizes-type", ctx => typeAnatomy(ctx.Payload<TypePayload>()));
    }

    DataStoreLocal local(Guid storeId) {
        if (!_server.Containers.TryGetValue(storeId, out var c)) throw new Exception("Database not found. ");
        var store = c.Store ?? throw new Exception("The database must be open. ");
        if (store.State != DataStoreState.Open) throw new Exception("The database must be open. ");
        if (store.Datastore is not DataStoreLocal local) throw new Exception("Only supported for local databases. ");
        return local;
    }

    // the bars of the size histogram are this many per doubling of size, chosen so a database whose
    // nodes are all much alike still gets a picture with some shape to it, and one that spans bytes
    // to megabytes does not get a hundred slivers
    static int stepsPerOctave(int octaves) => octaves <= 5 ? 4 : octaves <= 10 ? 2 : 1;
    // the log file is cut into this many stretches for the picture of where the current nodes are
    const int positionBins = 120;

    object overview(StorePayload p) {
        var watch = Stopwatch.StartNew();
        var store = local(p.StoreId);
        var dm = store.Datamodel;
        var snapshot = store.GetNodeSizes();
        var nodes = snapshot.Nodes;
        var edges = sizeEdges(nodes);
        var buckets = edges.Length - 1;
        // where the log file ends; with the size unknown, the end of the last node stands in for it
        var fileSize = snapshot.LogFileSize;
        foreach (var n in nodes) fileSize = Math.Max(fileSize, n.Position + n.Length);
        var binSize = Math.Max(1, (fileSize + positionBins - 1) / positionBins);

        var byType = new Dictionary<Guid, TypeTally>();
        long total = 0;
        var allLengths = new int[nodes.Length];
        for (var i = 0; i < nodes.Length; i++) {
            var n = nodes[i];
            if (!byType.TryGetValue(n.NodeTypeId, out var t)) byType[n.NodeTypeId] = t = new TypeTally(buckets);
            t.Lengths.Add(n.Length);
            t.Bytes += n.Length;
            var b = bucketOf(edges, n.Length);
            t.SizeCounts[b]++;
            t.SizeBytes[b] += n.Length;
            // a node longer than a stretch of the file is spread over every stretch it covers
            var start = n.Position;
            var end = n.Position + n.Length;
            for (var bin = start / binSize; bin < positionBins && bin * binSize < end; bin++) {
                var from = Math.Max(start, bin * binSize);
                var to = Math.Min(end, (bin + 1) * binSize);
                t.Position[bin] += to - from;
            }
            total += n.Length;
            allLengths[i] = n.Length;
        }
        Array.Sort(allLengths);
        var types = byType
            .Select(kv => {
                var lengths = kv.Value.Lengths;
                lengths.Sort();
                dm.NodeTypes.TryGetValue(kv.Key, out var type);
                return new {
                    Id = kv.Key,
                    Name = type?.CodeName ?? kv.Key.ToString(),
                    Full = type?.FullName ?? kv.Key.ToString(),
                    Kind = type?.ModelType.ToString() ?? "Class",
                    Count = lengths.Count,
                    kv.Value.Bytes,
                    Stats = stats(lengths, kv.Value.Bytes),
                    kv.Value.SizeCounts,
                    kv.Value.SizeBytes,
                    kv.Value.Position,
                };
            })
            .OrderByDescending(t => t.Bytes)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new {
            snapshot.LogFileKey,
            LogFileSize = fileSize,
            Measured = nodes.Length,
            snapshot.NotYetWritten,
            TotalBytes = total,
            Stats = stats(allLengths, total),
            Edges = edges,
            BinSize = binSize,
            Bins = positionBins,
            Types = types,
            ElapsedMs = watch.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// The edges of the size histogram's bars: powers of two, a few steps per doubling, from the
    /// power below the smallest node to the one above the largest. Sizes spread over orders of
    /// magnitude, and on a straight axis every node but the few largest would be one bar at zero.
    /// </summary>
    static long[] sizeEdges(NodeSizeEntry[] nodes) {
        var min = int.MaxValue;
        var max = 0;
        foreach (var n in nodes) {
            if (n.Length < min) min = n.Length;
            if (n.Length > max) max = n.Length;
        }
        if (nodes.Length == 0) (min, max) = (64, 64);
        var lo = (int)Math.Floor(Math.Log2(Math.Max(1, min)));
        var hi = (int)Math.Floor(Math.Log2(Math.Max(1, max))) + 1; // the largest is below 2^hi
        var steps = stepsPerOctave(hi - lo);
        var edges = new List<long>();
        for (var i = 0; i <= (hi - lo) * steps; i++) {
            var edge = (long)Math.Round(Math.Pow(2, lo + (double)i / steps));
            if (edges.Count == 0 || edge > edges[^1]) edges.Add(edge); // tiny sizes round onto the same byte
        }
        if (edges.Count < 2) edges.Add(edges[0] * 2);
        return [.. edges];
    }
    // the bar a size falls in: the last edge at or below it
    static int bucketOf(long[] edges, int length) {
        var i = Array.BinarySearch(edges, (long)length);
        if (i < 0) i = ~i - 1;
        return Math.Clamp(i, 0, edges.Length - 2);
    }

    // from lengths sorted ascending; nearest-rank percentiles, so every figure is a size a node has
    static object stats(IReadOnlyList<int> sorted, long total) {
        var n = sorted.Count;
        if (n == 0) return new { Min = 0, Max = 0, Mean = 0.0, Median = 0, P90 = 0, P99 = 0 };
        int at(double q) => sorted[Math.Clamp((int)Math.Ceiling(q * n) - 1, 0, n - 1)];
        return new { Min = sorted[0], Max = sorted[n - 1], Mean = (double)total / n, Median = at(0.5), P90 = at(0.9), P99 = at(0.99) };
    }

    sealed class TypeTally(int buckets) {
        public readonly List<int> Lengths = [];
        public long Bytes;
        public readonly int[] SizeCounts = new int[buckets];
        public readonly long[] SizeBytes = new long[buckets];
        public readonly long[] Position = new long[positionBins];
    }

    // ---- the largest nodes of a selection ----

    // what is read to name the nodes listed: the largest ones can be megabytes each, and a list is
    // not worth reading a gigabyte for - past this, a node is listed by its guid
    const long nameReadBudget = 64L * 1024 * 1024;
    const int maxListed = 1000;

    object largest(ListPayload p) {
        var store = local(p.StoreId);
        var dm = store.Datamodel;
        var take = Math.Clamp(p.Take ?? 100, 1, maxListed);
        var snapshot = store.GetNodeSizes();
        // the largest few of a million: a heap of the ones kept so far, smallest on top
        var heap = new PriorityQueue<NodeSizeEntry, long>();
        var matching = 0;
        long matchingBytes = 0;
        foreach (var n in snapshot.Nodes) {
            if (p.TypeId is Guid typeId && n.NodeTypeId != typeId) continue;
            if (p.MinBytes is long min && n.Length < min) continue;
            if (p.MaxBytes is long max && n.Length >= max) continue;
            matching++;
            matchingBytes += n.Length;
            // ties broken by id, so the same database lists the same nodes every time
            var priority = ((long)n.Length << 31) - n.Id;
            if (heap.Count < take) heap.Enqueue(n, priority);
            else if (priority > peekPriority(heap)) heap.EnqueueDequeue(n, priority);
        }
        var top = new List<NodeSizeEntry>(heap.Count);
        while (heap.Count > 0) top.Add(heap.Dequeue());
        top.Reverse();
        var stored = store.ReadStoredNodes([.. top.Select(n => n.Id)], nameReadBudget).ToDictionary(s => s.Id);
        var rows = top.Select(n => {
            stored.TryGetValue(n.Id, out var s);
            dm.NodeTypes.TryGetValue(n.NodeTypeId, out var type);
            return new {
                n.Id,
                NodeId = s.NodeId,
                TypeId = n.NodeTypeId,
                TypeName = type?.CodeName ?? n.NodeTypeId.ToString(),
                Size = n.Length,
                n.Position,
                Name = s.Bytes is byte[] bytes ? nameOf(dm, bytes) : null,
            };
        }).ToArray();
        return new { Matching = matching, MatchingBytes = matchingBytes, Nodes = rows };
    }
    static long peekPriority(PriorityQueue<NodeSizeEntry, long> heap) {
        heap.TryPeek(out _, out var priority);
        return priority;
    }

    // the name a node is shown by elsewhere in the UI, from its stored bytes
    static string? nameOf(Datamodel dm, byte[] bytes) {
        try {
            var data = readable(FromBytes.NodeData(dm, new MemoryStream(bytes, false), null));
            if (data == null) return null;
            var (name, _) = UIQuery.nameOf(dm, data);
            return name == data.Id.ToString() ? null : name; // the guid is shown anyway
        } catch {
            return null; // a node the current model cannot read is still a size worth listing
        }
    }
    // a revision container has no values of its own: the published revision speaks for it
    static INodeData? readable(INodeData node) {
        if (node is not NodeDataRevisions revisions) return node;
        return revisions.Revisions.FirstOrDefault(r => r.RevisionType == RevisionType.Published) ?? revisions.Revisions.FirstOrDefault();
    }

    // ---- one node, taken apart ----

    object node(NodePayload p) {
        var store = local(p.StoreId);
        var dm = store.Datamodel;
        var stored = store.ReadStoredNodes([p.Id], long.MaxValue);
        if (stored.Length == 0 || stored[0].Bytes is not byte[] bytes) throw new Exception("The node is gone, or not written to the log yet. ");
        var s = stored[0];
        var anatomy = FromBytes.NodeDataAnatomy(bytes);
        INodeData? data = null;
        try { data = readable(FromBytes.NodeData(dm, new MemoryStream(bytes, false), null)); } catch { } // the parts are measured either way
        dm.NodeTypes.TryGetValue(s.NodeTypeId, out var type);
        return new {
            s.Id,
            s.NodeId,
            TypeId = s.NodeTypeId,
            TypeName = type?.CodeName ?? s.NodeTypeId.ToString(),
            Name = data == null ? null : UIQuery.nameOf(dm, data).Name,
            Size = s.Length,
            s.Position,
            Version = anatomy.Version.ToString(),
            anatomy.Revisions,
            Parts = parts(dm, type, anatomy.Parts, 1, data),
        };
    }

    // ---- where the bytes of one type go, from a sample of its nodes ----

    // a type's nodes are read for this; the sample is spread over the whole type, and the reading
    // stops at the budget, so a type of huge nodes costs what a type of small ones does
    const int defaultSample = 500;
    const long sampleReadBudget = 128L * 1024 * 1024;

    object typeAnatomy(TypePayload p) {
        var store = local(p.StoreId);
        var dm = store.Datamodel;
        dm.NodeTypes.TryGetValue(p.TypeId, out var type);
        var ids = store.GetNodeSizes().Nodes.Where(n => n.NodeTypeId == p.TypeId).Select(n => n.Id).ToArray();
        var count = ids.Length;
        var sample = Math.Clamp(p.Sample ?? defaultSample, 1, 10_000);
        // a fixed seed: asking twice about the same type gives the same answer
        if (ids.Length > sample) {
            new Random(12345).Shuffle(ids);
            ids = ids[..sample];
        }
        var totals = new Dictionary<(NodeAnatomyPartKind, Guid), NodeAnatomyPart>();
        var measured = 0;
        long measuredBytes = 0;
        foreach (var s in store.ReadStoredNodes(ids, sampleReadBudget)) {
            if (s.Bytes is not byte[] bytes) continue; // over the budget
            NodeAnatomy anatomy;
            try { anatomy = FromBytes.NodeDataAnatomy(bytes); } catch { continue; }
            measured++;
            measuredBytes += bytes.Length;
            foreach (var part in anatomy.Parts) {
                var key = (part.Kind, part.PropertyId);
                if (!totals.TryGetValue(key, out var sum)) {
                    totals[key] = sum = new NodeAnatomyPart { Kind = part.Kind, PropertyId = part.PropertyId, PropertyType = part.PropertyType };
                }
                sum.Bytes += part.Bytes;
                sum.ValueBytes += part.ValueBytes;
                sum.Occurrences++; // here: the nodes that have it
            }
        }
        return new {
            TypeId = p.TypeId,
            TypeName = type?.CodeName ?? p.TypeId.ToString(),
            Nodes = count,
            Sampled = measured,
            SampledBytes = measuredBytes,
            Parts = parts(dm, type, totals.Values, measured, null),
        };
    }

    // ---- the parts, as the dialog shows them ----

    static object[] parts(Datamodel dm, NodeTypeModel? type, IEnumerable<NodeAnatomyPart> parts, int nodes, INodeData? data) {
        return [.. parts
            .Where(part => part.Bytes > 0)
            .OrderByDescending(part => part.Bytes)
            .Select(part => {
                dm.Properties.TryGetValue(part.PropertyId, out var property);
                var label = part.Kind switch {
                    NodeAnatomyPartKind.Header => "Header",
                    NodeAnatomyPartKind.DisplayName => "Display name",
                    NodeAnatomyPartKind.Address => "Address",
                    NodeAnatomyPartKind.Meta => "Meta",
                    _ => property?.CodeName ?? part.PropertyId.ToString(),
                };
                // a value the type no longer has, or the model no longer knows: still stored, still
                // read past on every load, and gone at the next save of the node
                var orphan = part.Kind == NodeAnatomyPartKind.Property && (property == null || type == null || !type.AllProperties.ContainsKey(part.PropertyId))
                    ? (property == null ? "not in the model" : "not on this type")
                    : null;
                return (object)new {
                    Kind = part.Kind.ToString(),
                    part.PropertyId,
                    // where the property is declared, so the dialog can open it in the model editor
                    DeclaringTypeId = property?.NodeType,
                    Label = label,
                    PropertyType = part.Kind == NodeAnatomyPartKind.Property ? part.PropertyType.ToString() : null,
                    part.Bytes,
                    part.ValueBytes,
                    part.Occurrences,
                    Orphan = orphan,
                    Preview = data != null && part.Kind == NodeAnatomyPartKind.Property ? preview(data, part.PropertyId) : null,
                    Share = nodes > 0 ? (double)part.Occurrences / nodes : 0,
                };
            })];
    }

    // a few words on what a value is, enough to tell why it is the size it is
    static string? preview(INodeData data, Guid propertyId) {
        if (!data.TryGetValue(propertyId, out var value)) return null;
        static string clip(string s) {
            s = s.ReplaceLineEndings(" ").Trim();
            return s.Length <= 80 ? s : s[..80] + "…";
        }
        return value switch {
            string s => $"{s.Length:N0} characters: {clip(s)}",
            FileValue f when f.IsEmpty => "no file",
            FileValue f => $"{f.Name}, {f.Size:N0} bytes in the file store" + (f.TextExtract.Length > 0 ? $", {f.TextExtract.Length:N0} characters of extracted text" : ""),
            string[] a => $"{a.Length:N0} strings",
            Guid[] a => $"{a.Length:N0} ids",
            int[] a => $"{a.Length:N0} values",
            float[] a => $"{a.Length:N0} numbers",
            byte[] a => $"{a.Length:N0} bytes",
            IInnerNodeDataMap m => $"{m.Count:N0} embedded node{(m.Count == 1 ? "" : "s")}",
            _ => clip(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""),
        };
    }

    sealed record StorePayload(Guid StoreId);
    sealed record ListPayload(Guid StoreId, Guid? TypeId, long? MinBytes, long? MaxBytes, int? Take);
    sealed record NodePayload(Guid StoreId, int Id);
    sealed record TypePayload(Guid StoreId, Guid TypeId, int? Sample);
}
