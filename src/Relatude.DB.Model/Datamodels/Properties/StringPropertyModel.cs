using System.Globalization;

namespace Relatude.DB.Datamodels.Properties;
public class StringPropertyModel : PropertyModel, IPropertyModelUniqueContraints {
    public override bool ExcludeFromTextIndex { get; set; } = false;
    public override PropertyType PropertyType { get => PropertyType.String; }
    public IndexStorageType TextIndexType { get; set; }
    public string? DefaultValue { get; set; } = string.Empty;
    public int MinLength { get; set; } = 0;
    public int MaxLength { get; set; } = int.MaxValue;
    public StringValueType StringType { get; set; } = StringValueType.AnyString;
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
    /// <summary>
    /// The values allowed, compared ordinally; null allows any. An empty value is always allowed - it
    /// is what a node holds before the value is set - so a value is required with <see cref="MinLength"/>.
    /// Left out of the JSON when null, so a model that does not use it has the checksum it had before
    /// the member existed (a changed checksum rebuilds the state and every index).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string[]? LegalValues { get; set; }
    public override string? GetDefaultDeclaration() => "string.Empty";
    public override object? GetDefaultValue() => DefaultValue;
    public static string ForceValueType(object? value, out bool changed) {
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
    public override string GetDefaultValueAsCode() => GetValueAsCode(DefaultValue);
    public override string GetValueAsCode(object? value) => CSharpLiteral.String(value as string);
    public override string? GetTextIndex(object value) {
        return value.ToString();
    }
}