using System.Buffers.Binary;
using Relatude.DB.Common;

namespace Relatude.DB.FileConversion;

public class FileIdWithAdjustment {
    public FileIdWithAdjustment(Guid fileId, FileAdjustment adj, PropertyPath propertyPath) {
        FileId = fileId;
        Adjustment = adj;
        PropertyPath = propertyPath;
    }
    public Guid FileId { get; }
    public FileAdjustment Adjustment { get; }
    public PropertyPath PropertyPath { get; }
    Guid? _key = null;
    public Guid GetKey() => _key ??= FileId.CombineHashGuid(Adjustment.GetKey());
}
public enum FileAdjustmentType {
    Image,
    Video,
    Meta,
}
public abstract class FileAdjustment {
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
    public abstract byte[] ToBytes();
    public static FileAdjustment FromBytes(byte[] bytes) {
        var adjustmentType = (FileAdjustmentType)bytes[0];
        return adjustmentType switch {
            FileAdjustmentType.Image => FileAdjustmentImage.FromBytes(bytes),
            FileAdjustmentType.Video => FileAdjustmentVideo.FromBytes(bytes),
            FileAdjustmentType.Meta => FileAdjustmentMeta.FromBytes(bytes),
            _ => throw new NotSupportedException($"Unsupported adjustment type: {adjustmentType}")
        };
    }
}
public class FileAdjustmentMeta : FileAdjustment {
    public override FileAdjustmentType GetAdjustmentType() => FileAdjustmentType.Meta;
    public override byte[] ToBytes() {
        var buf = new byte[5];
        var s = buf.AsSpan();
        s[0] = (byte)FileAdjustmentType.Meta;
        BinaryPrimitives.WriteInt32LittleEndian(s[1..], (int)RequestedFormat);
        return buf;
    }
    public static new FileAdjustmentMeta FromBytes(byte[] bytes) {
        if (bytes.Length != 5) throw new ArgumentException("Invalid byte array length for FileAdjustmentMeta");
        var obj = new FileAdjustmentMeta {
            RequestedFormat = (FileFormat)BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan()[1..])
        };
        return obj;
    }
    protected override string GenerateStringKey() {
        Span<byte> buf = stackalloc byte[4];
        BitConverter.TryWriteBytes(buf, (int)RequestedFormat);
        return buf.ToString();
    }
}
public class FileAdjustmentImage : FileAdjustment {
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

    public double? TimeOffsetMs { get; set; } // only relevant for video files. Specifies the timestamp in milliseconds from which to extract the thumbnail image.
    public double? TimeOffsetPercentage { get; set; } // only relevant for video files. Specifies the timestamp in percentage from which to extract the thumbnail image.

    protected override string GenerateStringKey() {
        Span<byte> buf = stackalloc byte[112];
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
        BitConverter.TryWriteBytes(buf[p..], Quality ?? int.MinValue); p += 4;
        BitConverter.TryWriteBytes(buf[p..], TimeOffsetMs ?? double.NaN); p += 8;
        BitConverter.TryWriteBytes(buf[p..], TimeOffsetPercentage ?? double.NaN);
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
        if (Sharpness.HasValue) Sharpness = Math.Clamp(Sharpness.Value, -100, 100);
        if (TimeOffsetMs.HasValue) TimeOffsetMs = TimeOffsetMs < 0 ? null : TimeOffsetMs.Value;
        if (TimeOffsetPercentage.HasValue) TimeOffsetPercentage = Math.Clamp(TimeOffsetPercentage.Value, 0, 100);
    }
    const int CURRENT_VERSION = 1;
    const int FIXED_SIZE = 116; // 1+4+4+4+4+8+4+4+4+4+8+8+8+8+8+8+4+4+8+8+1
    public override byte[] ToBytes() {
        var bgBytes = BackgroundColor != null ? System.Text.Encoding.UTF8.GetBytes(BackgroundColor) : [];
        var buf = new byte[FIXED_SIZE + 2 + bgBytes.Length];
        var s = buf.AsSpan();
        int p = 0;
        s[p++] = (byte)FileAdjustmentType.Image;
        BinaryPrimitives.WriteInt32LittleEndian(s[p..], CURRENT_VERSION); p += 4;
        BinaryPrimitives.WriteInt32LittleEndian(s[p..], (int)RequestedFormat); p += 4;
        BinaryPrimitives.WriteInt32LittleEndian(s[p..], Width ?? int.MinValue); p += 4;
        BinaryPrimitives.WriteInt32LittleEndian(s[p..], Height ?? int.MinValue); p += 4;
        BinaryPrimitives.WriteDoubleLittleEndian(s[p..], Zoom ?? double.NaN); p += 8;
        BinaryPrimitives.WriteInt32LittleEndian(s[p..], FocusX ?? int.MinValue); p += 4;
        BinaryPrimitives.WriteInt32LittleEndian(s[p..], FocusY ?? int.MinValue); p += 4;
        BinaryPrimitives.WriteInt32LittleEndian(s[p..], OffsetX ?? int.MinValue); p += 4;
        BinaryPrimitives.WriteInt32LittleEndian(s[p..], OffsetY ?? int.MinValue); p += 4;
        BinaryPrimitives.WriteDoubleLittleEndian(s[p..], Rotation ?? double.NaN); p += 8;
        BinaryPrimitives.WriteDoubleLittleEndian(s[p..], Brightness ?? double.NaN); p += 8;
        BinaryPrimitives.WriteDoubleLittleEndian(s[p..], Contrast ?? double.NaN); p += 8;
        BinaryPrimitives.WriteDoubleLittleEndian(s[p..], Saturation ?? double.NaN); p += 8;
        BinaryPrimitives.WriteDoubleLittleEndian(s[p..], HueShift ?? double.NaN); p += 8;
        BinaryPrimitives.WriteDoubleLittleEndian(s[p..], Sharpness ?? double.NaN); p += 8;
        BinaryPrimitives.WriteInt32LittleEndian(s[p..], (int)(CropMode ?? (ImageCropMode)(-1))); p += 4;
        BinaryPrimitives.WriteInt32LittleEndian(s[p..], Quality ?? int.MinValue); p += 4;
        BinaryPrimitives.WriteDoubleLittleEndian(s[p..], TimeOffsetMs ?? double.NaN); p += 8;
        BinaryPrimitives.WriteDoubleLittleEndian(s[p..], TimeOffsetPercentage ?? double.NaN); p += 8;
        s[p++] = AutoBackgroundColor.HasValue ? (byte)(AutoBackgroundColor.Value ? 2 : 1) : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(s[p..], (ushort)bgBytes.Length); p += 2;
        bgBytes.CopyTo(s[p..]);
        return buf;
    }
    static public new FileAdjustmentImage FromBytes(byte[] bytes) {
        var s = bytes.AsSpan();
        int p = 1; // skip type byte
        var version = BinaryPrimitives.ReadInt32LittleEndian(s[p..]); p += 4;
        if (version != CURRENT_VERSION) throw new NotSupportedException($"Unsupported FileAdjustmentImage version: {version}");
        int ri; double rd;
        var obj = new FileAdjustmentImage {
            RequestedFormat = (FileFormat)BinaryPrimitives.ReadInt32LittleEndian(s[p..])
        }; p += 4;
        obj.Width = (ri = BinaryPrimitives.ReadInt32LittleEndian(s[p..])) == int.MinValue ? null : ri; p += 4;
        obj.Height = (ri = BinaryPrimitives.ReadInt32LittleEndian(s[p..])) == int.MinValue ? null : ri; p += 4;
        obj.Zoom = double.IsNaN(rd = BinaryPrimitives.ReadDoubleLittleEndian(s[p..])) ? null : rd; p += 8;
        obj.FocusX = (ri = BinaryPrimitives.ReadInt32LittleEndian(s[p..])) == int.MinValue ? null : ri; p += 4;
        obj.FocusY = (ri = BinaryPrimitives.ReadInt32LittleEndian(s[p..])) == int.MinValue ? null : ri; p += 4;
        obj.OffsetX = (ri = BinaryPrimitives.ReadInt32LittleEndian(s[p..])) == int.MinValue ? null : ri; p += 4;
        obj.OffsetY = (ri = BinaryPrimitives.ReadInt32LittleEndian(s[p..])) == int.MinValue ? null : ri; p += 4;
        obj.Rotation = double.IsNaN(rd = BinaryPrimitives.ReadDoubleLittleEndian(s[p..])) ? null : rd; p += 8;
        obj.Brightness = double.IsNaN(rd = BinaryPrimitives.ReadDoubleLittleEndian(s[p..])) ? null : rd; p += 8;
        obj.Contrast = double.IsNaN(rd = BinaryPrimitives.ReadDoubleLittleEndian(s[p..])) ? null : rd; p += 8;
        obj.Saturation = double.IsNaN(rd = BinaryPrimitives.ReadDoubleLittleEndian(s[p..])) ? null : rd; p += 8;
        obj.HueShift = double.IsNaN(rd = BinaryPrimitives.ReadDoubleLittleEndian(s[p..])) ? null : rd; p += 8;
        obj.Sharpness = double.IsNaN(rd = BinaryPrimitives.ReadDoubleLittleEndian(s[p..])) ? null : rd; p += 8;
        obj.CropMode = (ri = BinaryPrimitives.ReadInt32LittleEndian(s[p..])) == -1 ? null : (ImageCropMode)ri; p += 4;
        obj.Quality = (ri = BinaryPrimitives.ReadInt32LittleEndian(s[p..])) == int.MinValue ? null : ri; p += 4;
        obj.TimeOffsetMs = double.IsNaN(rd = BinaryPrimitives.ReadDoubleLittleEndian(s[p..])) ? null : rd; p += 8;
        obj.TimeOffsetPercentage = double.IsNaN(rd = BinaryPrimitives.ReadDoubleLittleEndian(s[p..])) ? null : rd; p += 8;
        var abc = s[p++]; obj.AutoBackgroundColor = abc == 0 ? null : abc == 2;
        var bgLen = BinaryPrimitives.ReadUInt16LittleEndian(s[p..]); p += 2;
        obj.BackgroundColor = bgLen == 0 ? null : System.Text.Encoding.UTF8.GetString(s.Slice(p, bgLen));
        return obj;
    }
}
public class FileAdjustmentVideo : FileAdjustment {
    public int? Width { get; set; } // canvas width
    public int? Height { get; set; } // canvas height
    public double TargetBitRateInMbps { get; set; } // in bits per second
    public bool CropNotZoom { get; set; } = false; // If true, the video will be cropped to fit the target aspect ratio instead of being zoomed.
    public override void BasicSanitization() {
        base.BasicSanitization();
        if (Width.HasValue) Width = Width <= 0 ? null : Math.Clamp(Width.Value, 1, 10_000);
        if (Height.HasValue) Height = Height <= 0 ? null : Math.Clamp(Height.Value, 1, 10_000);
        TargetBitRateInMbps = Math.Clamp(TargetBitRateInMbps, 0.01, 100); // 
    }
    public override FileAdjustmentType GetAdjustmentType() => FileAdjustmentType.Video;
    const int CURRENT_VERSION = 1;
    protected override string GenerateStringKey() {
        Span<byte> buf = stackalloc byte[20];
        int p = 0;
        BitConverter.TryWriteBytes(buf[p..], (int)RequestedFormat); p += 4;
        BitConverter.TryWriteBytes(buf[p..], Width ?? int.MinValue); p += 4;
        BitConverter.TryWriteBytes(buf[p..], Height ?? int.MinValue); p += 4;
        BitConverter.TryWriteBytes(buf[p..], TargetBitRateInMbps); p += 8;
        var key = Convert.ToHexString(buf);
        if (CropNotZoom) key += "CropNotZoom";
        return key;
    }

    public override byte[] ToBytes() {
        var buf = new byte[26]; // 1+4+4+4+4+8+1
        var s = buf.AsSpan();
        int p = 0;
        s[p++] = (byte)FileAdjustmentType.Video;
        BinaryPrimitives.WriteInt32LittleEndian(s[p..], CURRENT_VERSION); p += 4;
        BinaryPrimitives.WriteInt32LittleEndian(s[p..], (int)RequestedFormat); p += 4;
        BinaryPrimitives.WriteInt32LittleEndian(s[p..], Width ?? int.MinValue); p += 4;
        BinaryPrimitives.WriteInt32LittleEndian(s[p..], Height ?? int.MinValue); p += 4;
        BinaryPrimitives.WriteDoubleLittleEndian(s[p..], TargetBitRateInMbps); p += 8;
        s[p] = CropNotZoom ? (byte)1 : (byte)0;
        return buf;
    }
    static public new FileAdjustmentVideo FromBytes(byte[] bytes) {
        var s = bytes.AsSpan();
        int p = 1; // skip type byte
        var version = BinaryPrimitives.ReadInt32LittleEndian(s[p..]); p += 4;
        if (version != CURRENT_VERSION) throw new NotSupportedException($"Unsupported FileAdjustmentVideo version: {version}");
        int ri;
        var obj = new FileAdjustmentVideo {
            RequestedFormat = (FileFormat)BinaryPrimitives.ReadInt32LittleEndian(s[p..])
        }; p += 4;
        obj.Width = (ri = BinaryPrimitives.ReadInt32LittleEndian(s[p..])) == int.MinValue ? null : ri; p += 4;
        obj.Height = (ri = BinaryPrimitives.ReadInt32LittleEndian(s[p..])) == int.MinValue ? null : ri; p += 4;
        obj.TargetBitRateInMbps = BinaryPrimitives.ReadDoubleLittleEndian(s[p..]); p += 8;
        obj.CropNotZoom = s[p] != 0;
        return obj;
    }

}