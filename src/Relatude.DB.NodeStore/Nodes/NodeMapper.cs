using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Query;
using Relatude.DB.Query.Expressions;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Relatude.DB.Nodes;
/// <summary>
/// A mapper that can map between INodeData and model objects, and vice versa.
/// It is using runtime generated libraries to optimize the performance.
/// It is also used to get the node type id and property ids from model objects in queries.
/// </summary>
public class NodeMapper {
    readonly Dictionary<Guid, IValueMapper> _nodeValueMapperByTypeId;
    readonly Dictionary<Type, KeyValuePair<IValueMapper, Guid>> _mapperByType;
    readonly Dictionary<Guid, Type> _typeByNodeTypeId;
    readonly NodeStore _store;
    public NodeMapper(Dictionary<Guid, Type> typesById, NodeStore store) {
        _nodeValueMapperByTypeId = typesById.ToDictionary(kv => kv.Key, kv => {
            var m = Activator.CreateInstance(kv.Value);
            if (m == null) throw new NullReferenceException("Unable to create mapper for: " + kv.Value);
            return (IValueMapper)m;
        });
        _typeByNodeTypeId = typesById.ToDictionary(kv => kv.Key, kv => kv.Value);
        _store = store;
        _mapperByType = new();
    }
    public PropertyModel GetProperty<T>(Expression<Func<T, object>> expression) {
        var exp = expression.Body;
        if (exp is UnaryExpression unExp && unExp.NodeType == ExpressionType.Convert) {
            exp = unExp.Operand;
        }
        if (exp is not MemberExpression memExp) throw new Exception("Only property references accepted. ");
        var propertyName = memExp.Member.Name;
        return GetProperty<T>(propertyName);
    }
    public PropertyModel GetProperty<T, TProperty>(Expression<Func<T, TProperty>> expression) {
        var exp = expression.Body;
        if (exp is UnaryExpression unExp && unExp.NodeType == ExpressionType.Convert) {
            exp = unExp.Operand;
        }
        if (exp is not MemberExpression memExp) throw new Exception("Only property references accepted. ");
        var propertyName = memExp.Member.Name;
        var prop = GetProperty<T>(propertyName);
        return prop;
    }
    public Guid GetNodeTypeId(Type nodeType) {
        lock (_mapperByType) {
            if (_mapperByType.TryGetValue(nodeType, out var kv)) return kv.Value;
        }
        getNodeValueMapper(nodeType, out var typeId);
        return typeId;
    }
    public Type GetNodeType(Guid nodeTypeId) => _typeByNodeTypeId[nodeTypeId];
    public bool TryGetNodeType(Guid nodeTypeId, [MaybeNullWhen(false)] out Type type) => _typeByNodeTypeId.TryGetValue(nodeTypeId, out type);
    public PropertyModel GetProperty<T>(string propertyName) {
        return _store.Datastore.Datamodel.NodeTypes[GetNodeTypeId(typeof(T))].AllPropertiesByName[propertyName];
    }
    IValueMapper getNodeValueMapper(Type objectType, out Guid typeId) {
        IValueMapper? mapper;
        lock (_mapperByType) {
            if (_mapperByType.TryGetValue(objectType, out var kv)) {
                typeId = kv.Value;
                mapper = kv.Key;
            } else {
                var orgType = objectType;
                if (objectType.InheritsFromOrImplements<INodeShellAccess>()) {
                    objectType = objectType.GetInterfaces().First(); // Use the first interface as the type for interface implementations
                }
                var guidS = DatamodelExtensions.GetOrCreateNodeAttributeWithId(objectType).Id
                    ?? throw new NullReferenceException("Unable create id for: " + objectType);
                typeId = Guid.Parse(guidS);
                if (!_nodeValueMapperByTypeId.TryGetValue(typeId, out mapper)) {
                    if (this._store.Datastore.Datamodel.NodeTypesByFullName.TryGetValue(objectType.FullName!, out var typeDef)) {
                        typeId = typeDef.Id;
                        mapper = null; // No mapper defined for this type
                    } else {
                        throw new Exception(objectType.FullName + " is not part of the datamodel. ");
                    }
                }
                _mapperByType.Add(orgType, new(mapper!, typeId));
            }
        }
        return mapper!;
    }
    public object CreateObjectFromNodeData(INodeDataOuter nodeData) {
        if (!_nodeValueMapperByTypeId.TryGetValue(nodeData.NodeType, out var mapper)) {
            throw new Exception(nodeData.NodeType + " is not part of the datamodel. ");
        }
        return mapper.NodeDataToObject(nodeData, _store);
    }
    public T CreateObjectFromNodeData<T>(INodeDataOuter nodeData) {
        if (!_nodeValueMapperByTypeId.TryGetValue(nodeData.NodeType, out var mapper)) {
            throw new Exception(nodeData.NodeType + " is not part of the datamodel. ");
        }
        return (T)mapper.NodeDataToObject(nodeData, _store);
    }
    public bool TryGetIdGuidAndCreateIfPossible(object node, out Guid id) {
        return getNodeValueMapper(node.GetType(), out _).TryGetIdGuidAndCreateIfPossible(node, out id);
    }
    public Guid GetIdGuidOrCreate(object? node) {
        if (node == null) throw new ArgumentNullException(nameof(node));
        if (TryGetIdGuidAndCreateIfPossible(node, out var id)) return id;
        throw new Exception("Unable to determine id for: " + node.GetType().FullName);
    }
    public bool TryGetIdGuid(object node, out Guid id) {
        return getNodeValueMapper(node.GetType(), out _).TryGetIdGuid(node, out id);
    }
    public Guid GetIdGuid(object node) {
        if (TryGetIdGuid(node, out var id)) return id;
        throw new Exception("Unable to get id for: " + node.GetType().FullName);
    }
    public bool TryGetIdUInt(object node, out int id) {
        return getNodeValueMapper(node.GetType(), out _).TryGetIdUInt(node, out id);
    }
    public INodeData CreateNodeDataFromObject(object node, RelatedCollection? relatedCollection) {
        return getNodeValueMapper(node.GetType(), out _).CreateNodeDataFromObject(node, relatedCollection);
    }
    public Guid GetRelationId<T>() => GetRelationId(typeof(T));
    public Guid GetRelationId(Type type) {
        if (_store.Datastore.Datamodel.RelationIdByType.TryGetValue(type, out var id)) return id;
        throw new Exception("Unable to find relation id for type: " + type.FullName);
    }

    public T NewObjectFromType<T>() => CreateObjectFromType<T>(Guid.NewGuid());
    public T CreateObjectFromType<T>(Guid guid) {
        var typeId = GetNodeTypeId(typeof(T));
        var nowUtc = DateTime.UtcNow;
        var nodeData = new NodeData(guid, 0, typeId, nowUtc, nowUtc, new Properties<object>(10));
        return CreateObjectFromNodeData<T>(nodeData);
    }
}
