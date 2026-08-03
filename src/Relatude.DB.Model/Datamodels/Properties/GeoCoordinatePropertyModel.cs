using Relatude.DB.Common;
namespace Relatude.DB.Datamodels.Properties;
public class GeoCoordinatePropertyModel : PropertyModel {
    public override bool ExcludeFromTextIndex { get; set; } = true;
    public override PropertyType PropertyType { get => PropertyType.GeoCoordinate; }
    public override object GetDefaultValue() => GeoCoordinate.Empty;
    public override string GetDefaultValueAsCode() => "default";
    public static GeoCoordinate ForceValueType(object? value, out bool changed) {
        if (value is GeoCoordinate g) {
            changed = false;
            return g;
        }
        changed = true;
        if (value is null) return GeoCoordinate.Empty;
        if (value is string s && GeoCoordinate.TryParse(s, out var parsed)) return parsed;
        if (value is ulong u && GeoCoordinate.TryFromStorageValue(u, out var fromCode)) return fromCode;
        return GeoCoordinate.Empty;
    }
    // node data codec: the 8 byte storage value (62-bit Morton code + 1, 0 = Empty)
    public static byte[] GetBytes(GeoCoordinate value) => BitConverter.GetBytes(value.StorageValue);
    public static GeoCoordinate GetValue(byte[] bytes) => GeoCoordinate.FromStorageValue(BitConverter.ToUInt64(bytes));
}
