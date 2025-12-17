using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.Query;
using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;
using Relatude.DB.Query.Parsing.Expressions;
using Relatude.DB.Query.Parsing.Tokens;
namespace Relatude.DB.DataStores;

public sealed partial class DataStoreLocal : IDataStore {
    internal FastRollingCounter _queryActivity = new();
    public Task<INodeData> GetAsync(Guid id, QueryContext? ctx = null) {
        if (id == Guid.Empty) throw new Exception("Guid cannot be empty.");
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            Interlocked.Increment(ref _noNodeGetsSinceClearCache);
            _queryActivity.Record();
            return Task.FromResult(_nodes.Get(_guids.GetId(id)));
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
    }
    public Task<INodeData> GetAsync(int id, QueryContext? ctx = null) {
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            _queryActivity.Record();
            Interlocked.Increment(ref _noNodeGetsSinceClearCache);
            return Task.FromResult(_nodes.Get(id));
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
    }
    public Task<IEnumerable<INodeData>> GetAsync(IEnumerable<int> __ids, QueryContext? ctx = null) {
        return Task.FromResult(Get(__ids));
    }
    public INodeData Get(Guid id, QueryContext? ctx = null) {
        if (id == Guid.Empty) throw new Exception("Guid cannot be empty.");
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            _queryActivity.Record();
            Interlocked.Increment(ref _noNodeGetsSinceClearCache);
            return _nodes.Get(_guids.GetId(id));
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
    }
    public INodeData Get(int id, QueryContext? ctx = null) {
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            Interlocked.Increment(ref _noNodeGetsSinceClearCache);
            return _nodes.Get(id);
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
    }
    public INodeData Get(IdKey id, QueryContext? ctx = null) {
        if (id.Int == 0) return Get(id.Guid);
        return Get(id.Int);
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
    public Guid GetNodeType(IdKey id) {
        if (id.Int == 0) return GetNodeType(id.Guid);
        return GetNodeType(id.Int);
    }
    public Dictionary<IdKey, Guid> GetNodeType(IEnumerable<IdKey> ids) {
        _lock.EnterReadLock();
        try {
            validateDatabaseState();
            var result = new Dictionary<IdKey, Guid>();
            foreach (var idKey in ids) {
                int id = idKey.Int == 0 ? _guids.GetId(idKey.Guid) : idKey.Int;
                result[idKey] = _definition.GetTypeOfNode(id);
            }
            return result;
        } finally {
            _lock.ExitReadLock();
        }
    }
    public bool TryGet(Guid id, [MaybeNullWhen(false)] out INodeData nodeData, QueryContext? ctx = null) {
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            Interlocked.Increment(ref _noNodeGetsSinceClearCache);
            _queryActivity.Record();
            if (_guids.TryGetId(id, out var uid)) {
                nodeData = _nodes.Get(uid);
                return true;
            }
            nodeData = null;
            return false;
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
    }
    public bool TryGet(int id, [MaybeNullWhen(false)] out INodeData nodeData, QueryContext? ctx = null) {
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            Interlocked.Increment(ref _noNodeGetsSinceClearCache);
            _queryActivity.Record();
            return _nodes.TryGet(id, out nodeData, out _);
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
    public IEnumerable<INodeData> Get(IEnumerable<int> __ids, QueryContext? ctx = null) {
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            var result = _nodes.Get(__ids.ToArray()); // must return a copy to avoid problems with locks
            Interlocked.Add(ref _noNodeGetsSinceClearCache, result.Length);
            return result;
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
    }
    public IEnumerable<INodeData> Get(IEnumerable<Guid> ids, QueryContext? ctx = null) {
        _lock.EnterReadLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Querying);
        try {
            validateDatabaseState();
            var result = _nodes.Get(ids.Select(_guids.GetId).ToArray()); // must return a copy to avoid problems with locks
            Interlocked.Add(ref _noNodeGetsSinceClearCache, result.Length);
            _queryActivity.Record();
            return result;
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
    }
    public bool ExistsAndIsType(Guid id, Guid nodeTypeId) {
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
    public bool ContainsRelation(Guid relationId, Guid from, Guid to, bool fromTargetToSource) {
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
    public INodeData[] GetRelatedNodesFromPropertyId(Guid propertyId, Guid from, QueryContext? ctx = null) {
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
            return _nodes.Get(relatedIds.ToArray());
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
    }
    public bool TryGetRelatedNodeFromPropertyId(Guid propertyId, Guid from, [MaybeNullWhen(false)] out INodeData node) {
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
            node = _nodes.Get(relatedIds.First());
            return true;
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitReadLock();
        }
    }
    public int GetRelatedCountFromPropertyId(Guid propertyId, Guid from) {
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
    public IEnumerable<Guid> GetRelatedNodeIdsFromRelationId(Guid relationId, Guid from, bool fromTargetToSource) {
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
        var result = this.query(expressionTree, query, parameters, ctx);
        return result;

    }
    public Task<object?> QueryAsync(string query, IEnumerable<Parameter> parameters, QueryContext? userCtx = null) {
        return Task.FromResult(Query(query, parameters));
    }
}
