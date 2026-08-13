using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;

using System.Collections;
using System.Globalization;
using System.Reflection;
using Relatude.DB.Nodes;
using System.Diagnostics.CodeAnalysis;
using Relatude.DB.Query;

namespace Relatude.DB.Datamodels;
// Extensions neede for building model from types and compiling model classes
internal static class BuildUtilsProperties {
    public static PropertyModel CreatePropertyFromMember(MemberInfo m, Type valueType, bool autoDeduceRelations = false) {
        var a = getOrCreatePropertyAttributeWithId(m, valueType, autoDeduceRelations);
        PropertyModel p;
        if (valueType == typeof(string)) {
            p = getStringPropertyModel(cast<StringPropertyAttribute>(a, m));
        } else if (valueType == typeof(bool)) {
            p = getBooleanPropertyModel(cast<BooleanPropertyAttribute>(a, m));
        } else if (valueType == typeof(int)) {
            p = getIntegerPropertyModel(cast<IntegerPropertyAttribute>(a, m));
        } else if (valueType.IsEnum) {
            p = getIntegerPropertyModel(cast<IntegerPropertyAttribute>(a, m));
        } else if (valueType == typeof(long)) {
            p = getLongPropertyModel(cast<LongPropertyAttribute>(a, m));
        } else if (valueType == typeof(decimal)) {
            p = getDecimalPropertyModel(cast<DecimalPropertyAttribute>(a, m));
        } else if (valueType == typeof(DateTime)) {
            p = getDateTimePropertyModel(cast<DateTimePropertyAttribute>(a, m));
        } else if (valueType == typeof(DateTimeOffset)) {
            p = getDateTimeOffsetPropertyModel(cast<DateTimeOffsetPropertyAttribute>(a, m));
        } else if (valueType == typeof(TimeSpan)) {
            p = getTimeSpanPropertyModel(cast<TimeSpanPropertyAttribute>(a, m));
        } else if (valueType == typeof(GeoCoordinate)) {
            p = getGeoCoordinatePropertyModel(cast<GeoCoordinatePropertyAttribute>(a, m));
        } else if (valueType == typeof(Guid)) {
            p = getGuidPropertyModel(cast<GuidPropertyAttribute>(a, m));
        } else if (valueType == typeof(byte[])) {
            p = getByteArrayPropertyModel(cast<ByteArrayPropertyAttribute>(a, m));
        } else if (valueType == typeof(double)) {
            p = getDoublePropertyModel(cast<DoublePropertyAttribute>(a, m));
        } else if (valueType == typeof(float)) {
            p = getFloatPropertyModel(cast<FloatPropertyAttribute>(a, m));
        } else if (valueType == typeof(float[])) {
            p = getFloatArrayPropertyModel(cast<FloatArrayPropertyAttribute>(a, m));
        } else if (valueType == typeof(string[])) {
            p = getStringArrayPropertyModel(cast<StringArrayPropertyAttribute>(a, m));
        } else if (valueType == typeof(Guid[])) {
            p = getGuidArrayPropertyModel(cast<GuidArrayPropertyAttribute>(a, m));
        } else if (isEnumArray(valueType)) {
            p = getEnumArrayPropertyModel(cast<EnumArrayPropertyAttribute>(a, m));
        } else if (valueType == typeof(FileValue)) {
            p = getFilePropertyModel(cast<FilePropertyAttribute>(a, m));
        } else if (valueType.InheritsFromOrImplements<IEmbedded>()) {
            p = getEmbeddedPropertyModel(m, cast<EmbeddedPropertyAttribute>(a, m), valueType);
        } else if (valueType.InheritsFromOrImplements<IEmbeddedMap>()) {
            p = getEmbeddedMapPropertyModel(cast<EmbeddedMapPropertyAttribute>(a, m), valueType);
        } else if (valueType.InheritsFromOrImplements<IReference>()) {
            p = getReferencePropertyModel(cast<ReferencePropertyAttribute>(a, m), m, valueType);
        } else if (valueType.InheritsFromOrImplements<IReferences>()) {
            p = getReferencesPropertyModel(cast<ReferencesPropertyAttribute>(a, m), m, valueType);
        } else if (a is ReferencePropertyAttribute && valueType.IsSubclassOf(typeof(object))) {
            // plain node-typed member modeled as a reference (single guid value)
            p = getReferencePropertyModel(cast<ReferencePropertyAttribute>(a, m), m, valueType);
        } else if (a is ReferencesPropertyAttribute && valueType.IsSubclassOf(typeof(object))) {
            // plain collection of node-typed members modeled as references (guid array value)
            p = getReferencesPropertyModel(cast<ReferencesPropertyAttribute>(a, m), m, valueType);
        } else if (valueType.IsSubclassOf(typeof(object))) {
            // if not primitive, then it is assumed to be a relation
            p = getRelationPropertyModel(cast<RelationPropertyAttribute>(a, m), m, valueType);
        } else {
            throw new NotSupportedException();
        }
        p.Id = string.IsNullOrEmpty(a.Id) ? Guid.Empty : Guid.Parse(a.Id);
        p.CodeName = m.Name;
        p.ReadAccess = string.IsNullOrEmpty(a.ReadAccess) ? Guid.Empty : Guid.Parse(a.ReadAccess);
        p.WriteAccess = string.IsNullOrEmpty(a.WriteAccess) ? Guid.Empty : Guid.Parse(a.WriteAccess);
        p.ExcludeFromTextIndex = a.ExcludeFromTextIndex;
        p.IndexBoost = a.TextIndexBoost;
        p.DisplayName = a.DisplayName;
        if (a is IAttrScalarProperty asc) {
            if (p is IScalarProperty psc) {
                psc.FacetRangePowerBase = asc.FacetRangePowerBase;
                psc.FacetRangeCount = asc.FacetRangeCount;
            }
        }
        if (a is IAttrWithNotFacet anf) p.NotFacet = anf.NotFacet;
        if (a is IAttrWithUniqueContraints au) {
            if (p is IPropertyModelUniqueContraints pu) {
                pu.UniqueValues = au.UniqueValues;
            } else {
                throw new Exception("Attribute " + a.GetType().FullName + " does not match value type for " + m.DeclaringType?.FullName + "." + m.Name);
            }
        } else {
            if (p is IPropertyModelUniqueContraints) {
                throw new Exception("Attribute " + a.GetType().FullName + " does not match value type for " + m.DeclaringType?.FullName + "." + m.Name);
            }
        }
        return p;
    }
    static T cast<T>(PropertyAttribute a, MemberInfo m) where T : PropertyAttribute {
        if (a is T aT) return aT;
        throw new Exception("Attribute " + a.GetType().FullName + " does not match value type for " + m.DeclaringType?.FullName + "." + m.Name);
    }
    static PropertyAttribute getOrCreatePropertyAttributeWithId(MemberInfo member, Type valueType, bool autoDeduceRelations) {
        if (!BuildUtils.tryGetAttribute<PropertyAttribute>(member, out var attr)) {
            if (valueType == typeof(string)) attr = new StringPropertyAttribute();
            else if (valueType == typeof(bool)) attr = new BooleanPropertyAttribute();
            else if (valueType == typeof(int)) attr = new IntegerPropertyAttribute();
            else if (valueType.IsEnum) attr = new IntegerPropertyAttribute() { IsEnum = true };
            else if (valueType == typeof(double)) attr = new DoublePropertyAttribute();
            else if (valueType == typeof(float)) attr = new FloatPropertyAttribute();
            else if (valueType == typeof(string[])) attr = new StringArrayPropertyAttribute();
            else if (valueType == typeof(Guid[])) attr = new GuidArrayPropertyAttribute();
            else if (isEnumArray(valueType)) attr = new EnumArrayPropertyAttribute();
            else if (valueType == typeof(long)) attr = new LongPropertyAttribute();
            else if (valueType == typeof(decimal)) attr = new DecimalPropertyAttribute();
            else if (valueType == typeof(DateTime)) attr = new DateTimePropertyAttribute();
            else if (valueType == typeof(DateTimeOffset)) attr = new DateTimeOffsetPropertyAttribute();
            else if (valueType == typeof(TimeSpan)) attr = new TimeSpanPropertyAttribute();
            else if (valueType == typeof(GeoCoordinate)) attr = new GeoCoordinatePropertyAttribute();
            else if (valueType == typeof(Guid)) attr = new GuidPropertyAttribute();
            else if (valueType == typeof(byte[])) attr = new ByteArrayPropertyAttribute();
            else if (valueType == typeof(FileValue)) attr = new FilePropertyAttribute();
            else if (valueType.InheritsFromOrImplements<IReference>()) {
                attr = new ReferencePropertyAttribute();
            } else if (valueType.InheritsFromOrImplements<IReferences>()) {
                attr = new ReferencesPropertyAttribute();
            } else if (valueType.InheritsFromOrImplements<IEmbedded>()) {
                attr = new EmbeddedPropertyAttribute();
            } else if (valueType.InheritsFromOrImplements<IEmbeddedMap>()) {
                var a = new EmbeddedMapPropertyAttribute();
                // since no attribute was defined, key property is id as Guid or Id
                var typeKey = valueType.GetGenericArguments()[0];
                if (typeKey == typeof(int)) a.KeyType = KeyPropertyType.NodeIntegerId;
                else if (typeKey == typeof(Guid)) a.KeyType = KeyPropertyType.NodeGuidId;
                else throw new Exception("The key type " + typeKey.FullName + " of property '" + "" + member.DeclaringType?.FullName + "." + member.Name + "' is not supported for EmbeddedMapProperty. ");
                attr = a;
            } else if (valueType.IsSubclassOf(typeof(object))) {
                if (autoDeduceRelations || valueType.InheritsFromOrImplements<IRelationProperty>()) {
                    // native relation properties (nested relation classes) are always relations;
                    // other node-typed members become relations only when auto-deduction is on
                    attr = new RelationPropertyAttribute();
                } else if (valueType.InheritsFromOrImplements<IEnumerable>()) {
                    attr = new ReferencesPropertyAttribute();
                } else {
                    attr = new ReferencePropertyAttribute();
                }
            } else throw new NotSupportedException(member.DeclaringType?.FullName + "." + member.Name + " - The value type " + valueType.FullName + " is not supported as a member type. ");
        } else {
            if (attr is StringPropertyAttribute && valueType != typeof(string)
            || attr is BooleanPropertyAttribute && valueType != typeof(bool)
            || attr is IntegerPropertyAttribute && (valueType != typeof(int) && !valueType.IsEnum)
            || attr is DoublePropertyAttribute && valueType != typeof(double)
            || attr is StringArrayPropertyAttribute && valueType != typeof(string[])
            || attr is GuidArrayPropertyAttribute && valueType != typeof(Guid[])
            || attr is EnumArrayPropertyAttribute && !isEnumArray(valueType)
            || attr is LongPropertyAttribute && valueType != typeof(long)
            || attr is DecimalPropertyAttribute && valueType != typeof(decimal)
            || attr is DateTimePropertyAttribute && valueType != typeof(DateTime)
            || attr is DateTimeOffsetPropertyAttribute && valueType != typeof(DateTimeOffset)
            || attr is TimeSpanPropertyAttribute && valueType != typeof(TimeSpan)
            || attr is GeoCoordinatePropertyAttribute && valueType != typeof(GeoCoordinate)
            || attr is GuidPropertyAttribute && valueType != typeof(Guid)
            || attr is ByteArrayPropertyAttribute && valueType != typeof(byte[])
            || attr is FilePropertyAttribute && valueType != typeof(FileValue)
            || attr is EmbeddedPropertyAttribute && !(valueType.InheritsFromOrImplements<IEmbedded>() || valueType.InheritsFromOrImplements<IEmbeddedMap>())
            || attr is ReferencePropertyAttribute && !(valueType.InheritsFromOrImplements<IReference>() || isPlainReferenceType(valueType))
            || attr is ReferencesPropertyAttribute && !(valueType.InheritsFromOrImplements<IReferences>() || isPlainReferencesType(valueType))
            || attr is EmbeddedMapPropertyAttribute && !valueType.InheritsFromOrImplements<IEmbeddedMap>()
            || attr is RelationPropertyAttribute && !valueType.IsSubclassOf(typeof(object))
            ) {
                throw new Exception("The type " + valueType.Name + " of property '" + "" + member.DeclaringType?.FullName + "." + member.Name + "' is not compatible with attribute " + attr.GetType().Name + ". ");
            }
        }
        if (string.IsNullOrEmpty(attr.Id)) {
            var rootType = BuildUtils.GetBaseDeclaringType(member);
            var nodeTypeAttr = BuildUtils.GetOrCreateNodeAttributeWithId(rootType);
            attr.Id = (nodeTypeAttr.Id + "." + member.Name).GenerateHashGuid().ToString();
        }
        if (valueType.IsEnum && attr is IntegerPropertyAttribute ipa) {
            ipa.IsEnum = true;
            ipa.LegalValues = valueType.GetEnumValues().Cast<int>().ToArray();
            ipa.LegalValueNames = valueType.GetEnumNames(); // same order as GetEnumValues (sorted by value)
            ipa.FullEnumTypeName = valueType.FullName;
        }
        if (isEnumArray(valueType) && attr is EnumArrayPropertyAttribute eaa) {
            var elementType = valueType.GetElementType()!;
            eaa.FullEnumTypeName = elementType.FullName;
            eaa.LegalValues = elementType.GetEnumValues().Cast<int>().ToArray(); // int-backed enums only, like the scalar path
            eaa.LegalValueNames = elementType.GetEnumNames();
        }
        return attr;
    }
    static bool isEnumArray(Type valueType) => valueType.IsArray && valueType.GetElementType()!.IsEnum;
    // the generic collection interfaces do not implement their non-generic counterparts
    // (only IEnumerable<T> : IEnumerable), so members declared as ICollection<T>/IList<T> etc.
    // must be matched on the generic type definition:
    static bool isAnyGenericInterface(Type t, params Type[] genericDefinitions) =>
        t.IsInterface && t.IsGenericType && genericDefinitions.Contains(t.GetGenericTypeDefinition());
    // plain node-typed member usable as a single reference (guid value)
    static bool isPlainReferenceType(Type t) =>
        t.IsSubclassOf(typeof(object)) && !t.IsValueType && !t.IsEnum
        && !t.InheritsFromOrImplements<IEnumerable>() && !t.InheritsFromOrImplements<IRelationProperty>();
    // plain collection of node-typed members usable as references (guid array value)
    static bool isPlainReferencesType(Type t) =>
        t.IsSubclassOf(typeof(object)) && t.InheritsFromOrImplements<IEnumerable>()
        && !t.InheritsFromOrImplements<IRelationProperty>();
    static StringPropertyModel getStringPropertyModel(StringPropertyAttribute a) {
        var p = new StringPropertyModel();
        p.DefaultValue = a.DefaultValue;
        p.Indexed = a.Indexed;
        p.IndexedByWords = a.IndexedByWords;
        p.IndexedBySemantic = a.IndexedBySemantic;
        p.InfixSearch = a.InfixSearch;
        p.MaxLength = a.MaxLength;
        p.MaxWordLength = a.MaxWordLength;
        p.IgnoreDuplicateEmptyValues = a.IgnoreDuplicateEmptyValues;
        p.DisplayName = a.DisplayName;
        p.MinLength = a.MinLength;
        p.MinWordLength = a.MinWordLength;
        p.PrefixSearch = a.PrefixSearch;
        return p;
    }
    static IntegerPropertyModel getIntegerPropertyModel(IntegerPropertyAttribute a) {
        var p = new IntegerPropertyModel();
        p.DefaultValue = a.DefaultValue;
        p.Indexed = a.Indexed;
        p.MaxValue = a.MaxValue;
        p.MinValue = a.MinValue;
        p.IsEnum = a.IsEnum;
        p.LegalValues = a.LegalValues;
        p.LegalValueNames = a.LegalValueNames;
        p.FullEnumTypeName = a.FullEnumTypeName;
        return p;
    }
    static LongPropertyModel getLongPropertyModel(LongPropertyAttribute a) {
        var p = new LongPropertyModel();
        p.DefaultValue = a.DefaultValue;
        p.Indexed = a.Indexed;
        p.MaxValue = a.MaxValue;
        p.MinValue = a.MinValue;
        return p;
    }
    static DecimalPropertyModel getDecimalPropertyModel(DecimalPropertyAttribute a) {
        var p = new DecimalPropertyModel();
        if (!string.IsNullOrEmpty(a.DefaultValue)) p.DefaultValue = decimal.Parse(a.DefaultValue, CultureInfo.InvariantCulture);
        p.Indexed = a.Indexed;
        p.MaxValue = string.IsNullOrEmpty(a.MaxValue) ? decimal.MaxValue : decimal.Parse(a.MaxValue, CultureInfo.InvariantCulture);
        p.MinValue = string.IsNullOrEmpty(a.MinValue) ? decimal.MinValue : decimal.Parse(a.MinValue, CultureInfo.InvariantCulture);
        return p;
    }
    static DateTimePropertyModel getDateTimePropertyModel(DateTimePropertyAttribute a) {
        var p = new DateTimePropertyModel();
        if (!string.IsNullOrEmpty(a.DefaultValue)) p.DefaultValue = DateTime.Parse(a.DefaultValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        p.Indexed = a.Indexed;
        p.MaxValue = string.IsNullOrEmpty(a.MaxValue) ? DateTime.MaxValue : DateTime.Parse(a.MaxValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        p.MinValue = string.IsNullOrEmpty(a.MinValue) ? DateTime.MinValue : DateTime.Parse(a.MinValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return p;
    }
    static DateTimeOffsetPropertyModel getDateTimeOffsetPropertyModel(DateTimeOffsetPropertyAttribute a) {
        var p = new DateTimeOffsetPropertyModel();
        if (!string.IsNullOrEmpty(a.DefaultValue)) p.DefaultValue = DateTimeOffset.Parse(a.DefaultValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        p.Indexed = a.Indexed;
        p.MaxValue = string.IsNullOrEmpty(a.MaxValue) ? DateTimeOffset.MaxValue : DateTimeOffset.Parse(a.MaxValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        p.MinValue = string.IsNullOrEmpty(a.MinValue) ? DateTimeOffset.MinValue : DateTimeOffset.Parse(a.MinValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return p;
    }
    static GeoCoordinatePropertyModel getGeoCoordinatePropertyModel(GeoCoordinatePropertyAttribute a) {
        var p = new GeoCoordinatePropertyModel();
        p.Indexed = a.Indexed;
        return p;
    }
    static TimeSpanPropertyModel getTimeSpanPropertyModel(TimeSpanPropertyAttribute a) {
        var p = new TimeSpanPropertyModel();
        if (!string.IsNullOrEmpty(a.DefaultValue)) p.DefaultValue = TimeSpan.Parse(a.DefaultValue, CultureInfo.InvariantCulture);
        p.Indexed = a.Indexed;
        p.MaxValue = string.IsNullOrEmpty(a.MaxValue) ? TimeSpan.MaxValue : TimeSpan.Parse(a.MaxValue, CultureInfo.InvariantCulture);
        p.MinValue = string.IsNullOrEmpty(a.MinValue) ? TimeSpan.MinValue : TimeSpan.Parse(a.MinValue, CultureInfo.InvariantCulture);
        return p;
    }
    static GuidPropertyModel getGuidPropertyModel(GuidPropertyAttribute a) {
        var p = new GuidPropertyModel();
        if (!string.IsNullOrEmpty(a.DefaultValue)) p.DefaultValue = Guid.Parse(a.DefaultValue);
        p.Indexed = a.Indexed;
        return p;
    }
    static ByteArrayPropertyModel getByteArrayPropertyModel(ByteArrayPropertyAttribute a) {
        var p = new ByteArrayPropertyModel();
        return p;
    }
    static FloatArrayPropertyModel getFloatArrayPropertyModel(FloatArrayPropertyAttribute a) {
        var p = new FloatArrayPropertyModel();
        return p;
    }
    static DoublePropertyModel getDoublePropertyModel(DoublePropertyAttribute a) {
        var p = new DoublePropertyModel();
        p.DefaultValue = a.DefaultValue;
        p.Indexed = a.Indexed;
        p.MaxValue = a.MaxValue;
        p.MinValue = a.MinValue;
        return p;
    }
    static FloatPropertyModel getFloatPropertyModel(FloatPropertyAttribute a) {
        var p = new FloatPropertyModel();
        p.DefaultValue = a.DefaultValue;
        p.Indexed = a.Indexed;
        p.MaxValue = a.MaxValue;
        p.MinValue = a.MinValue;
        return p;
    }
    static FilePropertyModel getFilePropertyModel(FilePropertyAttribute a) {
        var p = new FilePropertyModel();
        if (!string.IsNullOrEmpty(a.FileStorageProviderId)) p.FileStorageProviderId = Guid.Parse(a.FileStorageProviderId);
        return p;
    }
    static void addCommonEmbeddedProperties(EmbeddedPropertyAttribute a, EmbeddedPropertyModel p, Type valueType, bool isMap) {
        if (a.InnerTypeIds != null) {
            var ids = new List<Guid>();
            var names = new List<string>();
            foreach (var id in a.InnerTypeIds) {
                if (Guid.TryParse(id, out var innerTypeGuid)) {
                    ids.Add(innerTypeGuid);
                } else if (!string.IsNullOrEmpty(id)) {
                    names.Add(id);
                }
            }
            if (ids.Count > 0) p.InnerNodeTypes = ids;
            if (names.Count > 0) p.InnerNodeTypesNames = names;
        }
        if (a.InnerTypeIds == null) {
            var nodeType = valueType.GetGenericArguments()[isMap ? 1 : 0];
            p.InnerNodeTypesNames = [nodeType.FullName!];
        }
        p.IncludeTypes = a.IncludeTypes;
    }
    static EmbeddedPropertyModel getEmbeddedPropertyModel(MemberInfo m, EmbeddedPropertyAttribute a, Type valueType) {
        var p = new EmbeddedPropertyModel();
        // validating m is a property and does not have a set property:
        if (m is not PropertyInfo pi) throw new Exception("Member " + m.DeclaringType?.FullName + "." + m.Name + " is not a property, only members of type PropertyInfo are supported for EmbeddedProperties.");
        if (pi.SetMethod != null) throw new Exception("Property " + m.DeclaringType?.FullName + "." + m.Name + " has a set method that cannot be implemented by the mapper. Set methods are not supported for EmbeddedProperties.");
        addCommonEmbeddedProperties(a, p, valueType, false);
        p.EmbeddedValueType = EmbeddedValueType.InnerNodeList;
        return p;
    }
    static EmbeddedPropertyModel getEmbeddedMapPropertyModel(EmbeddedMapPropertyAttribute a, Type valueType) {
        var p = new EmbeddedPropertyModel();
        p.EmbeddedValueType = EmbeddedValueType.InnerNodeMap;
        addCommonEmbeddedProperties(a, p, valueType, true);
        if (a.KeyType == KeyPropertyType.NodeGuidId) {
            p.KeyProperty = InnerNodeDataMap<object>.PropertyIdNodeGuidId;
        } else if (a.KeyType == KeyPropertyType.NodeIntegerId) {
            p.KeyProperty = InnerNodeDataMap<object>.PropertyIdNodeIntId;
        }
        if (Guid.TryParse(a.KeyProperty, out var keyPropGuid)) {
            p.KeyProperty = keyPropGuid;
        } else {
            p.KeyPropertyName = a.KeyProperty;
        }
        p._keyTypeInCodeModelForLaterChecks = valueType;
        return p;
    }
    static ReferencePropertyModel getReferencePropertyModel(ReferencePropertyAttribute a, MemberInfo m, Type valueType) {
        var p = new ReferencePropertyModel();

        Type nodeType;
        if (valueType.InheritsFromOrImplements<IReference>()) {
            p.ReferenceValueType = ReferenceValueType.Wrapper;
            nodeType = valueType.GetGenericArguments()[0];
        } else { // plain node-typed member
            p.ReferenceValueType = ReferenceValueType.Object;
            nodeType = valueType;
        }
        if (a.TypeIds != null) {
            var ids = new List<Guid>();
            var names = new List<string>();
            foreach (var id in a.TypeIds) {
                if (Guid.TryParse(id, out var innerTypeGuid)) {
                    ids.Add(innerTypeGuid);
                } else if (!string.IsNullOrEmpty(id)) {
                    names.Add(id);
                }
            }
            if (ids.Count > 0) p.NodeTypes = ids;
            if (names.Count > 0) p.NodeTypesNames = names;
        }
        if (a.TypeIds == null) {
            p.NodeTypesNames = [nodeType.FullName!];
        }
        p.IncludeTypes = a.IncludeTypes;
        p.Indexed = a.Indexed;
        return p;
    }
    static ReferencesPropertyModel getReferencesPropertyModel(ReferencesPropertyAttribute a, MemberInfo m, Type valueType) {
        var p = new ReferencesPropertyModel();

        Type? nodeType;
        if (valueType.InheritsFromOrImplements<IReferences>()) {
            p.ReferenceValueType = ReferenceValueType.Wrapper;
            nodeType = valueType.GetGenericArguments()[0];
        } else if (valueType.InheritsFromOrImplements<Array>()) {
            p.ReferenceValueType = ReferenceValueType.Array;
            nodeType = valueType.GetElementType();
        } else { // same collection shape detection as relation properties
            var genericTypes = valueType.GetGenericArguments();
            nodeType = genericTypes.Length > 0 ? genericTypes[0] : null;
            if (valueType.InheritsFromOrImplements<IList>() || isAnyGenericInterface(valueType, typeof(IList<>))) {
                p.ReferenceValueType = ReferenceValueType.List;
            } else if (valueType.InheritsFromOrImplements<ICollection>()
                || isAnyGenericInterface(valueType, typeof(ICollection<>), typeof(IReadOnlyCollection<>), typeof(IReadOnlyList<>))) {
                p.ReferenceValueType = ReferenceValueType.Collection;
            } else if (valueType.InheritsFromOrImplements<IEnumerable>()) {
                p.ReferenceValueType = ReferenceValueType.Enumerable;
            } else {
                throw new Exception("Could not determine collection type for " + m.DeclaringType?.FullName + "." + m.Name);
            }
        }
        if (a.TypeIds != null) {
            var ids = new List<Guid>();
            var names = new List<string>();
            foreach (var id in a.TypeIds) {
                if (Guid.TryParse(id, out var innerTypeGuid)) {
                    ids.Add(innerTypeGuid);
                } else if (!string.IsNullOrEmpty(id)) {
                    names.Add(id);
                }
            }
            if (ids.Count > 0) p.NodeTypes = ids;
            if (names.Count > 0) p.NodeTypesNames = names;
        }
        if (a.TypeIds == null) {
            if (nodeType == null) throw new Exception("Could not determine node type of references property " + m.DeclaringType?.FullName + "." + m.Name);
            p.NodeTypesNames = [nodeType.FullName!];
        }
        p.IncludeTypes = a.IncludeTypes;
        p.Indexed = a.Indexed;
        return p;
    }
    static BooleanPropertyModel getBooleanPropertyModel(BooleanPropertyAttribute a) {
        var p = new BooleanPropertyModel();
        p.DefaultValue = a.DefaultValue;
        p.Indexed = a.Indexed;
        return p;
    }
    static StringArrayPropertyModel getStringArrayPropertyModel(StringArrayPropertyAttribute a) {
        var p = new StringArrayPropertyModel();
        p.Indexed = a.Indexed;
        return p;
    }
    static GuidArrayPropertyModel getGuidArrayPropertyModel(GuidArrayPropertyAttribute a) {
        var p = new GuidArrayPropertyModel();
        p.Indexed = a.Indexed;
        return p;
    }
    static EnumArrayPropertyModel getEnumArrayPropertyModel(EnumArrayPropertyAttribute a) {
        var p = new EnumArrayPropertyModel();
        p.Indexed = a.Indexed;
        p.FullEnumTypeName = a.FullEnumTypeName;
        p.LegalValues = a.LegalValues;
        p.LegalValueNames = a.LegalValueNames;
        return p;
    }
    static RelationType getRelationClassType(Type relationType) {
        if (relationType.InheritsFromOrImplements<IOneOne>()) return RelationType.OneOne;
        else if (relationType.InheritsFromOrImplements<IOneToOne>()) return RelationType.OneToOne;
        else if (relationType.InheritsFromOrImplements<IOneToMany>()) return RelationType.OneToMany;
        else if (relationType.InheritsFromOrImplements<IManyMany>()) return RelationType.ManyMany;
        else if (relationType.InheritsFromOrImplements<IManyToMany>()) return RelationType.ManyToMany;
        throw new Exception("Could not determine relation type for " + relationType.FullName);
    }
    static bool isRelationPropertyFromTargetToSource(Type relationClassType, Type propertyValueType) {
        propertyValueType = propertyValueType.BaseType!;
        return getRelationClassType(relationClassType) switch {
            RelationType.OneToOne => propertyValueType.Name == nameof(OneToOne<object, object>.OneFrom),
            RelationType.OneToMany => propertyValueType.Name == nameof(OneToMany<object, object>.One),
            RelationType.ManyToMany => propertyValueType.Name == nameof(ManyToMany<object, object>.ManyFrom),
            RelationType.OneOne => false,
            RelationType.ManyMany => false,
            _ => throw new NotSupportedException("Relation type " + relationClassType.FullName + " is not supported."),
        };
    }
    static bool tryFindTypeObjectForRelation(Type propValueType, [MaybeNullWhen(false)] out Type relationType, out string reason) {
        relationType = null;
        reason = string.Empty;
        relationType = propValueType.DeclaringType;
        if (relationType == null) {
            reason = "The declaring property value type " + propValueType.Name + " does not have a declaring type.";
            return false;
        }
        if (!relationType.InheritsFromOrImplements<IRelation>()) {
            reason = "The declaring property value type " + propValueType.Name + " does not implement IRelation or inherit from a type that implements IRelation.";
            return false;
        }
        return true;
    }
    // The nested side classes (One, Many, OneFrom, ManyTo...) are nested inside the generic relation class,
    // so their generic arguments are the relation's ([TFrom, TTo]), not the side's. Only OneProperty<T> /
    // ManyProperty<T> further up the base chain names the type this particular side relates to.
    static Type getRelatedTypeOfRelationProperty(Type propValueType, MemberInfo m) {
        for (var t = propValueType; t != null; t = t.BaseType) {
            if (!t.IsGenericType) continue;
            var def = t.GetGenericTypeDefinition();
            if (def == typeof(OneProperty<>) || def == typeof(ManyProperty<>)) return t.GetGenericArguments()[0];
        }
        throw new Exception("Could not determine type of related for " + m.DeclaringType?.FullName + "." + m.Name
            + " - " + propValueType.FullName + " does not inherit from " + nameof(OneProperty<object>) + " or " + nameof(ManyProperty<object>) + ".");
    }
    static RelationPropertyModel getRelationPropertyModel(RelationPropertyAttribute attr, MemberInfo m, Type valueType) {
        var r = new RelationPropertyModel();
        r.TextIndexRelatedContent = attr.TextIndexRelatedContent;
        r.TextIndexRelatedDisplayName = attr.TextIndexRelatedDisplayName;
        r.TextIndexRecursiveLevelLimit = attr.TextIndexRecursiveLevelLimit;
        r.Facet = attr.Facet;
        Type? typeOfRelated = null;
        Type? relationType = null;
        if (m is PropertyInfo pi && pi.PropertyType.InheritsFromOrImplements<IRelationProperty>()) {
            r.RelationValueType = RelationValueType.Native;
            if (!tryFindTypeObjectForRelation(pi.PropertyType, out relationType, out var reason)) {
                throw new Exception("Could not resolve the relation for property \"" + pi.DeclaringType!.Name + "." + pi.Name + "\". " + reason);
            }
            var propValueType = pi.PropertyType; // FromToNodes: inherits from OneProperty / ManyProperty
            r.FromTargetToSource = isRelationPropertyFromTargetToSource(relationType, propValueType);
            typeOfRelated = getRelatedTypeOfRelationProperty(propValueType, m);
            if (valueType.InheritsFromOrImplements<IManyProperty>()) {
                r.IsMany = true;
            } else if (valueType.InheritsFromOrImplements<IOneProperty>()) {
                r.IsMany = false;
            } else {
                throw new Exception("Could not determine relation type for " + m.DeclaringType?.FullName + "." + m.Name);
            }
            //r.RelationId = BuildUtils.GetOrCreateRelationId(relationClass);
        } else {
            var relationGenerics = attr.GetType().GetGenericArguments();
            if (relationGenerics.Length > 0) relationType = relationGenerics[0];
            r.FromTargetToSource = attr.RightToLeft;
            r.IsMany = valueType.InheritsFromOrImplements<IEnumerable>();
            if (r.IsMany) {
                if (valueType.InheritsFromOrImplements<Array>()) {
                    typeOfRelated = valueType.GetElementType();
                } else {
                    var genericTypes = valueType.GetGenericArguments();
                    if (genericTypes.Length > 0) typeOfRelated = valueType.GetGenericArguments()[0];
                }
                if (valueType.InheritsFromOrImplements<Array>()) {
                    r.RelationValueType = RelationValueType.Array;
                } else if (valueType.InheritsFromOrImplements<IList>() || isAnyGenericInterface(valueType, typeof(IList<>))) {
                    r.RelationValueType = RelationValueType.List;
                } else if (valueType.InheritsFromOrImplements<ICollection>()
                    || isAnyGenericInterface(valueType, typeof(ICollection<>), typeof(IReadOnlyCollection<>), typeof(IReadOnlyList<>))) {
                    r.RelationValueType = RelationValueType.Collection;
                } else if (valueType.InheritsFromOrImplements<IEnumerable>()) {
                    r.RelationValueType = RelationValueType.Enumerable;
                } else {
                    throw new Exception("Could not determine collection type for " + m.DeclaringType?.FullName + "." + m.Name);
                }
            } else {
                typeOfRelated = valueType;
            }
        }
        if (relationType is not null) {
            r.RelationId = BuildUtils.GetOrCreateRelationId(relationType);
        }
        if (typeOfRelated == null) throw new Exception("Could not determine type of related for " + m.DeclaringType?.FullName + "." + m.Name);
        r.NodeTypeOfRelated = BuildUtils.GetOrCreateNodeTypeId(typeOfRelated);
        return r;
    }
}
