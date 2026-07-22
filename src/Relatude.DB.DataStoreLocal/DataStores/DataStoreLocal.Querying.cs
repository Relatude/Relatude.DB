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
    public INodeDataExternal ToOuter(INodeDataInternal nodeDataInner, QueryContext? ctx) {
        ctx ??= _defaultQueryCtx;
        var ctxKey = _nativeModelStore.GetQueryContextKey(ctx, out var now);
        return _definition.NodeTypeIndex.PickBestOuter(nodeDataInner, ctxKey, now);
    }
    public INodeDataExternal[] ToOuter(INodeDataInternal[] nodeDataInners, QueryContext? ctx) {
        ctx ??= _defaultQueryCtx;
        var ctxKey = _nativeModelStore.GetQueryContextKey(ctx, out var now);
        var index = _definition.NodeTypeIndex;
        var result = new INodeDataExternal[nodeDataInners.Length];
        for (var i = 0; i < nodeDataInners.Length; i++) {
            result[i] = index.PickBestOuter(nodeDataInners[i], ctxKey, now);
        }
        return result;
    }
    public IEnumerable<INodeDataExternal> ToOuter(IEnumerable<INodeDataInternal> nodeDataInners, QueryContext? ctx) {
        ctx ??= _defaultQueryCtx;
        var ctxKey = _nativeModelStore.GetQueryContextKey(ctx, out var now);
        var index = _definition.NodeTypeIndex;
        foreach (var nodeDataInner in nodeDataInners) {
            yield return index.PickBestOuter(nodeDataInner, ctxKey, now);
        }
    }
    public int GetId(Guid id) => _guids.GetId(id);
    public Guid GetGuid(int id) => _guids.GetGuid(id);
    public Task<INodeDataExternal> GetAsync(Guid id, QueryContext? ctx = null) {
        if (id == Guid.Empty) throw new Exception("Guid cannot be empty.");
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            Interlocked.Increment(ref _noNodeGetsSinceClearCache);
            _queryActivity.Record();
            return Task.FromResult(ToOuter(_nodes.Get(_guids.GetId(id), out _), ctx));
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
    }
    public Task<INodeDataExternal> GetAsync(int id, QueryContext? ctx = null) {
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            _queryActivity.Record();
            Interlocked.Increment(ref _noNodeGetsSinceClearCache);
            return Task.FromResult(ToOuter(_nodes.Get(id, out _), ctx));
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
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
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            _queryActivity.Record();
            Interlocked.Increment(ref _noNodeGetsSinceClearCache);
            return ToOuter(_nodes.Get(_guids.GetId(id), out _), ctx);
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
    }
    public INodeDataExternal Get(int id, QueryContext? ctx = null) {
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            Interlocked.Increment(ref _noNodeGetsSinceClearCache);
            return ToOuter(_nodes.Get(id, out _), ctx);
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
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
        if (this.TryGet(id, out var nodeData)) {
            meta = new NodeMeta(nodeData);
            return true;
        }
        meta = null;
        return false;
    }
    public bool TryGetNodeMeta(int id, [MaybeNullWhen(false)] out NodeMeta meta, QueryContext? ctx = null) {
        if (this.TryGet(id, out var nodeData)) {
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
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            Interlocked.Increment(ref _noNodeGetsSinceClearCache);
            _queryActivity.Record();
            if (_guids.TryGetId(id, out var uid)) {
                nodeData = ToOuter(_nodes.Get(uid, out _), ctx);
                return true;
            }
            nodeData = null;
            return false;
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
    }
    public bool TryGet(int id, [MaybeNullWhen(false)] out INodeDataExternal nodeData, QueryContext? ctx = null) {
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            Interlocked.Increment(ref _noNodeGetsSinceClearCache);
            _queryActivity.Record();
            if (_nodes.TryGet(id, out var nodeDataInner, out _)) {
                nodeData = ToOuter(nodeDataInner, ctx);
                return true;
            } else {
                nodeData = null;
                return false;
            }
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
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
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            var result = _nodes.Get(__ids.ToArray()); // must return a copy to avoid problems with locks
            Interlocked.Add(ref _noNodeGetsSinceClearCache, result.Length);
            return ToOuter(result, ctx);
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
    }
    public IEnumerable<INodeDataExternal> Get(IEnumerable<Guid> ids, QueryContext? ctx = null) {
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            var result = _nodes.Get(ids.Select(_guids.GetId).ToArray()); // must return a copy to avoid problems with locks
            Interlocked.Add(ref _noNodeGetsSinceClearCache, result.Length);
            _queryActivity.Record();
            return ToOuter(result, ctx);
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
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
    public INodeDataExternal[] GetRelatedNodesFromPropertyId(Guid propertyId, Guid from, QueryContext? ctx = null) {
        var propDef = _definition.Datamodel.Properties[propertyId];
        if (propDef is not RelationPropertyModel relProp) throw new ArgumentException("Property is not a relation property");
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            _queryActivity.Record();
            if (!_guids.TryGetId(from, out var fromId)) return [];
            var relation = _definition.Relations[relProp.RelationId];
            var relatedIds = relation.GetRelated(fromId, relProp.FromTargetToSource);
            return ToOuter(_nodes.Get(relatedIds.ToArray()), ctx);
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
    }
    public bool TryGetRelatedNodeFromPropertyId(Guid propertyId, Guid from, [MaybeNullWhen(false)] out INodeDataExternal node, QueryContext? ctx = null) {
        var propDef = _definition.Datamodel.Properties[propertyId];
        if (propDef is not RelationPropertyModel relProp) throw new ArgumentException("Property is not a relation property");
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            _queryActivity.Record();
            if (!_guids.TryGetId(from, out var fromId)) {
                node = null;
                return false;
            }
            var relation = _definition.Relations[relProp.RelationId];
            var relatedIds = relation.GetRelated(fromId, relProp.FromTargetToSource);
            if (relatedIds.Count == 0) {
                node = null;
                return false;
            }
            node = ToOuter(_nodes.Get(relatedIds.First(), out _), ctx);
            return true;
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
    }    
    public int GetRelatedCountFromPropertyId(Guid propertyId, Guid from, QueryContext? ctx = null) {
        var propDef = _definition.Datamodel.Properties[propertyId];
        if (propDef is not RelationPropertyModel relProp) throw new ArgumentException("Property is not a relation property");
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            _queryActivity.Record();
            if (!_guids.TryGetId(from, out var fromId)) return 0;
            var relation = _definition.Relations[relProp.RelationId];
            return relation.GetRelated(fromId, relProp.FromTargetToSource).Count;
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
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            ctx ??= _defaultQueryCtx;
            validateDatabaseState();
            _queryActivity.Record();
            var sw = Stopwatch.StartNew();
            var scope = _variables.CreateQueryBaseScope(parameters, ctx);
            var result = expression.Evaluate(scope);
            if (result is IIncludeBranches nd) nd.EnsureRetrivalOfRelationNodesDataBeforeExitingReadLock(scope.Metrics);
            sw.Stop();
            var durationMs = (double)sw.Elapsed.Ticks / TimeSpan.TicksPerMillisecond;
            var resultCount = 1;
            if (result is ICollectionBase t) {
                t.DurationMs = durationMs;
                resultCount = t.Count;
            }
            if (_logger.LoggingQueries) _logger.RecordQuery(query ?? expression.ToString()!, sw.Elapsed, resultCount, scope.Metrics);
            Interlocked.Increment(ref _noQueriesSinceClearCache);
            Interlocked.Increment(ref _noQueriesSinceLastMetric);
            return result;
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
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
