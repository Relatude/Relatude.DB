using Relatude.DB.Common;
using System.Reflection;
using Relatude.DB.Nodes;
using Relatude.DB.Query;

namespace Relatude.DB.Datamodels;
// Extensions neede for building model from types and compiling model classes
public static class DatamodelExtensions {
    static bool excludeTypeAsNodeType(Type type) {
        if (type.IsAbstract && type.IsSealed) return true; // ignore static classes
        if (type.IsEnum) return true;
        return false;

    }
    public static void AddNamespace<T>(this Datamodel datamodel, bool autoDeduceRelations = false) {
        var assembly = typeof(T).Assembly;
        var namespaces = typeof(T).Namespace;
        if (namespaces == null) throw new Exception("The type " + typeof(T).FullName + " has no namespace, so it cannot be used to add a namespace of model types. "
            + "Move the model types into a namespace, or add them individually with Add<T>(). ");
        foreach (var type in assembly.GetTypes()) {
            if (excludeTypeAsNodeType(type)) continue;
            if (type.Namespace != namespaces) continue;
            datamodel.Add(type, true, autoDeduceRelations);
        }
    }
    public static void AddAssembly(this Datamodel datamodel, Assembly assembly, string nameSpace, bool autoDeduceRelations = false) {
        if (nameSpace == null) throw new Exception("A namespace is required when adding model types from the assembly " + assembly.GetName().Name
            + ". Specify the namespace that contains the model types. ");
        foreach (var type in assembly.GetTypes()) {
            if (excludeTypeAsNodeType(type)) continue;
            if (type.Namespace != nameSpace) continue;
            datamodel.Add(type, true, autoDeduceRelations);
        }
    }
    public static void Add<T>(this Datamodel datamodel, bool includeAllReferencedModels = true, bool autoDeduceRelations = false) {
        datamodel.Add(typeof(T), includeAllReferencedModels, autoDeduceRelations);
    }
    public static void Add(this Datamodel datamodel, Type type, bool includeAllReferencedModels = true, bool autoDeduceRelations = false) {
        if (includeAllReferencedModels) foreach (var refType in getRefTypes(type)) datamodel.addType(refType, autoDeduceRelations);
        datamodel.addType(type, autoDeduceRelations);
    }
    static void addType(this Datamodel datamodel, Type t, bool autoDeduceRelations) {
        if (datamodel.HasInitialized()) throw new Exception("Cannot add the type " + t.FullName + " to the datamodel: the datamodel is already initialized. "
            + "All types must be added before the store is created. ");
        if (t.InheritsFromOrImplements<IRelationProperty>()) return; // skip
        if (t.GetCustomAttribute<ExcludeAttribute>() != null) return; // skip excluded types
        if (t.InheritsFromOrImplements<IRelation>()) {
            var r = BuildUtils.CreateRelationModelFromType(t);
            if (datamodel.Relations.ContainsKey(r.Id)) return;
            r.DatamodelSourceId = datamodel.CurrentSourceId;
            datamodel.Relations.Add(r.Id, r);
        } else {
            bool noZeroArgConstructor = t.GetConstructors().All(c => c.GetParameters().Length > 0);
            if (!t.IsInterface && noZeroArgConstructor) {
                throw new Exception("The node type " + t.FullName + " must have a public parameterless constructor, "
                    + "so instances can be created when reading nodes from the store. ");
            }
            var c = BuildUtils.CreateNodeTypeModelFromType(t, autoDeduceRelations);
            if (datamodel.NodeTypes.TryGetValue(c.Id, out var c2)) {
                if (c.FullName == c2.FullName) return; // ignore, allow same class more than one time
                throw new Exception("The types " + c.FullName + " and " + c2.FullName + " have the same id " + c.Id + ". "
                    + "Node type ids must be unique - this usually comes from a copy-pasted Id in a [Node] attribute. Give one of them a new id. ");
            }
            c.DatamodelSourceId = datamodel.CurrentSourceId;
            datamodel.NodeTypes.Add(c.Id, c);
        }
        // remember Assembly Reference:
        datamodel.Assemblies.Add(t.Assembly);
    }
    static HashSet<Type> standardPropertyObjectTypes = [ // not relations
        typeof(string),typeof(string[]), typeof(DateTime), typeof(DateTimeOffset), typeof(Guid), typeof(Guid[]), typeof(TimeSpan), typeof(object), typeof(byte[]), typeof(decimal)
        , typeof(GeoCoordinate)
        , typeof(FileValue)
        , typeof(IEmbedded)
        , typeof(IEmbeddedMap)
    ];
    static HashSet<Type> getRefTypes(Type t) {
        var types = new HashSet<Type>();
        getReferencedTypes(t, types);
        return types;
    }
    static void getReferencedTypes(Type t, HashSet<Type> types) {
        if (types.Contains(t)) return;
        if (t == typeof(NodeMeta))
            return; // ignore special class
        types.Add(t);
        foreach (var m in t.GetMembers()) {
            var type = m is FieldInfo f ? f.FieldType : m is PropertyInfo p ? p.PropertyType : null;
            if (type == null) continue;
            if (type.InheritsFromOrImplementsAny(standardPropertyObjectTypes)) continue;
            if (type.IsPrimitive)
                continue;
            if (type.IsEnum) continue;
            if (type.IsGenericType) {
                foreach (var g in type.GetGenericArguments()) {
                    getReferencedTypes(g, types);
                }
            } else if (type.IsArray) {
                getReferencedTypes(type.GetElementType()!, types);
            } else {
                getReferencedTypes(type, types);
            }
        }
    }
    internal static NodeAttribute GetOrCreateNodeAttributeWithId(Type type) {
        if (!BuildUtils.TryGetAttribute<NodeAttribute>(type, out var attr)) attr = new NodeAttribute();
        if (attr.Id == null) {
            attr.Id = (type.FullName + string.Empty).GenerateHashGuid().ToString();
        } else {
            if (!Guid.TryParse(attr.Id, out _)) throw new Exception("Specified guid (" + attr.Id + ") for " + type.FullName + " is not a valid guid. ");
        }
        return attr;
    }
}
