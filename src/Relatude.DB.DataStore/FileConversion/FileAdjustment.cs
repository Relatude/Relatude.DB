using Relatude.DB.Common;
using Relatude.DB.Web;

namespace Relatude.DB.FileConversion;

public class FileIdWithAdjustment {
    public FileIdWithAdjustment(Guid fileId, FileAdjustmentBase adj) {
        FileId = fileId;
        Adjustment = adj;
    }
    public Guid FileId { get; }
    public FileAdjustmentBase Adjustment { get; }
    Guid? _key = null;
    public Guid GetKey() => _key ??= FileId.CombineHashGuid(Adjustment.GetKey());
}
public enum FileAdjustmentType {
    Image,
    ImageMetaData,
}
public abstract class FileAdjustmentBase {
    public FileFormat RequestedFormat { get; set; }
    public abstract FileAdjustmentType GetAdjustmentType();
    static Dictionary<string, Guid> _keyCache = [];
    Guid? _key;
    public Guid GetKey() {
        if (_key == null) {
            var stringKey = GetStringKey();
            lock (_keyCache) {
                if (!_keyCache.TryGetValue(stringKey, out var guid)) {
                    guid = stringKey.GenerateHashGuid();
                    _keyCache[stringKey] = guid;
                }
                _key = guid;
            }
        }
        return _key.Value;
    }
    protected abstract string GetStringKey();
}
public class FileAdjustmentImage : FileAdjustmentBase {
    public override FileAdjustmentType GetAdjustmentType() => FileAdjustmentType.Image;
    public int? Width { get; set; } // canvas width
    public int? Height { get; set; } // canvas height
    public double? Zoom { get; set; } = null; // in percentage. 100 means 1:1 (no zoom), 200 means 2x zoom into the image, 50 means zoom out.
    public int? FocusX { get; set; } // in reference to the original image
    public int? FocusY { get; set; } // in reference to the original image
    public int? OffsetX { get; set; } // in reference to the original image
    public int? OffsetY { get; set; } // in reference to the original image
    public double? Rotation { get; set; } = null; // in degrees
    public double? Brightness { get; set; } // -100..100, where 0 means no change, -100 means completely black, and 100 means completely white.
    public double? Contrast { get; set; } // -100..100, where 0 means no change, -100 means completely black, and 100 means completely white.
    public double? Saturation { get; set; } // -100..100, where 0 means no change, -100 means completely desaturated, and 100 means fully saturated.
    public double? HueShift { get; set; } // -180..180, where 0 means no change, -180 means shift hue by -180 degrees, and 180 means shift hue by 180 degrees.
    public double? Sharpness { get; set; } // 0-100, where 0 means no change, -100 means completely blurred, and 100 means maximum sharpness.
    public string? BackgroundColor { get; set; } = null; // Hex color code (e.g. "#RRGGBB" or "#RRGGBBAA"). Only used for certain crop modes when the output canvas is larger than the resized image.
    public bool? AutoBackgroundColor { get; set; } = null; // Automatically determine the background color based edge analysis of the image.
    public ImageCropMode? CropMode { get; set; }
    public int? Quality { get; set; } // 0-100, where 100 is the best quality. Only applicable for lossy formats like JPEG.

    string? _key = null;
    protected override string GetStringKey() {
        if (_key != null) return _key;
        Span<byte> buf = stackalloc byte[96];
        int p = 0;
        BitConverter.TryWriteBytes(buf[p..], (int)RequestedFormat); p += 4;
        BitConverter.TryWriteBytes(buf[p..], Width ?? int.MinValue); p += 4;
        BitConverter.TryWriteBytes(buf[p..], Height ?? int.MinValue); p += 4;
        BitConverter.TryWriteBytes(buf[p..], Zoom ?? double.NaN); p += 8;
        BitConverter.TryWriteBytes(buf[p..], FocusX ?? int.MinValue); p += 4;
        BitConverter.TryWriteBytes(buf[p..], FocusY ?? int.MinValue); p += 4;
        BitConverter.TryWriteBytes(buf[p..], OffsetX ?? int.MinValue); p += 4;
        BitConverter.TryWriteBytes(buf[p..], OffsetY ?? int.MinValue); p += 4;
        BitConverter.TryWriteBytes(buf[p..], Rotation ?? double.NaN); p += 8;
        BitConverter.TryWriteBytes(buf[p..], Brightness ?? double.NaN); p += 8;
        BitConverter.TryWriteBytes(buf[p..], Contrast ?? double.NaN); p += 8;
        BitConverter.TryWriteBytes(buf[p..], Saturation ?? double.NaN); p += 8;
        BitConverter.TryWriteBytes(buf[p..], HueShift ?? double.NaN); p += 8;
        BitConverter.TryWriteBytes(buf[p..], Sharpness ?? double.NaN); p += 8;
        BitConverter.TryWriteBytes(buf[p..], (int)(CropMode ?? (ImageCropMode)(-1))); p += 4;
        BitConverter.TryWriteBytes(buf[p..], Quality ?? int.MinValue);
        return _key = Convert.ToHexString(buf) + (AutoBackgroundColor?.ToString() ?? "") + (BackgroundColor ?? "");
    }
}
