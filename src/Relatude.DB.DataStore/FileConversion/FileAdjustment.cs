using Relatude.DB.Common;
using Relatude.DB.Web;

namespace Relatude.DB.FileConverter;

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
            if (!_keyCache.TryGetValue(stringKey, out var guid)) {
                guid = stringKey.GenerateHashGuid();
                _keyCache[stringKey] = guid;
            }
            _key = guid;
        }
        return _key.Value;
    }
    protected abstract string GetStringKey();
}
public class FileAdjustmentImage : FileAdjustmentBase {
    public int? Width { get; set; } // canvas width
    public int? Height { get; set; } // canvas height
    public double? Scale { get; set; } = null; // in percentage. 100 means no change, 50 means half size, 200 means double size.
    public double? Zoom { get; set; } = null; // in percentage. 100 means 1:1 (no zoom), 200 means 2x zoom into the image, 50 means zoom out.
    public int? FocusX { get; set; } // in reference to the original image
    public int? FocusY { get; set; } // in reference to the original image
    public int? OffsetX { get; set; } // in reference to the original image
    public int? OffsetY { get; set; } // in reference to the original image
    public double? Rotation { get; set; } = null; // in degrees. 0 means no change, 90 means rotate 90 degrees clockwise, -90 means rotate 90 degrees counter-clockwise.
    public int? Brightness { get; set; }
    public int? Contrast { get; set; }
    public int? Saturation { get; set; }
    public int? Colorize { get; set; }
    public int? HueShift { get; set; }
    public string? BackgroundColor { get; set; } = null;
    public ImageCropMode? CropMode { get; set; }
    // public string? AIInstructions { get; set; } // Remove background, enhance details, etc.
    public int? Quality { get; set; } // 0-100, where 100 is the best quality. Only applicable for lossy formats like JPEG.
    public FileAdjustmentImageMetaData[]? MetaData { get; set; }
    public override FileAdjustmentType GetAdjustmentType() => FileAdjustmentType.Image;
    string? _key = null;
    protected override string GetStringKey() {
        if (_key != null) return _key;
        Span<byte> buf = stackalloc byte[80];
        int p = 0;
        BitConverter.TryWriteBytes(buf[p..], (int)RequestedFormat);          p += 4;
        BitConverter.TryWriteBytes(buf[p..], Width      ?? int.MinValue);    p += 4;
        BitConverter.TryWriteBytes(buf[p..], Height     ?? int.MinValue);    p += 4;
        BitConverter.TryWriteBytes(buf[p..], Scale      ?? double.NaN);      p += 8;
        BitConverter.TryWriteBytes(buf[p..], Zoom       ?? double.NaN);      p += 8;
        BitConverter.TryWriteBytes(buf[p..], FocusX     ?? int.MinValue);    p += 4;
        BitConverter.TryWriteBytes(buf[p..], FocusY     ?? int.MinValue);    p += 4;
        BitConverter.TryWriteBytes(buf[p..], OffsetX    ?? int.MinValue);    p += 4;
        BitConverter.TryWriteBytes(buf[p..], OffsetY    ?? int.MinValue);    p += 4;
        BitConverter.TryWriteBytes(buf[p..], Rotation   ?? double.NaN);      p += 8;
        BitConverter.TryWriteBytes(buf[p..], Brightness ?? int.MinValue);    p += 4;
        BitConverter.TryWriteBytes(buf[p..], Contrast   ?? int.MinValue);    p += 4;
        BitConverter.TryWriteBytes(buf[p..], Saturation ?? int.MinValue);    p += 4;
        BitConverter.TryWriteBytes(buf[p..], Colorize   ?? int.MinValue);    p += 4;
        BitConverter.TryWriteBytes(buf[p..], HueShift   ?? int.MinValue);    p += 4;
        BitConverter.TryWriteBytes(buf[p..], (int)(CropMode ?? (ImageCropMode)(-1))); p += 4;
        BitConverter.TryWriteBytes(buf[p..], Quality    ?? int.MinValue);
        return _key = Convert.ToHexString(buf) + (BackgroundColor ?? "");
    }
}
public class FileAdjustmentImageMetaData : FileAdjustmentBase {
    public int? Wids1th { get; set; }
    public override FileAdjustmentType GetAdjustmentType() => FileAdjustmentType.ImageMetaData;
    string? _key = null;
    protected override string GetStringKey() => _key ??= $"{RequestedFormat}_{Wids1th}";
}