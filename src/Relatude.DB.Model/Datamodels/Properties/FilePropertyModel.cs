using Relatude.DB.Common;
namespace Relatude.DB.Datamodels.Properties;
public class FilePropertyModel : PropertyModel {
    public override bool ExcludeFromTextIndex { get; set; } = false;
    public override PropertyType PropertyType { get => PropertyType.File; }
    public override string? GetDefaultDeclaration() => typeof(FileValue).Namespace + "." + nameof(FileValue) + "." + nameof(FileValue.Empty);
    public Guid FileStorageProviderId { get; set; }
    public override object GetDefaultValue() => throw new NotImplementedException();
    public override bool IsReferenceTypeAndMustCopy() => true;
    public static FileValue ForceValueType(object value, out bool changed) {
        if (value is FileValue fileValue) {
            changed = false;
            return fileValue;
        }
        changed = true;
        return FileValue.Empty;
    }

    public static FileValue GetValue(byte[] bytes) {
        return FileValue.FromBytes(bytes);
    }
    public static byte[] GetBytes(FileValue value) {
        return value.ToBytes();
    }
    public override string GetDefaultValueAsCode() => typeof(FileValue).FullName + ".Empty";
    public override string? GetTextIndex(object value) => ForceValueType(value, out _).Name;
}
