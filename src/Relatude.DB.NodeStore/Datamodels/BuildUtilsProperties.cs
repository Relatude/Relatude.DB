using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Query.ExpressionToString.ZSpitz.Extensions;
using System.Collections;
using System.Reflection;
using Relatude.DB.Nodes;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.Datamodels;
// Extensions neede for building model from types and compiling model classes
internal static class BuildUtilsProperties {
    public static PropertyModel CreatePropertyFromMember(MemberInfo m, Type valueType) {
        var a = getOrCreatePropertyAttributeWithId(m, valueType);
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
        } else if (valueType == typeof(FileValue)) {
            p = getFilePropertyModel(cast<FilePropertyAttribute>(a, m));
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
        if (a.ExcludeFromTextIndex != BoolValue.Default) p.ExcludeFromTextIndex = a.ExcludeFromTextIndex == BoolValue.True;
        p.IndexBoost = a.TextIndexBoost;
        p.DisplayName = a.DisplayName;
        if (a is IAttrScalarProperty asc) {
            if (p is IScalarProperty psc) {
                psc.FacetRangePowerBase = asc.FacetRangePowerBase;
                psc.FacetRangeCount = asc.FacetRangeCount;
            }
        }
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
    static PropertyAttribute getOrCreatePropertyAttributeWithId(MemberInfo member, Type valueType) {
        if (!BuildUtils.tryGetAttribute<PropertyAttribute>(member, out var attr)) {
            if (valueType == typeof(string)) attr = new StringPropertyAttribute();
            else if (valueType == typeof(bool)) attr = new BooleanPropertyAttribute();
            else if (valueType == typeof(int)) attr = new IntegerPropertyAttribute();
            else if (valueType.IsEnum) attr = new IntegerPropertyAttribute() { IsEnum = true };
            else if (valueType == typeof(double)) attr = new DoublePropertyAttribute();
            else if (valueType == typeof(float)) attr = new FloatPropertyAttribute();
            else if (valueType == typeof(string[])) attr = new StringArrayPropertyAttribute();
            else if (valueType == typeof(long)) attr = new LongPropertyAttribute();
            else if (valueType == typeof(decimal)) attr = new DecimalPropertyAttribute();
            else if (valueType == typeof(DateTime)) attr = new DateTimePropertyAttribute();
            else if (valueType == typeof(DateTimeOffset)) attr = new DateTimeOffsetPropertyAttribute();
            else if (valueType == typeof(TimeSpan)) attr = new TimeSpanPropertyAttribute();
            else if (valueType == typeof(Guid)) attr = new GuidPropertyAttribute();
            else if (valueType == typeof(byte[])) attr = new ByteArrayPropertyAttribute();
            else if (valueType == typeof(FileValue)) attr = new FilePropertyAttribute();
            else if (valueType.IsSubclassOf(typeof(object))) attr = new RelationPropertyAttribute();
            else throw new NotSupportedException(member.DeclaringType?.FullName + "." + member.Name + " - The value type " + valueType.FullName + " is not supported as a member type. ");
        } else {
            if (attr is StringPropertyAttribute && valueType != typeof(string)
            || attr is BooleanPropertyAttribute && valueType != typeof(bool)
            || attr is IntegerPropertyAttribute && (valueType != typeof(int) && !valueType.IsEnum)
            || attr is DoublePropertyAttribute && valueType != typeof(double)
            || attr is StringArrayPropertyAttribute && valueType != typeof(string[])
            || attr is LongPropertyAttribute && valueType != typeof(long)
            || attr is DecimalPropertyAttribute && valueType != typeof(decimal)
            || attr is DateTimePropertyAttribute && valueType != typeof(DateTime)
            || attr is DateTimeOffsetPropertyAttribute && valueType != typeof(DateTimeOffset)
            || attr is TimeSpanPropertyAttribute && valueType != typeof(TimeSpan)
            || attr is GuidPropertyAttribute && valueType != typeof(Guid)
            || attr is ByteArrayPropertyAttribute && valueType != typeof(byte[])
            || attr is RelationPropertyAttribute && !valueType.IsSubclassOf(typeof(object))
            ) {
                throw new Exception("The type " + valueType.Name + " of property '" + "" + member.DeclaringType?.FullName + "." + member.Name + "' is not compatible with attribute " + attr.GetType().Name + ". ");
            }
        }
        if (string.IsNullOrEmpty(attr.Id)) {
            var rootType = BuildUtils.GetBaseDeclaringType(member);
            var nodeTypeAttr = BuildUtils.GetOrCreateNodeAttributeWithId(rootType);
            attr.Id = (nodeTypeAttr.Id + "." + member.Name).GenerateGuid().ToString();
        }
        if (valueType.IsEnum && attr is IntegerPropertyAttribute ipa) {
            ipa.IsEnum = true;
            ipa.LegalValues = valueType.GetEnumValues().Cast<int>().ToArray();
            ipa.FullEnumTypeName = valueType.FullName;
        }
        return attr;
    }
    static StringPropertyModel getStringPropertyModel(StringPropertyAttribute a) {
        var p = new StringPropertyModel();
        p.DefaultValue = a.DefaultValue;
        if (a.Indexed != BoolValue.Default) p.Indexed = a.Indexed == BoolValue.True;
        if (a.IndexedByWords != BoolValue.Default) p.IndexedByWords = a.IndexedByWords == BoolValue.True;
        if (a.IndexedBySemantic != BoolValue.Default) p.IndexedBySemantic = a.IndexedBySemantic == BoolValue.True;
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
        if (a.Indexed != BoolValue.Default) p.Indexed = a.Indexed == BoolValue.True;
        p.MaxValue = a.MaxValue;
        p.MinValue = a.MinValue;
        p.IsEnum = a.IsEnum;
        p.LegalValues = a.LegalValues;
        p.FullEnumTypeName = a.FullEnumTypeName;
        return p;
    }
    static LongPropertyModel getLongPropertyModel(LongPropertyAttribute a) {
        var p = new LongPropertyModel();
        p.DefaultValue = a.DefaultValue;
        if (a.Indexed != BoolValue.Default) p.Indexed = a.Indexed == BoolValue.True;
        p.MaxValue = a.MaxValue;
        p.MinValue = a.MinValue;
        return p;
    }
    static DecimalPropertyModel getDecimalPropertyModel(DecimalPropertyAttribute a) {
        var p = new DecimalPropertyModel();
        p.DefaultValue = a.DefaultValue;
        if (a.Indexed != BoolValue.Default) p.Indexed = a.Indexed == BoolValue.True;
        p.MaxValue = a.MaxValue;
        p.MinValue = a.MinValue;
        return p;
    }
    static DateTimePropertyModel getDateTimePropertyModel(DateTimePropertyAttribute a) {
        var p = new DateTimePropertyModel();
        p.DefaultValue = a.DefaultValue;
        if (a.Indexed != BoolValue.Default) p.Indexed = a.Indexed == BoolValue.True;
        p.MaxValue = a.MaxValue;
        p.MinValue = a.MinValue;
        return p;
    }
    static DateTimeOffsetPropertyModel getDateTimeOffsetPropertyModel(DateTimeOffsetPropertyAttribute a) {
        var p = new DateTimeOffsetPropertyModel();
        p.DefaultValue = a.DefaultValue;
        if (a.Indexed != BoolValue.Default) p.Indexed = a.Indexed == BoolValue.True;
        p.MaxValue = a.MaxValue;
        p.MinValue = a.MinValue;
        return p;
    }
    static TimeSpanPropertyModel getTimeSpanPropertyModel(TimeSpanPropertyAttribute a) {
        var p = new TimeSpanPropertyModel();
        p.DefaultValue = a.DefaultValue;
        if (a.Indexed != BoolValue.Default) p.Indexed = a.Indexed == BoolValue.True;
        p.MaxValue = a.MaxValue;
        p.MinValue = a.MinValue;
        return p;
    }
    static GuidPropertyModel getGuidPropertyModel(GuidPropertyAttribute a) {
        var p = new GuidPropertyModel();
        p.DefaultValue = a.DefaultValue;
        if (a.Indexed != BoolValue.Default) p.Indexed = a.Indexed == BoolValue.True;
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
        if (a.Indexed != BoolValue.Default) p.Indexed = a.Indexed == BoolValue.True;
        p.MaxValue = a.MaxValue;
        p.MinValue = a.MinValue;
        return p;
    }
    static FloatPropertyModel getFloatPropertyModel(FloatPropertyAttribute a) {
        var p = new FloatPropertyModel();
        p.DefaultValue = a.DefaultValue;
        if (a.Indexed != BoolValue.Default) p.Indexed = a.Indexed == BoolValue.True;
        p.MaxValue = a.MaxValue;
        p.MinValue = a.MinValue;
        return p;
    }
    static FilePropertyModel getFilePropertyModel(FilePropertyAttribute a) {
        var p = new FilePropertyModel();
        p.FileStorageProviderId = a.FileStorageProviderId;
        return p;
    }

    static BooleanPropertyModel getBooleanPropertyModel(BooleanPropertyAttribute a) {
        var p = new BooleanPropertyModel();
        p.DefaultValue = a.DefaultValue;
        if (a.Indexed != BoolValue.Default) p.Indexed = a.Indexed == BoolValue.True;
        return p;
    }
    static StringArrayPropertyModel getStringArrayPropertyModel(StringArrayPropertyAttribute a) {
        var p = new StringArrayPropertyModel();
        if (a.Indexed != BoolValue.Default) p.Indexed = a.Indexed == BoolValue.True;
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
        return getRelationClassType(relationClassType) switch {
            RelationType.OneToOne => propertyValueType.Name == nameof(OneToOne<object, object>.FromNode),
            RelationType.OneToMany => propertyValueType.Name == nameof(OneToMany<object, object>.FromNode),
            RelationType.ManyToMany => propertyValueType.Name == nameof(ManyToMany<object, object>.FromNodes),
            RelationType.OneOne => false,
            RelationType.ManyMany => false,
            _ => throw new NotSupportedException("Relation type " + relationClassType.FullName + " is not supported."),
        };
    }
    static bool tryFindTypeObjectForRelation(Type propValueType, [MaybeNullWhen(false)] out Type relationType, out string reason) {
        relationType = null;
        reason = string.Empty;
        var genericArguments = propValueType.GetGenericArguments();
        var lastArgument = genericArguments.Last();
        var relationHaveTRelationReference = lastArgument.InheritsFromOrImplements<IRelation>();
        if (relationHaveTRelationReference) {
            relationType = lastArgument;
            return true;
        }
        // search the assembly for a relation type that matches the property value type
        var baseType = propValueType.DeclaringType;
        if (baseType == null) {
            reason = "The declaring property value type " + propValueType.Name + " does not have a declaring type.";
            return false;
        }
        if (!baseType.InheritsFromOrImplements<IRelation>()) {
            reason = "The declaring property value type " + propValueType.Name + " does not implement IRelation or inherit from a type that implements IRelation.";
            return false;
        }
        var relationVariant1 = getRelationClassType(baseType);
        var assembly = propValueType.Assembly; // not looking at other assemblies
        List<Type> relationTypeMatches = new List<Type>();
        foreach (var type2 in assembly.GetTypes()) {
            if (type2.InheritsFromOrImplements<IRelation>() && type2 != typeof(IRelation)) {
                var relationVariant2 = getRelationClassType(type2);
                if (relationVariant2 == relationVariant1 && type2.BaseType != null) {
                    if (genericArguments.SequenceEqual(type2.BaseType.GetGenericArguments())) {
                        relationTypeMatches.Add(type2);
                    }
                }
            }
        }
        if (relationTypeMatches.Count == 1) {
            relationType = relationTypeMatches[0];
            return true;
        } else if (relationTypeMatches.Count > 1) {
            reason = "Found multiple relation types for " + propValueType.Name + ": " + string.Join(", ", relationTypeMatches.Select(t => t.FullName));
            reason += ". Please specify the relation type explicitly using the extra genric parameter TRelationReference in the decaration of the relation.";
            return false;
        } else {
            reason = "Could not find any matching relation type for " + propValueType.Name + ". ";
            reason += "Please specify the relation type explicitly using the extra genric parameter TRelationReference in the decaration of the relation.";
            return false;
        }
    }
    static RelationPropertyModel getRelationPropertyModel(RelationPropertyAttribute attr, MemberInfo m, Type valueType) {
        var r = new RelationPropertyModel();
        r.TextIndexRelatedContent = attr.TextIndexRelatedContent;
        r.TextIndexRelatedDisplayName = attr.TextIndexRelatedDisplayName;
        r.TextIndexRecursiveLevelLimit = attr.TextIndexRecursiveLevelLimit;
        Type? typeOfRelated = null;
        Type? relationType = null;
        if (m is PropertyInfo pi && pi.PropertyType.InheritsFromOrImplements<IRelationProperty>()) {
            r.RelationValueType = RelationValueType.Native;
            if (!tryFindTypeObjectForRelation(pi.PropertyType, out relationType, out var reason)) {
                throw new Exception("Could not resolve the relation for propery \"" + pi.DeclaringType!.Name + "." + pi.Name + "\". " + reason);
            }
            var propValueType = pi.PropertyType; // FromToNodes: inherits from OneProperty / ManyProperty
            r.FromTargetToSource = isRelationPropertyFromTargetToSource(relationType, propValueType);
            var propValueTypeBaseType = propValueType.BaseType;
            if (propValueTypeBaseType == null) throw new Exception("Could not determine base type for " + m.DeclaringType?.FullName + "." + m.Name + " - " + propValueType.FullName + ".");
            typeOfRelated = propValueTypeBaseType.GetGenericArguments()[0];
            if (valueType.InheritsFromOrImplements<IManyProperty>()) {
                r.IsMany = true;
            } else if (valueType.InheritsFromOrImplements<IOneProperty>()) {
                r.IsMany = false;
                typeOfRelated = m.DeclaringType!;
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
                } else if (valueType.InheritsFromOrImplements<IList>()) {
                    r.RelationValueType = RelationValueType.List;
                } else if (valueType.InheritsFromOrImplements<ICollection>()) {
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
