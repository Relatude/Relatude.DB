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
    /// <summary>
    /// How few nodes of this type, its descendants included, there may be: a transaction that takes the
    /// count below it fails. The count may start below it - with 1, the last node cannot be deleted.
    /// </summary>
    public int MinNoInstances { get; set; } = 0;
    /// <summary>How many nodes of this type, its descendants included, there may be: a transaction that
    /// takes the count above it fails.</summary>
    public int MaxNoInstances { get; set; } = int.MaxValue;
    public BoolValue InstantTextIndexing { get; set; } = BoolValue.Default;
    public BoolValue TextIndex { get; set; }
    public BoolValue SemanticIndex { get; set; }
    public double TextIndexBoost { get; set; } = 0;
}
/// <summary>
/// Overrides attributes of a property this type inherits, for this type and the types inheriting from
/// it, without touching the type that declares the property - which may be in an assembly you cannot
/// change:
/// <code>
/// [Node]
/// [PropertyOverride(nameof(IContent.Title), DefaultValue = "Untitled news", TextIndexBoost = 2)]
/// public class NewsArticle : IContent { ... }
/// </code>
/// Only the attributes that may differ between the types that have a property can be overridden here:
/// the default value, whether the value is in the text index and with what boost, and whether it is
/// part of the display name. A type that sets its own value wins over its base types; a node type's own
/// switches (TextIndex and the like) are set with <see cref="NodeAttribute"/> on the derived type, as
/// they are inherited too.
/// <para>
/// A type can also ask for the property's value index with <see cref="Indexed"/>, so it can filter,
/// sort and facet on it, and for the property to be a facet with <see cref="NotFacet"/> set to
/// <see cref="BoolValue.False"/> where the declaration leaves it out of the facets. There is one index
/// and one set of facet counts, shared by every type that has the property: the property has them when
/// its declaration says so or any type asks, and they hold the values of every node that has the
/// property. Everything else about a property - its other index settings, its rules - is one setting
/// for the property wherever it is used, and stays with its declaration.
/// </para>
/// <para>
/// On a member that implements an interface property, the name can be left out:
/// <c>[PropertyOverride(DefaultValue = "Untitled news")] public string Title { get; set; }</c>.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Struct | AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = true, Inherited = false)]
public class PropertyOverrideAttribute : Attribute {
    /// <summary>The value of <see cref="TextIndexBoost"/> when it is not overridden.</summary>
    public const int NotSet = int.MinValue;
    public PropertyOverrideAttribute() { }
    /// <param name="property">The name of the inherited property, best given with nameof.</param>
    public PropertyOverrideAttribute(string property) => Property = property;
    /// <summary>The name of the inherited property. Left out on a member, which overrides itself.</summary>
    public string? Property { get; }
    /// <summary>
    /// The default for this type: a constant of the property's type. Decimals, dates, durations and guids
    /// are given as strings in the formats the property attributes use (invariant culture, round-trip "O"
    /// for dates, constant "c" for durations).
    /// </summary>
    public object? DefaultValue { get; set; }
    public BoolValue ExcludeFromTextIndex { get; set; }
    public int TextIndexBoost { get; set; } = NotSet;
    public BoolValue DisplayName { get; set; }
    /// <summary>
    /// True asks for the property's value index (see the class remarks: one index, kept when any type
    /// asks). False asks for nothing; it cannot take the index away from a declaration that has one.
    /// </summary>
    public BoolValue Indexed { get; set; }
    /// <summary>
    /// False asks for the property to be a facet where its declaration says NotFacet (see the class
    /// remarks: one set of facet counts, kept when any type asks; a facet needs the value index too).
    /// True asks for nothing; it cannot take the facet away from a declaration that has one.
    /// </summary>
    public BoolValue NotFacet { get; set; }
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
public class GeoCoordinatePropertyAttribute : PropertyAttribute {
    public bool Indexed { get; set; }
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
    public int MinWordLength { get; set; } = 3;
    public int MaxWordLength { get; set; } = 30;
    /// <summary>The values allowed (compared ordinally). An empty value is always allowed; require a value with MinLength = 1.</summary>
    public string[]? LegalValues;
    /// <summary>A pattern every value written must match - the empty value too, so allow that in the pattern
    /// when the value is optional: ^([a-z]+)?$. It is a match anywhere in the value unless anchored with ^ and $.</summary>
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
