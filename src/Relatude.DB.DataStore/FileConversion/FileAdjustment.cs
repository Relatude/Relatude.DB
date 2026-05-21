using Relatude.DB.Common;

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
    static Dictionary<string, Guid> _staticKeyCache = [];
    Guid? _key;
    string? _stringKey;
    object _localKeyLock = new();
    public Guid GetKey() {
        lock (_localKeyLock) {
            if (_key != null) return _key.Value;
            if (_stringKey == null) _stringKey = GenerateStringKey();
        }
        lock (_staticKeyCache) {
            if (!_staticKeyCache.TryGetValue(_stringKey, out var guid)) {
                guid = _stringKey.GenerateHashGuid();
                _staticKeyCache[_stringKey] = guid;
            }
            _key = guid;
        }
        return _key.Value;
    }
    public virtual void BasicSanitization() {
        if (RequestedFormat == FileFormat.Unknown) RequestedFormat = FileFormat.Png;
    }
    protected abstract string GenerateStringKey();
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

    protected override string GenerateStringKey() {
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
        var key = Convert.ToHexString(buf);
        if (AutoBackgroundColor.HasValue) key += AutoBackgroundColor.Value.ToString();
        if (BackgroundColor != null) key += BackgroundColor;
        return key;
    }
    public override void BasicSanitization() {
        base.BasicSanitization();
        if (Width.HasValue) Width = Width <= 0 ? null : Math.Clamp(Width.Value, 1, 10_000);
        if (Height.HasValue) Height = Height <= 0 ? null : Math.Clamp(Height.Value, 1, 10_000);
        if (Zoom.HasValue) Zoom = Zoom <= 0 ? null : Math.Clamp(Zoom.Value, 0.1, 10_000);
        if (FocusX.HasValue) FocusX = Math.Clamp(FocusX.Value, -10_000, 10_000);
        if (FocusY.HasValue) FocusY = Math.Clamp(FocusY.Value, -10_000, 10_000);
        if (OffsetX.HasValue) OffsetX = Math.Clamp(OffsetX.Value, -10_000, 10_000);
        if (OffsetY.HasValue) OffsetY = Math.Clamp(OffsetY.Value, -10_000, 10_000);
        if (Rotation.HasValue) Rotation = Math.Clamp(Rotation.Value, -360, 360);
        if (Quality.HasValue) Quality = Math.Clamp(Quality.Value, 0, 100);
        if (Brightness.HasValue) Brightness = Math.Clamp(Brightness.Value, -100, 100);
        if (Contrast.HasValue) Contrast = Math.Clamp(Contrast.Value, -100, 100);
        if (Saturation.HasValue) Saturation = Math.Clamp(Saturation.Value, -100, 100);
        if (HueShift.HasValue) HueShift = Math.Clamp(HueShift.Value, -180, 180);
        if (Sharpness.HasValue) Sharpness = Math.Clamp(Sharpness.Value, 0, 100);
    }
}
