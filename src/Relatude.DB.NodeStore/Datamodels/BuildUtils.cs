using Relatude.DB.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Relatude.DB.Nodes;
using Relatude.DB.Query;

namespace Relatude.DB.Datamodels;
// Extensions neede for building model from types and compiling model classes
internal static class BuildUtils {
    public static bool TryGetAttribute<T>(Type type, [MaybeNullWhen(false)] out T attribute) where T : Attribute {
        var matches = type.GetCustomAttributes<T>();
        var count = matches.Count();
        if (count == 0) {
            attribute = null;
            return false;
        }
        attribute = matches.First();
        return true;
    }
    public static RelationModel CreateRelationModelFromType(Type type) {
        var r = new RelationModel();
        var relationAttr = GetOrCreateRelationAttributeWithId(type);
        if (relationAttr.Id == null) throw new NullReferenceException();
        r.Id = Guid.Parse(relationAttr.Id);
        r.Namespace = type.Namespace;
        r.CodeName = type.Name;
        r.RelationClassType = type;
        if (type.InheritsFromOrImplements<IManyMany>()) r.RelationType = RelationType.ManyMany;
        else if (type.InheritsFromOrImplements<IManyToMany>()) r.RelationType = RelationType.ManyToMany;
        else if (type.InheritsFromOrImplements<IOneOne>()) r.RelationType = RelationType.OneOne;
        else if (type.InheritsFromOrImplements<IOneToMany>()) r.RelationType = RelationType.OneToMany;
        else if (type.InheritsFromOrImplements<IOneToOne>()) r.RelationType = RelationType.OneToOne;
        else throw new Exception(type.FullName + " is not a known relation type. ");
        if (type.BaseType == null) throw new Exception();
        switch (r.RelationType) {
            case RelationType.OneOne:
            case RelationType.ManyMany:
                var nodeType = type.BaseType.GetGenericArguments()[0];
                r.SourceTypes.Add(getNodeTypeId(nodeType));
                r.TargetTypes.Add(getNodeTypeId(nodeType));
                break;
            case RelationType.OneToOne:
            case RelationType.OneToMany:
            case RelationType.ManyToMany:
                var nodeTypes = type.BaseType.GetGenericArguments();
                r.SourceTypes.Add(getNodeTypeId(nodeTypes[0]));
                r.TargetTypes.Add(getNodeTypeId(nodeTypes[1]));
                break;
            default:
                throw new Exception(type.FullName + " is not a known relation type. ");
        }
        if (relationAttr.SourceTypes != null && relationAttr.SourceTypes.Any()) {
            r.SourceTypes.AddRange(relationAttr.SourceTypes.Select(t => Guid.Parse(t)));
        }
        if (relationAttr.TargetTypes != null && relationAttr.TargetTypes.Any()) {
            r.TargetTypes.AddRange(relationAttr.TargetTypes.Select(t => Guid.Parse(t)));
        }
        r.SourceTypes = r.SourceTypes.Distinct().ToList();
        r.TargetTypes = r.TargetTypes.Distinct().ToList();
        return r;
    }
    public static NodeTypeModel CreateNodeTypeModelFromType(Type type) {
        var c = new NodeTypeModel();
        var nodeAttr = GetOrCreateNodeAttributeWithId(type);
        if (nodeAttr.Id == null) throw new NullReferenceException();
        c.Id = Guid.Parse(nodeAttr.Id);
        c.Namespace = type.Namespace;
        c.CodeName = type.Name;
        c.ModelType = getModelType(type);
        if (nodeAttr.TextIndex != BoolValue.Default) c.TextIndex = nodeAttr.TextIndex == BoolValue.True;
        c.TextIndexBoost = nodeAttr.TextIndexBoost;
        if (nodeAttr.InstantTextIndexing != BoolValue.Default) c.InstantTextIndexing = nodeAttr.InstantTextIndexing == BoolValue.True;
        if (nodeAttr.SemanticIndex != BoolValue.Default) c.SemanticIndex = nodeAttr.SemanticIndex == BoolValue.True;
        List<Type> types = [.. type.GetInterfaces()];
        if (type.BaseType != null) types.Add(type.BaseType);
        c.Parents = types.Where(t => isTypeRelevant(t)).Select(t => getNodeTypeId(t)).ToList();

        // first, gather all public fields and properties:
        var all = new List<MemberInfo>();

        foreach (var f in type.GetFields()) {
            firstTestForIllegalTypes(f.FieldType, f);
            if (f.IsPublic) all.Add(f);
        }
        foreach (var p in type.GetProperties()) {
            firstTestForIllegalTypes(p.PropertyType, p);
            if (p.GetGetMethod(true) != null) all.Add(p);
        }

        // then, inlcude only members that are defined for the first time in this class/interface/record,
        // this means, members that are implementations of interfaces will be excluded:
        // the definition of these members are stored on the noteTypeModel for the interface            
        var filtered = all.Where(m => GetBaseDeclaringType(m) == type);

        foreach (MemberInfo m in filtered) {
            var valueType = m is PropertyInfo ? ((PropertyInfo)m).PropertyType : ((FieldInfo)m).FieldType;
            if (isIdPropertyThenAssignIt(c, m, valueType)) continue;
            if (isSystemPropertyThenAssignIt(c, m, valueType)) continue;
            var property = BuildUtilsProperties.CreatePropertyFromMember(m, valueType);
            c.Properties.Add(property.Id, property);
        }
        return c;
    }
    public static RelationAttribute GetOrCreateRelationAttributeWithId(Type type) {
        if (!tryGetAttribute<RelationAttribute>(type, out var attr)) attr = new RelationAttribute();
        if (attr.Id == null) {
            attr.Id = (type.FullName + string.Empty).GenerateGuid().ToString();
        } else {
            if (!Guid.TryParse(attr.Id, out _)) throw new Exception("Specified guid (" + attr.Id + ") for " + type.FullName + " is not a valid guid. ");
        }
        return attr;
    }
    public static NodeAttribute GetOrCreateNodeAttributeWithId(Type type) {
        if (!tryGetAttribute<NodeAttribute>(type, out var attr)) attr = new NodeAttribute();
        if (attr.Id == null) {
            attr.Id = (type.FullName + string.Empty).GenerateGuid().ToString();
        } else {
            if (!Guid.TryParse(attr.Id, out _)) throw new Exception("Specified guid (" + attr.Id + ") for " + type.FullName + " is not a valid guid. ");
        }
        return attr;
    }
    static Type[] knownSupportedValueTypes = [typeof(bool), typeof(byte), typeof(int), typeof(long), typeof(double), typeof(decimal), 
        typeof(DateTime), typeof(DateTimeOffset), typeof(Guid), typeof(TimeSpan)];
    static void firstTestForIllegalTypes(Type valueType, MemberInfo member) {
        if (valueType.IsEnum) return;
        if (valueType.IsValueType) {
            if (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(Nullable<>)) {
                throw new Exception("Nullable types are unsupported, property type: " + valueType.FullName);
            }
            if (!knownSupportedValueTypes.Contains(valueType)) {
                throw new Exception("The type \"" + valueType.GetCSharpName()+ "\" of member \""
                    + member.DeclaringType!.Name + "." + member.Name
                    + "\" is not supported. ");
            }
        }
        // non value types are either ok like arrays etc., or relations to types/models not known yet, so cannot check them here...
    }
    public static Guid GetOrCreateNodeTypeId(Type type) {
        if (tryGetAttribute<NodeAttribute>(type, out var attr) && attr.Id != null) {
            if (!Guid.TryParse(attr.Id, out var guid))
                throw new Exception("Specified guid (" + attr.Id + ") for " + type.FullName + " is not a valid guid. ");
            return guid;
        }
        return (type.FullName + string.Empty).GenerateGuid();
    }
    public static Guid GetOrCreateRelationId(Type type) {
        if (tryGetAttribute<RelationAttribute>(type, out var attr) && attr.Id != null) {
            if (!Guid.TryParse(attr.Id, out var guid))
                throw new Exception("Specified guid (" + attr.Id + ") for " + type.FullName + " is not a valid guid. ");
            return guid;
        }
        return (type.FullName + string.Empty).GenerateGuid();
    }
    public static bool tryGetAttribute<T>(MemberInfo type, [MaybeNullWhen(false)] out T attribute) where T : Attribute {
        var matches = type.GetCustomAttributes<T>();
        var count = matches.Count();
        if (count == 0) {
            attribute = null;
            return false;
        }
        attribute = matches.First();
        return true;
    }
    static Guid getNodeTypeId(Type type) {
        return Guid.Parse(GetOrCreateNodeAttributeWithId(type).Id ?? throw new NullReferenceException());
    }
    static IEnumerable<Type> findParentsIncludingThis(Type t) {
        HashSet<Type> parents = new();
        addParents(t, parents);
        parents.Add(t);
        return parents;
    }
    static void addParents(Type t, HashSet<Type> parents) {
        if (parents.Contains(t)) return;
        foreach (var i in t.GetInterfaces()) {
            addParents(i, parents);
            parents.Add(i);
        }
        if (t.BaseType != null && t.BaseType == typeof(object)) {
            addParents(t.BaseType, parents);
            parents.Add(t.BaseType);
        }
    }
    public static Type GetBaseDeclaringType(MemberInfo member) {
        if (member.DeclaringType is null) throw new NullReferenceException("Null value on declaring type. ");
        IEnumerable<Type> parents = findParentsIncludingThis(member.DeclaringType);
        var interfacesWithMember = parents.Where(i => i.IsInterface && i.GetMembers().Where(m => m.Name == member.Name).Count() > 0).ToList();
        var classesOrRecsWithMember = parents.Where(c => c.IsClass && c.GetMembers().Where(m => m.Name == member.Name).Count() > 0).ToList();
        if (interfacesWithMember.Count > 1) throw new Exception("Multiple interfaces cannot specify same property:" + string.Join(", ", interfacesWithMember.Select(c => c.FullName + "." + member.Name)));
        if (interfacesWithMember.Count == 1) return interfacesWithMember.First();
        if (classesOrRecsWithMember.Count > 1) throw new Exception("Multiple classes cannot specify same property, overriding is not supported:" + string.Join(", ", classesOrRecsWithMember.Select(c => c.FullName + "." + member.Name)));
        if (classesOrRecsWithMember.Count == 1) return classesOrRecsWithMember.First();
        throw new Exception("Unable to locate a root type for member " + member.Name);
    }
    static bool hasAttr<T>(MemberInfo pInfo) where T : Attribute { return tryGetAttribute<T>(pInfo, out _); }
    static bool isIdPropertyThenAssignIt(NodeTypeModel c, MemberInfo pInfo, Type valueType) {
        var publicIdName = hasAttr<PublicIdPropertyAttribute>(pInfo) ? pInfo.Name : NodeTypeModel.DefaultPublicIdPropertyName;
        var internalIdName = hasAttr<InternalIdPropertyAttribute>(pInfo) ? pInfo.Name : NodeTypeModel.DefaultInternalIdPropertyName;
        if (pInfo.Name == publicIdName) {
            if (valueType == typeof(Guid)) {
                c.NameOfPublicIdProperty = pInfo.Name;
                c.DataTypeOfPublicId = DataTypePublicId.Guid;
                return true;
            } else if (valueType == typeof(string)) {
                c.NameOfPublicIdProperty = pInfo.Name;
                c.DataTypeOfPublicId = DataTypePublicId.String;
                return true;
            } else {
                if (hasAttr<PublicIdPropertyAttribute>(pInfo)) throw new Exception("Incompatible datatype on ID property");
            }
        }
        if (pInfo.Name == internalIdName) {
            if (valueType == typeof(int)) {
                c.NameOfInternalIdProperty = pInfo.Name;
                c.DataTypeOfInternalId = DataTypeInternalId.UInt;
                return true;
            } else if (valueType == typeof(int)) {
                c.NameOfInternalIdProperty = pInfo.Name;
                c.DataTypeOfInternalId = DataTypeInternalId.Int;
                return true;
            } else if (valueType == typeof(long)) {
                c.NameOfInternalIdProperty = pInfo.Name;
                c.DataTypeOfInternalId = DataTypeInternalId.Long;
                return true;
            } else if (valueType == typeof(string)) {
                c.NameOfInternalIdProperty = pInfo.Name;
                c.DataTypeOfInternalId = DataTypeInternalId.String;
                return true;
            } else {
                if (hasAttr<InternalIdPropertyAttribute>(pInfo)) throw new Exception("Incompatible datatype on internal ID property");
            }
        }
        return false;
    }
    static bool isSystemPropertyThenAssignIt(NodeTypeModel c, MemberInfo pInfo, Type valueType) {
        if (hasAttr<ChangedUtcPropertyAttribute>(pInfo)) {
            if (valueType == typeof(DateTime)) {
                c.NameOfChangedUtcProperty = pInfo.Name;
                return true;
            } else {
                if (hasAttr<ChangedUtcPropertyAttribute>(pInfo)) throw new Exception("Incompatible datatype on changedUtc property");
            }
        }
        if (hasAttr<CreatedUtcPropertyAttribute>(pInfo)) {
            if (valueType == typeof(DateTime)) {
                c.NameOfCreatedUtcProperty = pInfo.Name;
                return true;
            } else {
                if (hasAttr<CreatedUtcPropertyAttribute>(pInfo)) throw new Exception("Incompatible datatype on createdUtc property");
            }
        }
        return false;
    }
    static ModelType getModelType(Type type) {
        if (type.IsInterface) {
            return ModelType.Interface;
        } else if (((TypeInfo)type).DeclaredProperties.Any(x => x.Name == "EqualityContract")) { // kind of "hackish", bit not that critical if class is used instead...
            return ModelType.Record;
        } else if (type.IsClass) {
            return ModelType.Class;
        } else if (type.IsValueType && !type.IsPrimitive && !type.IsEnum) {
            return ModelType.Struct;
        } else {
            throw new Exception(type.FullName + " is not a valid model type, it is a \"" + type.BaseType + "\". A model must be a class, interface, record or struct. ");
        }
    }
    static bool isTypeRelevant(Type type) {
        if (type == typeof(object)) return false;
        if (type.IsGenericType) return false; // filter out "IEquatable<T>" , probably a better way of doing it...
        return true;
    }
}
