using Relatude.DB.Datamodels.Properties;
namespace Relatude.DB.Nodes;
internal interface IAttrWithUniqueContraints {
    bool UniqueValues { get; set; }
}
internal interface IAttrScalarProperty {
    double FacetRangePowerBase { get; set; }
    int FacetRangeCount { get; set; }
}

public enum BoolValue : int {
    Default = 0,
    False = 1,
    True = -1
}
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
public class PublicIdPropertyAttribute : Attribute {
}
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public class InternalIdPropertyAttribute : Attribute {
}
public abstract class PropertyAttribute : Attribute {
    public string? Id { get; set; }
    public string? ReadAccess { get; set; }
    public string? WriteAccess { get; set; }
    public BoolValue ExcludeFromTextIndex { get; set; }
    public int TextIndexBoost { get; set; }
    public bool DisplayName { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public class BooleanPropertyAttribute : PropertyAttribute {
    public bool DefaultValue { get; set; }
    public BoolValue Indexed { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public class IntegerPropertyAttribute : PropertyAttribute, IAttrWithUniqueContraints, IAttrScalarProperty {
    public bool IsEnum { get; set; }
    public string? FullEnumTypeName { get; set; }
    public int DefaultValue { get; set; }
    public BoolValue Indexed { get; set; }
    public bool UniqueValues { get; set; }
    public double FacetRangePowerBase { get; set; }
    public int FacetRangeCount { get; set; }
    public int MinValue = int.MinValue;
    public int MaxValue = int.MaxValue;
    public int[]? LegalValues;
}
[AttributeUsage(AttributeTargets.Property)]
public class DecimalPropertyAttribute : PropertyAttribute, IAttrWithUniqueContraints {
    public int DefaultValue { get; set; }
    public BoolValue Indexed { get; set; }
    public decimal MinValue = decimal.MinValue;
    public decimal MaxValue = decimal.MaxValue;
    public bool UniqueValues { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public class LongPropertyAttribute : PropertyAttribute, IAttrWithUniqueContraints {
    public int DefaultValue { get; set; }
    public BoolValue Indexed { get; set; }
    public long MinValue = long.MinValue;
    public long MaxValue = long.MaxValue;
    public bool UniqueValues { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public class GuidPropertyAttribute : PropertyAttribute, IAttrWithUniqueContraints {
    public Guid DefaultValue { get; set; }
    public BoolValue Indexed { get; set; }
    public bool UniqueValues { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public class DateTimePropertyAttribute : PropertyAttribute, IAttrWithUniqueContraints {
    public DateTime DefaultValue { get; set; }
    public BoolValue Indexed { get; set; }
    public DateTime MinValue = DateTime.MinValue;
    public DateTime MaxValue = DateTime.MaxValue;
    public bool UniqueValues { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public class TimeSpanPropertyAttribute : PropertyAttribute, IAttrWithUniqueContraints {
    public TimeSpan DefaultValue { get; set; }
    public BoolValue Indexed { get; set; }
    public TimeSpan MinValue = TimeSpan.MinValue;
    public TimeSpan MaxValue = TimeSpan.MaxValue;
    public bool UniqueValues { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public class ByteArrayPropertyAttribute : PropertyAttribute {
}
[AttributeUsage(AttributeTargets.Property)]
public class FloatArrayPropertyAttribute : PropertyAttribute {
}
[AttributeUsage(AttributeTargets.Property)]
public class DoublePropertyAttribute : PropertyAttribute {
    public double DefaultValue { get; set; }
    public BoolValue Indexed { get; set; }
    public double MinValue = int.MinValue;
    public double MaxValue = int.MaxValue;
}
[AttributeUsage(AttributeTargets.Property)]
public class FloatPropertyAttribute : PropertyAttribute {
    public float DefaultValue { get; set; }
    public BoolValue Indexed { get; set; }
    public float MinValue = int.MinValue;
    public float MaxValue = int.MaxValue;
}
[AttributeUsage(AttributeTargets.Property)]
public class StringPropertyAttribute : PropertyAttribute, IAttrWithUniqueContraints {
    public string? DefaultValue { get; set; } = string.Empty;
    public int MinLength { get; set; } = 0;
    public int MaxLength { get; set; } = int.MaxValue;
    public StringValueType StringType = StringValueType.AnyString;
    public bool PrefixSearch { get; set; }
    public bool InfixSearch { get; set; }
    public BoolValue Indexed { get; set; }
    public BoolValue IndexedByWords { get; set; }
    public BoolValue IndexedBySemantic { get; set; }
    public bool PreloadWordIndex { get; set; }
    public int MinWordLength { get; set; } = 3;
    public int MaxWordLength { get; set; } = 30;
    public string[]? LegalValues;
    public string? RegularExpression { get; set; }
    public bool IgnoreDuplicateEmptyValues { get; set; }
    public bool UniqueValues { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public class StringArrayPropertyAttribute : PropertyAttribute, IAttrWithUniqueContraints {
    public BoolValue Indexed { get; set; }
    public bool UniqueValues { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public class HtmlPropertyAttribute : StringPropertyAttribute {
    public HtmlPropertyAttribute() {
        StringType = StringValueType.HTML;
    }
}
[AttributeUsage(AttributeTargets.Property)]
public class FilePropertyAttribute : PropertyAttribute {
    public Guid FileStorageProviderId { get; set; }

}


[AttributeUsage(AttributeTargets.Property)]
public class RelationPropertyAttribute : PropertyAttribute {
    public string? Relation { get; set; }
    public bool RightToLeft { get; set; }

    public bool TextIndexRelatedDisplayName { get; set; }
    public bool TextIndexRelatedContent { get; set; }
    public int TextIndexRecursiveLevelLimit { get; set; }

}
[AttributeUsage(AttributeTargets.Property)]
public class RelationPropertyAttribute<T> : RelationPropertyAttribute where T : IRelation {
}
[AttributeUsage(AttributeTargets.Class)]
public class RelationAttribute : Attribute {
    public string? Id { get; set; }
    public string[]? SourceTypes { get; set; }
    public string[]? TargetTypes { get; set; }
}
