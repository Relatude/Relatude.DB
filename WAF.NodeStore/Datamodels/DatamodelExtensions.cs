using WAF.Common;
using WAF.Query.ExpressionToString.ZSpitz.Extensions;
using System.Reflection;
using WAF.Nodes;

namespace WAF.Datamodels;
// Extensions neede for building model from types and compiling model classes
public static class DatamodelExtensions {
    static bool excludeTypeAsNodeType(Type type) { 
        if (type.IsAbstract && type.IsSealed) return true; // ignore static classes
        if (type.IsEnum) return true; 
        return false;
    
    }
    public static void AddNamespace<T>(this Datamodel datamodel) {
        var assembly = typeof(T).Assembly;
        var namespaces = typeof(T).Namespace;
        if (namespaces == null) throw new Exception("Namespace not found for type " + typeof(T).FullName);
        foreach (var type in assembly.GetTypes()) {
            if(excludeTypeAsNodeType(type)) continue;
            if (type.Namespace != namespaces) continue;
            datamodel.Add(type, true);
        }
    }
    public static void AddAssembly(this Datamodel datamodel, Assembly assembly, string nameSpace) {
        if(nameSpace == null) throw new Exception("Namespace not found for assembly " + assembly.FullName);
        foreach (var type in assembly.GetTypes()) {
            if (excludeTypeAsNodeType(type)) continue;
            if (type.Namespace != nameSpace) continue;
            datamodel.Add(type, true);
        }
    }
    public static void Add<T>(this Datamodel datamodel, bool includeAllReferencedModels = true) {
        datamodel.Add(typeof(T), includeAllReferencedModels);
    }
    public static void Add(this Datamodel datamodel, Type type, bool includeAllReferencedModels = true) {
        if (includeAllReferencedModels) foreach (var refType in getRefTypes(type)) datamodel.addType(refType);
        datamodel.addType(type);
    }
    static void addType(this Datamodel datamodel, Type t) {
        if(datamodel.HasInitialized()) throw new Exception("Datamodel is already initialized. Cannot add more types. " + t.FullName);
        if (t.InheritsFromOrImplements<IRelation>()) {
            var r = BuildUtils.CreateRelationModelFromType(t);
            if (datamodel.Relations.ContainsKey(r.Id)) return;
            datamodel.Relations.Add(r.Id, r);
        } else {
            var c = BuildUtils.CreateNodeTypeModelFromType(t);
            if (datamodel.NodeTypes.TryGetValue(c.Id, out var c2)) {
                if (c.FullName == c2.FullName) return; // ignore, allow same class more than one time
                throw new Exception($"Different types have same Id: {c.FullName} and {c2.FullName} have the same ID: {c.Id}");
            }
            datamodel.NodeTypes.Add(c.Id, c);
        }
        // remember Assembly Reference:
        datamodel.Assemblies.Add(t.Assembly);
    }
    static HashSet<Type> standardPropetyObjectTypes = new() {
        typeof(string),typeof(string[]), typeof(DateTime), typeof(Guid), typeof(TimeSpan), typeof(object), typeof(byte[]), typeof(decimal), typeof(FileValue)
    };
    static HashSet<Type> getRefTypes(Type t) {
        var types = new HashSet<Type>();
        getReferencedTypes(t, types);
        return types;
    }
    static void getReferencedTypes(Type t, HashSet<Type> types) {
        if (types.Contains(t)) return;
        types.Add(t);
        foreach (var m in t.GetMembers()) {
            var type = m is FieldInfo f ? f.FieldType : m is PropertyInfo p ? p.PropertyType : null;
            if (type == null) continue;
            if (standardPropetyObjectTypes.Contains(type)) continue;
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
            attr.Id = (type.FullName + string.Empty).GenerateGuid().ToString();
        } else {
            if (!Guid.TryParse(attr.Id, out _)) throw new Exception("Specified guid (" + attr.Id + ") for " + type.FullName + " is not a valid guid. ");
        }
        return attr;
    }
}
