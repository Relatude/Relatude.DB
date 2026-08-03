using System.Buffers.Binary;
using System.Globalization;
namespace Relatude.DB.Datamodels.Properties;
public class DateTimeOffsetPropertyModel : PropertyModel, IPropertyModelUniqueContraints, IScalarProperty {
    public override bool ExcludeFromTextIndex { get; set; } = true;
    public override PropertyType PropertyType { get => PropertyType.DateTimeOffset; }
    public DateTimeOffset DefaultValue { get; set; }
    public DateTimeOffset MinValue { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset MaxValue { get; set; } = DateTimeOffset.MaxValue;
    public double FacetRangePowerBase { get; set; }
    public int FacetRangeCount { get; set; }
    public override object GetDefaultValue() => DefaultValue;
    public static DateTimeOffset ForceValueType(object? value, out bool changed) {
        if (value is DateTimeOffset dt) {
            changed = false;
            return dt;
        }
        changed = true;
        if (value is null) return default;
        //if (value is byte) return (decimal)value;
        if (value is long l) return new DateTimeOffset(l, TimeSpan.Zero);
        //if (value is byte) return (decimal)value;
        //if (value is decimal) return (int)value;
        //if (value is double) return (int)value;
        //if (value is float) return (int)value;
        if (value is string sv) {
            if (DateTimeOffset.TryParse(sv, CultureInfo.InvariantCulture, out var v)) {
                return v;
            }
            if (long.TryParse(sv, CultureInfo.InvariantCulture, out var lv)) {
                return new DateTimeOffset(lv, TimeSpan.Zero);
            }
        }
        return default;
    }
    public override string GetDefaultValueAsCode() =>
        $"new DateTimeOffset({DefaultValue.Ticks}, new TimeSpan({DefaultValue.Offset.Ticks}))";
    // node data codec: 8 bytes utc ticks + 2 bytes offset in minutes (offsets are whole minutes, range ±14h)
    public static byte[] GetBytes(DateTimeOffset value) {
        var bytes = new byte[10];
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0, 8), value.UtcTicks);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(8, 2), checked((short)value.Offset.TotalMinutes));
        return bytes;
    }
    public static DateTimeOffset GetValue(byte[] bytes) {
        // 8 bytes = unix milliseconds: a read case for this encoding predates any working write
        // path, so no stored data should have it, but it is kept readable just in case
        if (bytes.Length == 8) return DateTimeOffset.FromUnixTimeMilliseconds(BitConverter.ToInt64(bytes));
        var utcTicks = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(0, 8));
        var offsetMinutes = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(8, 2));
        return new DateTimeOffset(utcTicks, TimeSpan.Zero).ToOffset(TimeSpan.FromMinutes(offsetMinutes));
    }
}
