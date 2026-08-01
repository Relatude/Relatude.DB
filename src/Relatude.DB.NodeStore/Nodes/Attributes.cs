using Relatude.DB.Datamodels.Properties;
namespace Relatude.DB.Nodes;

internal interface IAttrWithUniqueContraints {
    bool UniqueValues { get; set; }
}
internal interface IAttrScalarProperty {
    double FacetRangePowerBase { get; set; }
    int FacetRangeCount { get; set; }
}
internal interface IAttrWithNotFacet {
    bool NotFacet { get; set; }
}

public enum BoolValue : int {
    Default = 0,
    False = 1,
    True = -1
}
/// <summary>
/// Attribute used to exclude types and properties from being included in the datamodel. 
/// </summary>
public class ExcludeAttribute : Attribute { }
/// <summary>
/// Attribute used to mark a class or interface as a node type in the datamodel. Can be used on classes, interfaces and structs.
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public class NodeAttribute : Attribute {
    public string? Id { get; set; }
    public int MinNoInstances { get; set; } = 0;
    public int MaxNoInstances { get; set; } = int.MaxValue;
    public BoolValue InstantTextIndexing { get; set; } = BoolValue.Default;
    public BoolValue TextIndex { get; set; }
    public BoolValue SemanticIndex { get; set; }
    public double TextIndexBoost { get; set; } = 0;
}
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public class ChangedUtcPropertyAttribute : Attribute {
}
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public class CreatedUtcPropertyAttribute : Attribute {
}
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public class DisplayNamePropertyAttribute : Attribute {
}
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public class AddressPropertyAttribute : Attribute {
}
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public class PublicIdPropertyAttribute : Attribute {
}
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public class InternalIdPropertyAttribute : Attribute {
}
public abstract class PropertyAttribute : Attribute {
    public string? Id { get; set; }
    public string? ReadAccess { get; set; }
    public string? WriteAccess { get; set; }
    public bool ExcludeFromTextIndex { get; set; }
    public int TextIndexBoost { get; set; }
    public bool DisplayName { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public class BooleanPropertyAttribute : PropertyAttribute, IAttrWithNotFacet {
    public bool DefaultValue { get; set; }
    public bool Indexed { get; set; }
    public bool NotFacet { get; set; } // excluded from faceting even when indexed
}
[AttributeUsage(AttributeTargets.Property)]
public class IntegerPropertyAttribute : PropertyAttribute, IAttrWithUniqueContraints, IAttrScalarProperty, IAttrWithNotFacet {
    public bool IsEnum { get; set; }
    public string? FullEnumTypeName { get; set; }
    public int DefaultValue { get; set; }
    public bool Indexed { get; set; }
    public bool NotFacet { get; set; } // excluded from faceting even when indexed
    public bool UniqueValues { get; set; }
    public double FacetRangePowerBase { get; set; }
    public int FacetRangeCount { get; set; }
    public int MinValue = int.MinValue;
    public int MaxValue = int.MaxValue;
    public int[]? LegalValues;
    public string[]? LegalValueNames; // enum value names, parallel to LegalValues (auto-populated for enum properties)
}
[AttributeUsage(AttributeTargets.Property)]
public class DecimalPropertyAttribute : PropertyAttribute, IAttrWithUniqueContraints, IAttrScalarProperty, IAttrWithNotFacet {
    // decimal is not a legal attribute parameter type, values are given as invariant culture strings:
    public string? DefaultValue { get; set; }
    public bool Indexed { get; set; }
    public bool NotFacet { get; set; } // excluded from faceting even when indexed
    public string? MinValue; // null means decimal.MinValue
    public string? MaxValue; // null means decimal.MaxValue
    public bool UniqueValues { get; set; }
    public double FacetRangePowerBase { get; set; }
    public int FacetRangeCount { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public class LongPropertyAttribute : PropertyAttribute, IAttrWithUniqueContraints, IAttrScalarProperty, IAttrWithNotFacet {
    public long DefaultValue { get; set; }
    public bool Indexed { get; set; }
    public bool NotFacet { get; set; } // excluded from faceting even when indexed
    public long MinValue = long.MinValue;
    public long MaxValue = long.MaxValue;
    public bool UniqueValues { get; set; }
    public double FacetRangePowerBase { get; set; }
    public int FacetRangeCount { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public class GuidPropertyAttribute : PropertyAttribute, IAttrWithUniqueContraints {
    // Guid is not a legal attribute parameter type, value is given as string:
    public string? DefaultValue { get; set; }
    public bool Indexed { get; set; }
    public bool UniqueValues { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public class DateTimePropertyAttribute : PropertyAttribute, IAttrWithUniqueContraints, IAttrScalarProperty, IAttrWithNotFacet {
    // DateTime is not a legal attribute parameter type, values are given as round-trip ("O") strings:
    public string? DefaultValue { get; set; }
    public bool Indexed { get; set; }
    public bool NotFacet { get; set; } // excluded from faceting even when indexed
    public string? MinValue; // null means DateTime.MinValue
    public string? MaxValue; // null means DateTime.MaxValue
    public bool UniqueValues { get; set; }
    public double FacetRangePowerBase { get; set; }
    public int FacetRangeCount { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public class DateTimeOffsetPropertyAttribute : PropertyAttribute, IAttrWithUniqueContraints, IAttrScalarProperty, IAttrWithNotFacet {
    // DateTimeOffset is not a legal attribute parameter type, values are given as round-trip ("O") strings:
    public string? DefaultValue { get; set; }
    public bool Indexed { get; set; }
    public bool NotFacet { get; set; } // excluded from faceting even when indexed
    public string? MinValue; // null means DateTimeOffset.MinValue
    public string? MaxValue; // null means DateTimeOffset.MaxValue
    public bool UniqueValues { get; set; }
    public double FacetRangePowerBase { get; set; }
    public int FacetRangeCount { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public class TimeSpanPropertyAttribute : PropertyAttribute, IAttrWithUniqueContraints, IAttrScalarProperty, IAttrWithNotFacet {
    // TimeSpan is not a legal attribute parameter type, values are given as constant ("c") format strings:
    public string? DefaultValue { get; set; }
    public bool Indexed { get; set; }
    public bool NotFacet { get; set; } // excluded from faceting even when indexed
    public string? MinValue; // null means TimeSpan.MinValue
    public string? MaxValue; // null means TimeSpan.MaxValue
    public bool UniqueValues { get; set; }
    public double FacetRangePowerBase { get; set; }
    public int FacetRangeCount { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public class ByteArrayPropertyAttribute : PropertyAttribute {
}
[AttributeUsage(AttributeTargets.Property)]
public class FloatArrayPropertyAttribute : PropertyAttribute {
}
[AttributeUsage(AttributeTargets.Property)]
public class DoublePropertyAttribute : PropertyAttribute, IAttrScalarProperty, IAttrWithNotFacet {
    public double DefaultValue { get; set; }
    public bool Indexed { get; set; }
    public bool NotFacet { get; set; } // excluded from faceting even when indexed
    public double MinValue = double.MinValue;
    public double MaxValue = double.MaxValue;
    public double FacetRangePowerBase { get; set; }
    public int FacetRangeCount { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public class FloatPropertyAttribute : PropertyAttribute, IAttrScalarProperty, IAttrWithNotFacet {
    public float DefaultValue { get; set; }
    public bool Indexed { get; set; }
    public bool NotFacet { get; set; } // excluded from faceting even when indexed
    public float MinValue = float.MinValue;
    public float MaxValue = float.MaxValue;
    public double FacetRangePowerBase { get; set; }
    public int FacetRangeCount { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public class StringPropertyAttribute : PropertyAttribute, IAttrWithUniqueContraints, IAttrWithNotFacet {
    public string? DefaultValue { get; set; } = string.Empty;
    public int MinLength { get; set; } = 0;
    public int MaxLength { get; set; } = int.MaxValue;
    public StringValueType StringType = StringValueType.AnyString;
    public bool PrefixSearch { get; set; }
    public bool InfixSearch { get; set; }
    public bool Indexed { get; set; }
    public bool NotFacet { get; set; } // excluded from faceting even when indexed
    public bool IndexedByWords { get; set; }
    public bool IndexedBySemantic { get; set; }
    public bool PreloadWordIndex { get; set; }
    public int MinWordLength { get; set; } = 3;
    public int MaxWordLength { get; set; } = 30;
    public string[]? LegalValues;
    public string? RegularExpression { get; set; }
    public bool IgnoreDuplicateEmptyValues { get; set; }
    public bool UniqueValues { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public class StringArrayPropertyAttribute : PropertyAttribute, IAttrWithUniqueContraints, IAttrWithNotFacet {
    public bool Indexed { get; set; }
    public bool NotFacet { get; set; } // excluded from faceting even when indexed
    public bool UniqueValues { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public class GuidArrayPropertyAttribute : PropertyAttribute, IAttrWithUniqueContraints, IAttrWithNotFacet {
    public bool Indexed { get; set; }
    public bool NotFacet { get; set; } // excluded from faceting even when indexed
    public bool UniqueValues { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public class EnumArrayPropertyAttribute : PropertyAttribute, IAttrWithUniqueContraints, IAttrWithNotFacet {
    public bool Indexed { get; set; }
    public bool NotFacet { get; set; } // excluded from faceting even when indexed
    public bool UniqueValues { get; set; }
    // enum metadata, auto-populated from the property's element type (like IntegerPropertyAttribute for scalar enums):
    public string? FullEnumTypeName { get; set; }
    public int[]? LegalValues;
    public string[]? LegalValueNames;
}
[AttributeUsage(AttributeTargets.Property)]
public class HtmlPropertyAttribute : StringPropertyAttribute {
    public HtmlPropertyAttribute() {
        StringType = StringValueType.HTML;
    }
}
[AttributeUsage(AttributeTargets.Property)]
public class FilePropertyAttribute : PropertyAttribute {
    // Guid is not a legal attribute parameter type, value is given as string:
    public string? FileStorageProviderId { get; set; }

}
[AttributeUsage(AttributeTargets.Property)]
public class EmbeddedPropertyAttribute : PropertyAttribute {
    public IncludeTypeOptions IncludeTypes { get; set; } = IncludeTypeOptions.ThisTypeAndDescending;
    public string[]? InnerTypeIds { get; set; }    
}
public enum KeyPropertyType {
    NodeGuidId,
    NodeIntegerId,
    NodeProperty,
}
[AttributeUsage(AttributeTargets.Property)]
public class EmbeddedMapPropertyAttribute : EmbeddedPropertyAttribute {
    public KeyPropertyType KeyType { get; set; }
    public string? KeyProperty { get; set; }    
}
[AttributeUsage(AttributeTargets.Property)]
public class ReferencePropertyAttribute : PropertyAttribute, IAttrWithNotFacet {
    public IncludeTypeOptions IncludeTypes { get; set; } = IncludeTypeOptions.ThisTypeAndDescending;
    public string[]? TypeIds { get; set; }
    public bool Indexed { get; set; } // required for filtering and faceting on the reference
    public bool NotFacet { get; set; } // excluded from faceting even when indexed
}
[AttributeUsage(AttributeTargets.Property)]
public class ReferencesPropertyAttribute : PropertyAttribute, IAttrWithUniqueContraints, IAttrWithNotFacet {
    public IncludeTypeOptions IncludeTypes { get; set; } = IncludeTypeOptions.ThisTypeAndDescending;
    public string[]? TypeIds { get; set; }
    public bool Indexed { get; set; } // required for faceting on the references
    public bool NotFacet { get; set; } // excluded from faceting even when indexed
    public bool UniqueValues { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public class RelationPropertyAttribute : PropertyAttribute {
    public string? Relation { get; set; }
    public bool RightToLeft { get; set; }

    public bool TextIndexRelatedDisplayName { get; set; }
    public bool TextIndexRelatedContent { get; set; }
    public int TextIndexRecursiveLevelLimit { get; set; }
    public bool Facet { get; set; } // opt-in: enables faceting on this relation property

}
[AttributeUsage(AttributeTargets.Property)]
public class RelationPropertyAttribute<T> : RelationPropertyAttribute where T : IRelation {
}
[AttributeUsage(AttributeTargets.Class)]
public class RelationAttribute : Attribute {
    public string? Id { get; set; }
    public string[]? SourceTypes { get; set; }
    public string[]? TargetTypes { get; set; }
    public bool DisallowCircularReferences { get; set; }
}
