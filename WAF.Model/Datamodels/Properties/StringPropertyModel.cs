using System.Globalization;

namespace WAF.Datamodels.Properties;
public class StringPropertyModel : PropertyModel, IPropertyModelUniqueContraints {
    public override bool ExcludeFromTextIndex { get; set; } = false;
    public override PropertyType PropertyType { get => PropertyType.String; }
    public IndexStorageType TextIndexType { get; set; }
    public string? DefaultValue { get; set; } = string.Empty;
    public int MinLength { get; set; } = 0;
    public int MaxLength { get; set; } = int.MaxValue;
    readonly public StringValueType StringType = StringValueType.AnyString;
    public bool PrefixSearch { get; set; }
    public bool InfixSearch { get; set; }
    public bool IndexedByWords { get; set; }
    public bool IndexedBySemantic { get; set; }
    public Guid PropertyIdForEmbeddings { get; set; }
    public int MinWordLength { get; set; } = DefaultMinWordLength;
    public int MaxWordLength { get; set; } = DefaultMaxWordLength;
    public bool IgnoreDuplicateEmptyValues { get; set; }
    public static readonly int DefaultMinWordLength = 3;
    public static readonly int DefaultMaxWordLength = 30;

    public string? RegularExpression { get; set; }
    public override string? GetDefaultDeclaration() => "string.Empty";
    public override object GetDefaultValue() => DefaultValue ?? string.Empty;
    public static string ForceValueType(object value, out bool changed) {
        if (value is null) {
            changed = true;
            return string.Empty;
        }
        if (value is string) {
            changed = false;
            return (string)value;
        }
        if (value is DateTime dt) {
            changed = true;
            return dt.ToString(CultureInfo.InvariantCulture);
        }
        if (value is decimal dec) {
            changed = true;
            return dec.ToString(CultureInfo.InvariantCulture);
        }
        if (value is double d) {
            changed = true;
            return d.ToString(CultureInfo.InvariantCulture);
        }
        changed = true;
        return value.ToString() ?? string.Empty;
    }
    public override string GetDefaultValueAsCode() => $"\"{DefaultValue}\"";
    public override string? GetTextIndex(object value) {
        return value.ToString();
    }
}