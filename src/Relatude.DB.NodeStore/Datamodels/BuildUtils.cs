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
    const string relationBaseClassHint = "A relation class must inherit directly from OneOne<T>, OneToOne<TFrom, TTo>, OneToMany<TOne, TMany>, ManyToMany<TFrom, TTo> or ManyMany<T>, with node types as generic arguments. ";
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
        else throw new Exception("The relation class " + type.FullName + " does not implement a known relation type. " + relationBaseClassHint);
        var genericArgs = type.BaseType?.GetGenericArguments() ?? [];
        switch (r.RelationType) {
            case RelationType.OneOne:
            case RelationType.ManyMany:
                if (genericArgs.Length < 1) throw new Exception(
                    "Cannot read the node type of the relation class " + type.FullName + " from its base class. " + relationBaseClassHint);
                var nodeType = genericArgs[0];
                r.SourceTypes.Add(getNodeTypeId(nodeType));
                r.TargetTypes.Add(getNodeTypeId(nodeType));
                break;
            case RelationType.OneToOne:
            case RelationType.OneToMany:
            case RelationType.ManyToMany:
                if (genericArgs.Length < 2) throw new Exception(
                    "Cannot read the source and target node types of the relation class " + type.FullName + " from its base class. " + relationBaseClassHint);
                r.SourceTypes.Add(getNodeTypeId(genericArgs[0]));
                r.TargetTypes.Add(getNodeTypeId(genericArgs[1]));
                break;
            default:
                throw new Exception("The relation class " + type.FullName + " does not implement a known relation type. " + relationBaseClassHint);
        }
        // resolving code names for source and target types: ( for code generation purposes )
        evaluateCodeNameSourcesAndTargets(r, type);
        if (relationAttr.SourceTypes != null && relationAttr.SourceTypes.Any()) {
            r.SourceTypes.AddRange(relationAttr.SourceTypes.Select(t => Guid.Parse(t)));
        }
        if (relationAttr.TargetTypes != null && relationAttr.TargetTypes.Any()) {
            r.TargetTypes.AddRange(relationAttr.TargetTypes.Select(t => Guid.Parse(t)));
        }
        r.DisallowCircularReferences = relationAttr.DisallowCircularReferences;
        r.SourceTypes = r.SourceTypes.Distinct().ToList();
        r.TargetTypes = r.TargetTypes.Distinct().ToList();
        return r;
    }
    static void evaluateCodeNameSourcesAndTargets(RelationModel r, Type type) {
        // only nested relation side classes count; other nested types (helpers, enums) are allowed and ignored
        var nestedTypes = type.GetNestedTypes().Where(t => t.InheritsFromOrImplements<IRelationProperty>()).ToArray();
        if (nestedTypes.Length == 0) return; // no nested types defined
        switch (r.RelationType) {
            case RelationType.OneOne:
                assignSideNames(r, type, nestedTypes, nameof(OneOne<object>.One), null);
                break;
            case RelationType.ManyMany:
                assignSideNames(r, type, nestedTypes, nameof(ManyMany<object>.Many), null);
                break;
            case RelationType.OneToOne:
                assignSideNames(r, type, nestedTypes, nameof(OneToOne<object, object>.OneFrom), nameof(OneToOne<object, object>.OneTo));
                break;
            case RelationType.OneToMany:
                assignSideNames(r, type, nestedTypes, nameof(OneToMany<object, object>.One), nameof(OneToMany<object, object>.Many));
                break;
            case RelationType.ManyToMany:
                assignSideNames(r, type, nestedTypes, nameof(ManyToMany<object, object>.ManyFrom), nameof(ManyToMany<object, object>.ManyTo));
                break;
            default:
                throw new Exception("The relation class " + type.FullName + " does not implement a known relation type. " + relationBaseClassHint);
        }
    }
    // the nested side classes name the two ends of the relation; each must derive from the matching
    // side class of the relation base (e.g. One/Many for OneToMany). targetBaseName is null for
    // symmetric relations, which only have one side.
    static void assignSideNames(RelationModel r, Type type, Type[] nestedTypes, string sourceBaseName, string? targetBaseName) {
        string describe() => "The relation class " + type.FullName + " defines the nested side class(es) "
            + string.Join(", ", nestedTypes.Select(t => t.Name + " : " + (t.BaseType?.Name ?? "?"))) + ". ";
        if (targetBaseName == null) {
            if (nestedTypes.Length != 1 || nestedTypes[0].BaseType?.Name != sourceBaseName) throw new Exception(describe()
                + "A " + r.RelationType + " relation must define exactly one nested class deriving from " + sourceBaseName + ", naming its property side. ");
            r.CodeNameSources = nestedTypes[0].Name;
            return;
        }
        var source = nestedTypes.FirstOrDefault(t => t.BaseType?.Name == sourceBaseName);
        var target = nestedTypes.FirstOrDefault(t => t.BaseType?.Name == targetBaseName);
        if (nestedTypes.Length != 2 || source == null || target == null) throw new Exception(describe()
            + "A " + r.RelationType + " relation must define exactly two nested classes, one deriving from " + sourceBaseName
            + " and one from " + targetBaseName + ", naming the two property sides. ");
        r.CodeNameSources = source.Name;
        r.CodeNameTargets = target.Name;
    }
    public static NodeTypeModel CreateNodeTypeModelFromType(Type type, bool autoDeduceRelations = false) {
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
            if (f.GetCustomAttribute<ExcludeAttribute>() != null) continue;
            firstTestForIllegalTypes(f.FieldType, f);
            if (f.IsPublic) all.Add(f);
        }
        foreach (var p in type.GetProperties()) {
            if (p.GetCustomAttribute<ExcludeAttribute>() != null) continue;
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
            var property = BuildUtilsProperties.CreatePropertyFromMember(m, valueType, autoDeduceRelations);
            if (!c.Properties.TryAdd(property.Id, property)) {
                throw new Exception("The members \"" + c.Properties[property.Id].CodeName + "\" and \"" + m.Name + "\" on " + type.FullName
                    + " have the same property id " + property.Id + ". "
                    + "Property ids must be unique - this usually comes from a copy-pasted Id in a property attribute. Give one of them a new id. ");
            }
        }
        return c;
    }
    public static RelationAttribute GetOrCreateRelationAttributeWithId(Type type) {
        if (!tryGetAttribute<RelationAttribute>(type, out var attr)) attr = new RelationAttribute();
        if (attr.Id == null) {
            attr.Id = (type.FullName + string.Empty).GenerateHashGuid().ToString();
        } else {
            if (!Guid.TryParse(attr.Id, out _)) throw new Exception("Specified guid (" + attr.Id + ") for " + type.FullName + " is not a valid guid. ");
        }
        return attr;
    }
    public static NodeAttribute GetOrCreateNodeAttributeWithId(Type type) {
        if (!tryGetAttribute<NodeAttribute>(type, out var attr)) attr = new NodeAttribute();
        if (attr.Id == null) {
            attr.Id = (type.FullName + string.Empty).GenerateHashGuid().ToString();
        } else {
            if (!Guid.TryParse(attr.Id, out _)) throw new Exception("Specified guid (" + attr.Id + ") for " + type.FullName + " is not a valid guid. ");
        }
        return attr;
    }
    static Type[] knownSupportedValueTypes = [typeof(bool), typeof(byte), typeof(int), typeof(long), typeof(double), typeof(float), typeof(decimal),
        typeof(DateTime), typeof(DateTimeOffset), typeof(Guid), typeof(TimeSpan), typeof(GeoCoordinate)];
    static void firstTestForIllegalTypes(Type valueType, MemberInfo member) {
        if (valueType.IsEnum) return;
        if (valueType.IsValueType) {
            if (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(Nullable<>)) {
                throw new Exception("The member \"" + member.DeclaringType!.Name + "." + member.Name + "\" is of the nullable type "
                    + valueType.GetCSharpName() + ", which is not supported. "
                    + "Use the non-nullable type instead, or mark the member with [Exclude] if it should not be stored. ");
            }
            if (!knownSupportedValueTypes.Contains(valueType)) {
                throw new Exception("The type \"" + valueType.GetCSharpName() + "\" of member \""
                    + member.DeclaringType!.Name + "." + member.Name + "\" is not supported. "
                    + "Supported value types are: " + string.Join(", ", knownSupportedValueTypes.Select(t => t.Name)) + " and enums. "
                    + "Mark the member with [Exclude] if it should not be stored. ");
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
        return (type.FullName + string.Empty).GenerateHashGuid();
    }
    public static Guid GetOrCreateRelationId(Type type) {
        if (tryGetAttribute<RelationAttribute>(type, out var attr) && attr.Id != null) {
            if (!Guid.TryParse(attr.Id, out var guid))
                throw new Exception("Specified guid (" + attr.Id + ") for " + type.FullName + " is not a valid guid. ");
            return guid;
        }
        return (type.FullName + string.Empty).GenerateHashGuid();
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
        if (interfacesWithMember.Count > 1) throw new Exception("The property \"" + member.Name + "\" is declared by more than one interface: "
            + string.Join(", ", interfacesWithMember.Select(c => c.FullName + "." + member.Name)) + ". "
            + "A property can only be declared once in a type hierarchy. Declare it on a single shared interface, or rename one of them. ");
        if (interfacesWithMember.Count == 1) return interfacesWithMember.First();
        if (classesOrRecsWithMember.Count > 1) throw new Exception("The property \"" + member.Name + "\" is declared by more than one class: "
            + string.Join(", ", classesOrRecsWithMember.Select(c => c.FullName + "." + member.Name)) + ". "
            + "Overriding or hiding a stored property is not supported. Declare it only on the base class, or rename one of them. ");
        if (classesOrRecsWithMember.Count == 1) return classesOrRecsWithMember.First();
        throw new Exception("Unable to locate the declaring type of member \"" + member.Name + "\" on " + member.DeclaringType.FullName + ". ");
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
                if (hasAttr<PublicIdPropertyAttribute>(pInfo)) throw new Exception(
                    "The member \"" + pInfo.DeclaringType?.Name + "." + pInfo.Name + "\" is marked with [PublicIdProperty] but is of type "
                    + valueType.GetCSharpName() + ". A public id property must be of type Guid or string. ");
            }
        }
        if (pInfo.Name == internalIdName) {
            if (valueType == typeof(int)) {
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
                if (hasAttr<InternalIdPropertyAttribute>(pInfo)) throw new Exception(
                    "The member \"" + pInfo.DeclaringType?.Name + "." + pInfo.Name + "\" is marked with [InternalIdProperty] but is of type "
                    + valueType.GetCSharpName() + ". An internal id property must be of type int, long or string. ");
            }
        }
        return false;
    }
    static bool isSystemPropertyThenAssignIt(NodeTypeModel c, MemberInfo pInfo, Type valueType) {

        if (valueType == typeof(NodeMeta)) {
            c.NameOfMetaProperty = pInfo.Name;
            return true;
        }

        static Exception wrongType(MemberInfo pInfo, string attributeName, Type valueType, string expected) => new(
            "The member \"" + pInfo.DeclaringType?.Name + "." + pInfo.Name + "\" is marked with [" + attributeName + "] but is of type "
            + valueType.GetCSharpName() + ". It must be of type " + expected + ". ");
        if (hasAttr<ChangedUtcPropertyAttribute>(pInfo)) {
            if (valueType == typeof(DateTime)) {
                c.NameOfChangedUtcProperty = pInfo.Name;
                return true;
            }
            throw wrongType(pInfo, "ChangedUtcProperty", valueType, "DateTime");
        }
        if (hasAttr<CreatedUtcPropertyAttribute>(pInfo)) {
            if (valueType == typeof(DateTime)) {
                c.NameOfCreatedUtcProperty = pInfo.Name;
                return true;
            }
            throw wrongType(pInfo, "CreatedUtcProperty", valueType, "DateTime");
        }
        if (hasAttr<DisplayNamePropertyAttribute>(pInfo)) {
            if (valueType == typeof(string)) {
                c.NameOfDisplayNameProperty = pInfo.Name;
                return true;
            }
            throw wrongType(pInfo, "DisplayNameProperty", valueType, "string");
        }
        if (hasAttr<AddressPropertyAttribute>(pInfo)) {
            if (valueType == typeof(string)) {
                c.NameOfAddressProperty = pInfo.Name;
                return true;
            }
            throw wrongType(pInfo, "AddressProperty", valueType, "string");
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
            throw new Exception("The type " + type.FullName + " cannot be used as a node type. "
                + "A node type must be a class, interface, record or struct. "
                + "If the type ended up in the datamodel by accident, mark it with [Exclude] or narrow the namespace filter of the datamodel source. ");
        }
    }
    static bool isTypeRelevant(Type type) {
        if (type == typeof(object)) return false;
        if (type.IsGenericType) return false; // filter out "IEquatable<T>" , probably a better way of doing it...
        return true;
    }
}
