using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Query;
using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;
using Relatude.DB.Query.Parsing.Expressions;
using Relatude.DB.Query.Parsing.Tokens;
namespace Relatude.DB.DataStores;

public sealed partial class DataStoreLocal : IDataStore {
    internal FastRollingCounter _queryActivity = new();

    /// <summary>
    /// Whether the words of a string property can be counted (see <see cref="IDataStore"/>). A
    /// lookup and a type test, no reading and no locking: the answer follows from the model and the
    /// engine the property's index sits on, both fixed for as long as the database is open.
    /// </summary>
    public bool CanCountWords(Guid propertyId, QueryContext? ctx = null)
        => _definition.Properties.TryGetValue(propertyId, out var property)
            && property is Definitions.PropertyTypes.IWordCountProperty counter
            && counter.CanCountWords(ctx ?? QueryContext.Default);
    public TimeSpan EstimateWordCountDuration(Guid propertyId, WordCountOptions options, QueryContext? ctx = null)
        => _definition.Properties.TryGetValue(propertyId, out var property) && property is Definitions.PropertyTypes.IWordCountProperty counter
            ? counter.EstimateCountWordsDuration(options, ctx ?? QueryContext.Default) : TimeSpan.Zero;
    /// <summary>
    /// What a reader in a context sees of the nodes: the context is resolved here, under the store lock, and
    /// the converter then reads nothing but the node it is given - so it can run after the lock is let go.
    /// </summary>
    internal OuterConverter GetOuterConverter(QueryContext? ctx) {
        ctx ??= _defaultQueryCtx;
        var ctxKey = _nativeModelStore.GetQueryContextKey(ctx, out var now);
        return new(_definition.NodeTypeIndex, ctxKey, now);
    }
    public INodeDataExternal ToOuter(INodeDataInternal nodeDataInner, QueryContext? ctx) {
        return GetOuterConverter(ctx).ToOuter(nodeDataInner);
    }
    public INodeDataExternal[] ToOuter(INodeDataInternal[] nodeDataInners, QueryContext? ctx) {
        var outer = GetOuterConverter(ctx);
        var result = new INodeDataExternal[nodeDataInners.Length];
        for (var i = 0; i < nodeDataInners.Length; i++) {
            result[i] = outer.ToOuter(nodeDataInners[i]);
        }
        return result;
    }
    public IEnumerable<INodeDataExternal> ToOuter(IEnumerable<INodeDataInternal> nodeDataInners, QueryContext? ctx) {
        var outer = GetOuterConverter(ctx);
        foreach (var nodeDataInner in nodeDataInners) {
            yield return outer.ToOuter(nodeDataInner);
        }
    }
    enum NodeLookup { ById, ByGuid, ByGuidIfKnown }
    /// <summary>
    /// One node, read the way a query reads: looked up under the read lock - a cached node is done right
    /// there - and otherwise read from the log after the lock is let go, so a writer is not kept waiting
    /// for the disk. Unknown guids throw for <see cref="NodeLookup.ByGuid"/>, as the guid map always did;
    /// a missing node throws unless <paramref name="throwIfMissing"/> is off.
    /// </summary>
    bool readOne(NodeLookup lookup, int id, Guid guid, bool throwIfMissing, QueryContext? ctx, [MaybeNullWhen(false)] out INodeDataExternal node) {
        long activityId = 0;
        Stores.NodeStore.NodeRead? read = null;
        try {
            OuterConverter outer;
            enterReadLock(ctx ?? _defaultQueryCtx);
            try {
                activityId = RegisterActvity(DataStoreActivityCategory.Querying);
                validateDatabaseState();
                Interlocked.Increment(ref _noNodeGetsSinceClearCache);
                _queryActivity.Record();
                if (lookup == NodeLookup.ByGuid) {
                    id = _guids.GetId(guid);
                } else if (lookup == NodeLookup.ByGuidIfKnown && !_guids.TryGetId(guid, out id)) {
                    node = null;
                    return false;
                }
                outer = GetOuterConverter(ctx);
                read = _nodes.BeginRead([id], throwIfMissing);
            } finally {
                _lock.ExitReadLock();
            }
            var inner = read.Complete(CancellationToken.None)[0]; // no store lock taken from here on
            if (inner == null) {
                node = null;
                return false;
            }
            node = outer.ToOuter(inner);
            return true;
        } finally {
            read?.Dispose();
            if (activityId != 0) DeRegisterActivity(activityId);
        }
    }
    /// <summary>
    /// Many nodes, read the way a query reads: <paramref name="lookupUnderLock"/> finds their ids under the
    /// read lock, and what the cache does not hold is read from the log after the lock is let go.
    /// </summary>
    INodeDataExternal[] readMany(QueryContext? ctx, Func<int[]> lookupUnderLock) {
        long activityId = 0;
        Stores.NodeStore.NodeRead? read = null;
        try {
            OuterConverter outer;
            enterReadLock(ctx ?? _defaultQueryCtx);
            try {
                activityId = RegisterActvity(DataStoreActivityCategory.Querying);
                validateDatabaseState();
                _queryActivity.Record();
                var ids = lookupUnderLock();
                Interlocked.Add(ref _noNodeGetsSinceClearCache, ids.Length);
                if (ids.Length == 0) return [];
                outer = GetOuterConverter(ctx);
                read = _nodes.BeginRead(ids, throwIfMissing: true);
            } finally {
                _lock.ExitReadLock();
            }
            var inner = read.Complete(CancellationToken.None); // no store lock taken from here on
            var result = new INodeDataExternal[inner.Length];
            for (var i = 0; i < inner.Length; i++) result[i] = outer.ToOuter(inner[i]!);
            return result;
        } finally {
            read?.Dispose();
            if (activityId != 0) DeRegisterActivity(activityId);
        }
    }
    public int GetId(Guid id) => _guids.GetId(id);
    public Guid GetGuid(int id) => _guids.GetGuid(id);
    public Task<INodeDataExternal> GetAsync(Guid id, QueryContext? ctx = null) {
        if (id == Guid.Empty) throw new Exception("Guid cannot be empty.");
        readOne(NodeLookup.ByGuid, 0, id, true, ctx, out var node);
        return Task.FromResult(node!);
    }
    public Task<INodeDataExternal> GetAsync(int id, QueryContext? ctx = null) {
        readOne(NodeLookup.ById, id, Guid.Empty, true, ctx, out var node);
        return Task.FromResult(node!);
    }
    public Task<IEnumerable<INodeDataExternal>> GetAsync(IEnumerable<int> __ids, QueryContext? ctx = null) {
        return Task.FromResult(Get(__ids, ctx));
    }
    public T GetValue<T>(PropertyPath path, QueryContext? ctx = null) {
        if (!TryGetValue<T>(path, out var value, ctx)) throw new Exception("Property value not found. Node or property does not exists. ");
        return value;
    }
    public INodeDataExternal Get(NodePath path, QueryContext? ctx = null) {
        if (TryGet(path, out var node, ctx)) return node;
        throw new Exception("Node not found.");
    }
    public bool TryGet(NodePath path, [MaybeNullWhen(false)] out INodeDataExternal node, QueryContext? ctx = null) {
        if (path.Path.Length == 0) {
            if (path.NodeKey.HasInt) return TryGet(path.NodeKey.Int, out node, ctx);
            else if (path.NodeKey.HasGuid) return TryGet(path.NodeKey.Guid, out node, ctx);
            else throw new Exception("NodePath has no NodeKey.");
        } else {
            bool found;
            if (path.NodeKey.HasInt) found = TryGet(path.NodeKey.Int, out node, ctx);
            else if (path.NodeKey.HasGuid) found = TryGet(path.NodeKey.Guid, out node, ctx);
            else throw new Exception("NodePath has no NodeKey.");
            if (!found || node == null) return false;
            return node.TryGetInnerNode(path, out node);
        }
    }
    public bool TryGetValue<T>(PropertyPath path, [MaybeNullWhen(false)] out T value, QueryContext? ctx = null) {
        if (TryGet(path.NodePath, out var node, ctx)) {
            if (node.TryGetValue(path.PropertyId, out var v)) {
                if (v is not T) {
                    var propName = Datamodel.Properties[path.PropertyId].CodeName;
                    throw new Exception("Value is not a value of the expected type: "
                        + typeof(T).FullName + " for property: " + propName);
                }
                value = (T)v;
                return true;
            }
        }
        value = default;
        return false;
    }

    public INodeDataExternal Get(Guid id, QueryContext? ctx = null) {
        if (id == Guid.Empty) throw new Exception("Guid cannot be empty.");
        readOne(NodeLookup.ByGuid, 0, id, true, ctx, out var node);
        return node!;
    }
    public INodeDataExternal Get(int id, QueryContext? ctx = null) {
        readOne(NodeLookup.ById, id, Guid.Empty, true, ctx, out var node);
        return node!;
    }
    public INodeDataExternal Get(NodeKey id, QueryContext? ctx = null) {
        if (id.HasGuid) return Get(id.Guid, ctx);
        return Get(id.Int, ctx);
    }
    public bool TryGetNodeType(Guid id, out Guid nodeTypeId) {
        _lock.EnterReadLock();
        try {
            validateDatabaseState();
            if (_guids.TryGetId(id, out var uid)) {
                nodeTypeId = _definition.GetTypeOfNode(uid);
                return true;
            }
            nodeTypeId = Guid.Empty;
            return false;
        } finally {
            _lock.ExitReadLock();
        }

    }
    public Guid GetNodeType(Guid id) {
        _lock.EnterReadLock();
        try {
            validateDatabaseState();
            return _definition.GetTypeOfNode(_guids.GetId(id));
        } finally {
            _lock.ExitReadLock();
        }
    }
    public Guid GetNodeType(int id) {
        _lock.EnterReadLock();
        try {
            validateDatabaseState();
            return _definition.GetTypeOfNode(id);
        } finally {
            _lock.ExitReadLock();
        }
    }
    public Guid GetNodeType(NodeKey id) {
        if (id.Int == 0) return GetNodeType(id.Guid);
        return GetNodeType(id.Int);
    }
    public bool TryGetNodeMeta(Guid id, [MaybeNullWhen(false)] out NodeMeta meta, QueryContext? ctx = null) {
        if (this.TryGet(id, out var nodeData, ctx)) {
            meta = new NodeMeta(nodeData);
            return true;
        }
        meta = null;
        return false;
    }
    public bool TryGetNodeMeta(int id, [MaybeNullWhen(false)] out NodeMeta meta, QueryContext? ctx = null) {
        if (this.TryGet(id, out var nodeData, ctx)) {
            meta = new NodeMeta(nodeData);
            return true;
        }
        meta = null;
        return false;
    }
    public bool TryGetNodeMeta(NodeKey id, [MaybeNullWhen(false)] out NodeMeta meta, QueryContext? ctx = null) {
        if (id.Int == 0) return TryGetNodeMeta(id.Guid, out meta, ctx);
        return TryGetNodeMeta(id.Int, out meta, ctx);
    }
    Guid? getBestCultureId(QueryContext? ctx) {
        ctx ??= _defaultQueryCtx;
        if (ctx.CultureId.HasValue) return ctx.CultureId.Value;
        if (ctx.CultureCode != null) {
            if (_nativeModelStore.TryGetCultureId(string.Intern(ctx.CultureCode), out var cultureId)) return cultureId;
        }
        return null;
    }
    public Dictionary<NodeKey, Guid> GetNodeType(IEnumerable<NodeKey> ids) {
        _lock.EnterReadLock();
        try {
            validateDatabaseState();
            var result = new Dictionary<NodeKey, Guid>();
            foreach (var idKey in ids) {
                int id = idKey.Int == 0 ? _guids.GetId(idKey.Guid) : idKey.Int;
                result[idKey] = _definition.GetTypeOfNode(id);
            }
            return result;
        } finally {
            _lock.ExitReadLock();
        }
    }
    public bool TryGet(Guid id, [MaybeNullWhen(false)] out INodeDataExternal nodeData, QueryContext? ctx = null) {
        // the guid map may still resolve a recently deleted node, so the node fetch must also
        // be a try (matching the int overload below) for TryGet to honor its contract
        return readOne(NodeLookup.ByGuidIfKnown, 0, id, false, ctx, out nodeData);
    }
    public bool TryGet(int id, [MaybeNullWhen(false)] out INodeDataExternal nodeData, QueryContext? ctx = null) {
        return readOne(NodeLookup.ById, id, Guid.Empty, false, ctx, out nodeData);
    }
    public bool TryGetGuid(int id, out Guid guid, QueryContext? ctx = null) {
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            Interlocked.Increment(ref _noNodeGetsSinceClearCache);
            _queryActivity.Record();
            return _guids.TryGetId(id, out guid);
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
    }
    public IEnumerable<INodeDataExternal> Get(IEnumerable<int> __ids, QueryContext? ctx = null) {
        return readMany(ctx, () => __ids.ToArray()); // the result is a copy: nothing in it depends on the lock
    }
    public IEnumerable<INodeDataExternal> Get(IEnumerable<Guid> ids, QueryContext? ctx = null) {
        return readMany(ctx, () => ids.Select(_guids.GetId).ToArray());
    }
    public bool Exists(int id, QueryContext? ctx = null) {
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            _queryActivity.Record();
            return _nodes.Contains(id);
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
    }
    public bool Exists(Guid id, QueryContext? ctx = null) {
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            _queryActivity.Record();
            if (!_guids.TryGetId(id, out var uid)) return false;
            return _nodes.Contains(uid);
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
    }
    public bool ExistsAndIsType(Guid id, Guid nodeTypeId, QueryContext? ctx = null) {
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            _queryActivity.Record();
            if (!_guids.TryGetId(id, out var uid)) return false;
            if (!_nodes.Contains(uid)) return false;
            var actualTypeId = _definition.GetTypeOfNode(uid);
            NodeTypeModel actualType = _definition.Datamodel.NodeTypes[actualTypeId];
            return actualType.ThisAndAllInheritedTypes.ContainsKey(nodeTypeId);
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
    }
    public bool ContainsRelation(Guid relationId, Guid from, Guid to, bool fromTargetToSource, QueryContext? ctx = null) {
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            _queryActivity.Record();
            if (!_guids.TryGetId(from, out var fromId) || !_guids.TryGetId(to, out var toId)) return false;
            return _definition.Relations[relationId].Contains(fromId, toId, fromTargetToSource);
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
    }
    // The related nodes the context may read, filtered through the same sets queries filter with (read access,
    // publication, deletion, hidden types), so a relation property read off a node shows a reader what a query
    // would. Under the read lock, as the sets are.
    int[] relatedForContext(RelationPropertyModel relProp, Guid from, QueryContext ctx, int max = int.MaxValue) {
        if (!_guids.TryGetId(from, out var fromId)) return [];
        var related = _definition.Relations[relProp.RelationId].GetRelated(fromId, relProp.FromTargetToSource);
        var readable = new List<int>();
        foreach (var id in related.Enumerate()) {
            if (_definition.NodeTypeIndex.TryGetType(id, out var typeId) && _definition.GetAllIdsForType(typeId, ctx).Has(id)) {
                readable.Add(id);
                if (readable.Count == max) break;
            }
        }
        return [.. readable];
    }
    static RelationPropertyModel relationProperty(Datamodel datamodel, Guid propertyId) =>
        datamodel.Properties[propertyId] as RelationPropertyModel ?? throw new ArgumentException("Property is not a relation property");

    public INodeDataExternal[] GetRelatedNodesFromPropertyId(Guid propertyId, Guid from, QueryContext? ctx = null) {
        var relProp = relationProperty(_definition.Datamodel, propertyId);
        return readMany(ctx, () => relatedForContext(relProp, from, ctx ?? _defaultQueryCtx));
    }
    public bool TryGetRelatedNodeFromPropertyId(Guid propertyId, Guid from, [MaybeNullWhen(false)] out INodeDataExternal node, QueryContext? ctx = null) {
        var relProp = relationProperty(_definition.Datamodel, propertyId);
        var nodes = readMany(ctx, () => relatedForContext(relProp, from, ctx ?? _defaultQueryCtx, max: 1));
        node = nodes.Length > 0 ? nodes[0] : null;
        return node != null;
    }
    public int GetRelatedCountFromPropertyId(Guid propertyId, Guid from, QueryContext? ctx = null) {
        var relProp = relationProperty(_definition.Datamodel, propertyId);
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            _queryActivity.Record();
            return relatedForContext(relProp, from, ctx ?? _defaultQueryCtx).Length;
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
    }
    public IEnumerable<Guid> GetRelatedNodeIdsFromRelationId(Guid relationId, Guid from, bool fromTargetToSource, QueryContext? ctx = null) {
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            if (!_guids.TryGetId(from, out var fromId)) return [];
            return _definition.Relations[relationId].GetRelated(fromId, fromTargetToSource).Enumerate().Select(id => _guids.GetGuid(id)).ToArray();
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
    }
    object? query(IExpression expression, string? query, IEnumerable<Parameter> parameters, QueryContext? ctx) {
        ctx ??= _defaultQueryCtx;
        object? result;
        int resultCount = 1;
        Metrics metrics;
        long activityId = 0;
        PendingRetrieval? pending = null;
        var sw = Stopwatch.StartNew();
        // the context the query runs with: cancelled by the caller's token, or by the time limit
        using var limit = new QueryLimit(ctx, _settings.MaxQueryDurationInSeconds);
        var runCtx = limit.Context;
        try {
            limit.WaitingForLock = true;
            enterReadLock(runCtx);
            limit.WaitingForLock = false;
            try {
                activityId = RegisterActvity(DataStoreActivityCategory.Querying);
                validateDatabaseState();
                _queryActivity.Record();
                runCtx.CancellationToken.ThrowIfCancellationRequested();
                var scope = _variables.CreateQueryBaseScope(parameters, runCtx);
                result = expression.Evaluate(scope);
                if (result is IDeferredNodeRetrieval deferred) pending = deferred.BeginRetrievalUnderReadLock(scope.Metrics);
                else if (result is IIncludeBranches nd) nd.EnsureRetrivalOfRelationNodesDataBeforeExitingReadLock(scope.Metrics);
                // counted here while the lock is held, unless the count needs the nodes still to be read
                if (pending == null && result is ICollectionBase c) resultCount = c.Count;
                metrics = scope.Metrics;
            } finally {
                _lock.ExitReadLock();
            }
            // The node bytes are read after the read lock is let go of. A writer waiting for the lock holds
            // up every reader that comes after it, so a query reading a lot of nodes from the log would keep
            // them all waiting for the disk. Which nodes, and where in the log, was settled under the lock,
            // so the result is the same. No store lock may be taken from here on (see LogReadGate).
            if (pending != null) {
                pending.Complete(runCtx.CancellationToken);
                if (result is ICollectionBase c) resultCount = c.Count;
            }
        } catch (OperationCanceledException) when (limit.TimedOut) {
            throw limit.TimeoutError();
        } finally {
            pending?.Dispose();
            if (activityId != 0) DeRegisterActivity(activityId);
        }
        sw.Stop();
        var duration = sw.Elapsed;
        if (result is ICollectionBase t) t.DurationMs = (double)duration.Ticks / TimeSpan.TicksPerMillisecond;
        Interlocked.Increment(ref _noQueriesSinceClearCache);
        Interlocked.Increment(ref _noQueriesSinceLastMetric);
        // Recorded once the read lock is let go of, for the same reason: writing a log entry is no reason
        // to keep a writer, and every reader behind it, waiting.
        if (_logger.LoggingQueries) _logger.RecordQuery(query ?? expression.ToString()!, duration, resultCount, metrics);
        warnIfManyNodesReadFromDisk(metrics, query, expression, duration);
        return result;
    }
    /// <summary>
    /// How long one wait for the read lock lasts before a query looks at its cancellation and its lock-wait
    /// budget again: short, so a Stop is felt at once, at the cost of a wake-up per slice while it waits.
    /// </summary>
    const int lockWaitSliceMs = 50;
    /// <summary>
    /// The read lock, as a query or a read takes it. A transaction holds the write lock for as long as it
    /// runs, and EnterReadLock can be neither cancelled nor timed out, so a query behind a long transaction
    /// used to wait it out whatever its caller wanted - the admin UI's Stop and MaxQueryDurationInSeconds
    /// included. Here the wait is taken in slices, and ends the way the query would: cancelled by its token,
    /// which also carries the time limit, or with <see cref="StoreBusyException"/> once the context's
    /// <see cref="QueryContext.LockWait"/> has passed. A context with neither waits the plain way.
    /// </summary>
    void enterReadLock(QueryContext ctx) {
        if (_lock.TryEnterReadLock(0)) return;
        var token = ctx.CancellationToken;
        var budget = ctx.LockWait;
        if (budget == null && !token.CanBeCanceled) {
            _lock.EnterReadLock();
            return;
        }
        var waited = Stopwatch.StartNew();
        while (true) {
            token.ThrowIfCancellationRequested();
            var slice = lockWaitSliceMs;
            if (budget is TimeSpan b) {
                var left = (long)(b - waited.Elapsed).TotalMilliseconds;
                if (left <= 0) throw new StoreBusyException(waited.Elapsed);
                if (left < slice) slice = (int)left;
            }
            if (_lock.TryEnterReadLock(slice)) return;
        }
    }
    /// <summary>
    /// The context a query runs with. Without a time limit or a cancellable token it is the caller's own;
    /// otherwise a copy whose token is cancelled by the caller's token or when the limit passes. The token
    /// source is disposed when the query returns, after which the token never fires - a result that reads
    /// lazily later, with this context, is not cancelled by a timer that outlived its query. For the same
    /// reason the token is only ever polled, never registered with.
    /// </summary>
    sealed class QueryLimit : IDisposable {
        readonly CancellationTokenSource? _cts;
        readonly CancellationToken _callerToken;
        readonly double _seconds;
        public QueryContext Context { get; }
        public QueryLimit(QueryContext ctx, double seconds) {
            _callerToken = ctx.CancellationToken;
            _seconds = seconds > 0 ? seconds : 0;
            if (_seconds == 0 && !_callerToken.CanBeCanceled) {
                Context = ctx;
                return;
            }
            _cts = _callerToken.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(_callerToken) : new CancellationTokenSource();
            if (_seconds > 0) _cts.CancelAfter(TimeSpan.FromSeconds(_seconds));
            Context = ctx.WithCancellation(_cts.Token);
        }
        public bool TimedOut => _seconds > 0 && _cts != null && _cts.IsCancellationRequested && !_callerToken.IsCancellationRequested;
        /// <summary>Whether the query is still waiting for the store lock, which decides what a time-out says.</summary>
        public bool WaitingForLock { get; set; }
        public TimeoutException TimeoutError() => WaitingForLock
            ? new("The query was stopped after " + _seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " seconds, the limit set by the setting MaxQueryDurationInSeconds, while it waited for a transaction to finish. ")
            : new("The query was stopped after " + _seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " seconds, the limit set by the setting MaxQueryDurationInSeconds. Page the result, filter on indexed properties, or raise the limit. ");
        public void Dispose() => _cts?.Dispose();
    }
    long _lastDiskReadWarningTicks = long.MinValue;
    // A query that reads this many nodes from the log is slow, holds the disk and pushes what other readers
    // use out of the node cache - worth a line in the system log, though not one per query.
    void warnIfManyNodesReadFromDisk(Metrics metrics, string? query, IExpression expression, TimeSpan duration) {
        var threshold = _settings.WarnWhenQueryReadsNodesFromDisk;
        if (threshold <= 0 || metrics.NodesReadFromDisk < threshold) return;
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastDiskReadWarningTicks);
        if (last != long.MinValue && now - last < 60_000) return; // at most one a minute
        if (Interlocked.CompareExchange(ref _lastDiskReadWarningTicks, now, last) != last) return; // another query got there first
        var text = query ?? expression.ToString() ?? "";
        if (text.Length > 1000) text = text[..1000] + "...";
        LogWarning("A query read " + metrics.NodesReadFromDisk.To1000N() + " nodes from the log file in " + duration.TotalMilliseconds.To1000N() + "ms"
            + " (the warning threshold, WarnWhenQueryReadsNodesFromDisk, is " + threshold.To1000N() + "). "
            + "Such a query is slow and pushes what other queries use out of the node cache: page the result, or filter on indexed properties. "
            + "Further warnings like this are held back for a minute. ", text);
    }
    public object? Query(string query, IEnumerable<Parameter> parameters, QueryContext? ctx = null) {
        var syntaxTree = TokenParser.Parse(query, parameters);
        var expressionTree = ExpressionTreeBuilder.Build(syntaxTree, Datamodel);
        return this.query(expressionTree, query, parameters, ctx);
    }
    public Task<object?> QueryAsync(string query, IEnumerable<Parameter> parameters, QueryContext? ctx = null) {
        return Task.FromResult(Query(query, parameters, ctx));
    }
    public NodeDataRevision[] GetRevisions(Guid nodeId, QueryContext? ctx = null) {
        _lock.EnterReadLock();
        try {
            var nodeData = _nodes.Get(_guids.GetId(nodeId), out _);
            if (nodeData is NodeDataRevisions revs) {
                return revs.Revisions;
            } else if (nodeData is NodeData nd) {
                return [nd.CopyAndConvertToNodeDataRevision(nd.Meta, Guid.Empty)];
            } else {
                throw new Exception("Unexpected node data type");
            }
        } finally {
            _lock.ExitReadLock();
        }
    }
}
/// <summary>
/// Turns stored node data into what a reader in a context sees: the right revision for its culture and
/// release window. Made under the store lock (<see cref="DataStoreLocal.GetOuterConverter"/>), where the
/// context is resolved against the users and cultures; converting reads nothing but the node, so it may
/// run after the lock is let go.
/// </summary>
internal readonly struct OuterConverter(Indexes.Meta.NodeTypesByIds index, QueryContextKey key, DateTime nowUtc) {
    public INodeDataExternal ToOuter(INodeDataInternal node) => index.PickBestOuter(node, key, nowUtc);
}
