using WAF.Datamodels;
using WAF.Datamodels.Properties;
using WAF.Query.Expressions;
using System.Linq.Expressions;
using System.Reflection;

namespace WAF.Nodes;
public class NodeMapper {  // threadsafe, and a faster alternative to System.Activator
    readonly Dictionary<Guid, IValueMapper> _nodeValueMapperByTypeId;
    readonly Dictionary<Type, KeyValuePair<IValueMapper, Guid>> _mapperByType;
    readonly NodeStore _store;
    public NodeMapper(Dictionary<Guid, Type> typesById, NodeStore store) {
        _nodeValueMapperByTypeId = typesById.ToDictionary(kv => kv.Key, kv => {
            var m = Activator.CreateInstance(kv.Value);
            if (m == null) throw new NullReferenceException("Unable to create mapper for: " + kv.Value);
            return (IValueMapper)m;
        });
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
                var guidS = DatamodelExtensions.GetOrCreateNodeAttributeWithId(objectType).Id
                    ?? throw new NullReferenceException("Unable create id for: " + objectType);
                typeId = Guid.Parse(guidS);
                if (!_nodeValueMapperByTypeId.TryGetValue(typeId, out mapper)) {
                    throw new Exception(objectType.FullName + " is not part of the datamodel. ");
                }
                _mapperByType.Add(objectType, new(mapper, typeId));
            }
        }
        return mapper;
    }
    public object CreateObjectFromNodeData(INodeData nodeData) {
        if (!_nodeValueMapperByTypeId.TryGetValue(nodeData.NodeType, out var mapper)) {
            throw new Exception(nodeData.NodeType + " is not part of the datamodel. ");
        }
        return mapper.NodeDataToObject(nodeData, _store);
    }
    public T CreateObjectFromNodeData<T>(INodeData nodeData) {
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
    public Guid GetRelationId<T>() {
        var type = typeof(T);
        if (_store.Datastore.Datamodel.RelationIdByType.TryGetValue(type, out var id)) return id;
        throw new Exception("Unable to find relation id for type: " + type.FullName);
    }
}
