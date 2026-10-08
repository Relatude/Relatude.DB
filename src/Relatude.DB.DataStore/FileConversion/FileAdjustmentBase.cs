using System.Buffers.Binary;
using Relatude.DB.Common;

namespace Relatude.DB.FileConversion;

public class FileIdWithAdjustment {
    public FileIdWithAdjustment(Guid fileId, FileAdjustmentBase adj, PropertyPath propertyPath, FileFormat? sharedSourceFormat = null) {
        FileId = fileId;
        Adjustment = adj;
        PropertyPath = propertyPath;
        SharedSourceFormat = sharedSourceFormat;
    }
    /// <summary>The conversion of a file value, keyed as <see cref="KeyOf"/> keys it.</summary>
    public static FileIdWithAdjustment Of(FileValue file, FileAdjustmentBase adj, PropertyPath propertyPath)
        => new(file.FileId, adj, propertyPath, sharedSourceFormatOf(file));
    public Guid FileId { get; }
    public FileAdjustmentBase Adjustment { get; }
    public PropertyPath PropertyPath { get; }
    /// <summary>Only for a file whose bytes other file values may share: the format its own name gives
    /// them, which is part of the key.</summary>
    public FileFormat? SharedSourceFormat { get; }
    Guid? _key = null;
    public Guid GetKey() => _key ??= keyOf(FileId, SharedSourceFormat, Adjustment);
    /// <summary>
    /// The key a conversion of the file is cached, run and reported under: the file id and the
    /// adjustment. Values sharing one copy of their bytes (<see cref="FileValue.IsKeptByHash"/>) share
    /// their conversions too, so the format the value's name gives the bytes is added - the same bytes
    /// named as another format convert differently or not at all, and a failure is cached under the
    /// key. Every other file keeps the key it always had, so no cached conversion is lost.
    /// </summary>
    public static Guid KeyOf(FileValue file, FileAdjustmentBase adj) => keyOf(file.FileId, sharedSourceFormatOf(file), adj);
    static FileFormat? sharedSourceFormatOf(FileValue file) => FileValue.IsKeptByHash(file) ? file.Format : null;
    static Guid keyOf(Guid fileId, FileFormat? sharedSourceFormat, FileAdjustmentBase adj) {
        var key = fileId.CombineHashGuid(adj.GetKey());
        return sharedSourceFormat is { } format ? key.CombineHashGuid(("source format " + format).GenerateHashGuid()) : key;
    }
}
public enum FileAdjustmentType { // the first byte of ToBytes: fixed numbers, like FileFormat
    Image = 0,
    Video = 1,
    Meta = 2,
}
public abstract class FileAdjustmentBase {
    public FileFormat RequestedFormat { get; set; }
    /// <summary>A small result is kept in memory only, never written to the converted file cache. Part of
    /// the key, so a temporary request never decides whether a persistent one of the same picture is kept.</summary>
    public bool Temporary { get; set; } = false;
    public abstract FileAdjustmentType GetAdjustmentType();
    /// <summary>
    /// The key of the adjustment as it is now. Computed on every call rather than kept: the properties
    /// are settable, and a key kept from before a change would name another picture (an adjustment
    /// reused for a list of sizes would hand out the URL of the first size for all of them).
    /// </summary>
    public Guid GetKey() => GenerateStringKey().GenerateHashGuid();
    public virtual void BasicSanitization() {
        if (RequestedFormat == FileFormat.Unknown) RequestedFormat = FileFormat.Png;
    }
    /// <summary>
    /// A sanitized copy: values out of range clamped or dropped, and equivalent ways of asking for the
    /// same picture written one way. Conversions are keyed by this form whichever way the adjustment
    /// arrived (an encoded URL token, a readable URL, the admin UI, application code), so one picture
    /// is converted and cached once. This instance is never changed.
    /// </summary>
    public FileAdjustmentBase Normalized() {
        var copy = FromBytes(ToBytes()); // the round trip also turns NaN and other "not set" markers into null
        copy.BasicSanitization();
        return copy;
    }
    protected abstract string GenerateStringKey();
    public abstract byte[] ToBytes();
    public static FileAdjustmentBase FromBytes(byte[] bytes) {
        var adjustmentType = (FileAdjustmentType)bytes[0];
        return adjustmentType switch {
            FileAdjustmentType.Image => FileAdjustmentImage.FromBytes(bytes),
            FileAdjustmentType.Video => FileAdjustmentVideo.FromBytes(bytes),
            FileAdjustmentType.Meta => FileAdjustmentMeta.FromBytes(bytes),
            _ => throw new NotSupportedException($"Unsupported adjustment type: {adjustmentType}")
        };
    }
}
public class FileAdjustmentMeta : FileAdjustmentBase {
    public FileAdjustmentMeta() {
        RequestedFormat = FileFormat.FileMetaJson;
        Temporary = true;
    }
    public override FileAdjustmentType GetAdjustmentType() => FileAdjustmentType.Meta;
    public override byte[] ToBytes() {
        var buf = new byte[6];
        var s = buf.AsSpan();
        s[0] = (byte)FileAdjustmentType.Meta;
        BinaryPrimitives.WriteInt32LittleEndian(s[1..], (int)RequestedFormat);
        s[5] = Temporary ? (byte)1 : (byte)0;
        return buf;
    }
    public static new FileAdjustmentMeta FromBytes(byte[] bytes) {
        if (bytes.Length < 5) throw new ArgumentException("Invalid byte array length for FileAdjustmentMeta");
        var obj = new FileAdjustmentMeta { RequestedFormat = (FileFormat)BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan()[1..]) };
        if (bytes.Length >= 6) obj.Temporary = bytes[5] != 0;
        return obj;
    }
    // (this used to be Span<byte>.ToString(), which is the type name, so every meta key was the same)
    protected override string GenerateStringKey() => "Meta" + (int)RequestedFormat + (Temporary ? "Tmp" : string.Empty);
}
public class FileAdjustmentImage : FileAdjustmentBase {
    public FileAdjustmentImage() {
        RequestedFormat = FileFormat.Image; // adaptive: resolved against the original file and the store defaults, see ResolveAdaptiveFormat
    }
    public override FileAdjustmentType GetAdjustmentType() => FileAdjustmentType.Image;
    public int? Width { get; set; } // canvas width
    public int? Height { get; set; } // canvas height
    // The rectangle of the original to work from, in its own pixels; everything else applies to it.
    // A conversion that names its rectangle costs one resample whatever the magnification.
    public int? SourceX { get; set; }
    public int? SourceY { get; set; }
    public int? SourceWidth { get; set; }
    public int? SourceHeight { get; set; }
    public double? Zoom { get; set; } = null; // in percentage, 100 or below meaning the whole picture; above it, the window about the focus point that fills the canvas
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
    public bool? InvertLuminance { get; set; } = null; // Shifts the hue 180 degrees and inverts all colors. Light becomes dark and dark becomes light while hues are preserved, so artwork made for one background reads correctly on the opposite one.
    public AutoLightDarkSwitch? AutoLightDarkMode { get; set; } = null; // Analyzes the image and applies InvertLuminance only if it is likely to improve the result. Photographs are never inverted.
    public string? BackgroundColor { get; set; } = null; // Hex color code (e.g. "#RRGGBB" or "#RRGGBBAA"). Only used for certain crop modes when the output canvas is larger than the resized image.
    public bool? AutoBackgroundColor { get; set; } = null; // Automatically determine the background color based edge analysis of the image.
    public ImageCropMode? CropMode { get; set; }
    public int? Quality { get; set; } // 0-100, where 100 is the best quality. Only applicable for lossy formats like JPEG.

    public double? TimeOffsetMs { get; set; } // only relevant for video files. Specifies the timestamp in milliseconds from which to extract the thumbnail image.
    public double? TimeOffsetPercentage { get; set; } // only relevant for video files. Specifies the timestamp in percentage from which to extract the thumbnail image.

    /// <summary>
    /// True when nothing is requested beyond the adaptive format: no dimensions, no quality, no
    /// edits. The store then serves the original file untouched instead of converting.
    /// </summary>
    public bool IsPlainRequest() => RequestedFormat == FileFormat.Image && Width == null && Height == null && !hasAdjustmentsBesideDimensions();

    /// <summary>
    /// Resolves the adaptive <see cref="FileFormat.Image"/> against the original file and the store
    /// defaults, returning a resolved copy (this instance is never changed - its conversion cache
    /// key may already be handed out). A gif stays a gif, preserving animations, when it keeps its
    /// original dimensions and has no other adjustments, or when the default format cannot animate
    /// (anything but webp); everything else becomes the default format, with the default quality
    /// when none is given. Returns this instance unchanged when the requested format is already concrete.
    /// </summary>
    public FileAdjustmentImage ResolveAdaptiveFormat(FileFormat originalFormat, int originalWidth, int originalHeight, FileFormat defaultFormat, int defaultQuality) {
        if (RequestedFormat != FileFormat.Image) return this;
        var resolved = FromBytes(ToBytes());
        if (originalFormat == FileFormat.Gif && (defaultFormat != FileFormat.Webp || KeepsOriginalGif(originalFormat, originalWidth, originalHeight))) {
            resolved.RequestedFormat = FileFormat.Gif;
        } else {
            resolved.RequestedFormat = defaultFormat;
            resolved.Quality ??= defaultQuality;
        }
        return resolved;
    }
    /// <summary>An adaptive request for a gif at its own size with nothing else changed: the original file is the answer.</summary>
    public bool KeepsOriginalGif(FileFormat originalFormat, int originalWidth, int originalHeight) =>
        RequestedFormat == FileFormat.Image && originalFormat == FileFormat.Gif && !hasAdjustmentsBesideDimensions() && dimensionsSameOrAbsent(originalWidth, originalHeight);
    bool hasAdjustmentsBesideDimensions() =>
        Zoom != null || FocusX != null || FocusY != null || OffsetX != null || OffsetY != null
        || Rotation != null || Brightness != null || Contrast != null || Saturation != null
        || HueShift != null || Sharpness != null || InvertLuminance != null || AutoLightDarkMode != null
        || BackgroundColor != null || AutoBackgroundColor != null || CropMode != null || Quality != null
        || TimeOffsetMs != null || TimeOffsetPercentage != null
        || SourceX != null || SourceY != null || SourceWidth != null || SourceHeight != null;
    bool dimensionsSameOrAbsent(int originalWidth, int originalHeight) =>
        (Width == null || (originalWidth > 0 && Width == originalWidth))
        && (Height == null || (originalHeight > 0 && Height == originalHeight));

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
        BitConverter.TryWriteBytes(buf[p..], TimeOffsetPercentage ?? double.NaN); p += 8;
        BitConverter.TryWriteBytes(buf[p..], (int)(AutoLightDarkMode ?? (AutoLightDarkSwitch)(-1)));
        var key = Convert.ToHexString(buf);
        // appended only when asked for, so every key handed out before the rectangle existed still holds
        if (SourceWidth.HasValue || SourceHeight.HasValue) key += "S" + SourceX + "," + SourceY + "," + SourceWidth + "," + SourceHeight;
        if (AutoBackgroundColor.HasValue) key += AutoBackgroundColor.Value.ToString();
        if (InvertLuminance.HasValue) key += "Inv" + InvertLuminance.Value.ToString();
        if (Temporary) key += "Tmp"; // before the free text color; a persistent key stays what it always was
        if (BackgroundColor != null) key += BackgroundColor;
        return key;
    }
    public override void BasicSanitization() {
        if (RequestedFormat == FileFormat.Unknown) RequestedFormat = FileFormat.Image; // for images, unknown means adaptive
        base.BasicSanitization();
        if (Width.HasValue) Width = Width <= 0 ? null : Math.Clamp(Width.Value, 1, 10_000);
        if (Height.HasValue) Height = Height <= 0 ? null : Math.Clamp(Height.Value, 1, 10_000);
        if (Zoom.HasValue) Zoom = Zoom <= 100 ? null : Math.Min(Zoom.Value, 1_000_000);
        if (SourceX.HasValue) SourceX = Math.Max(0, SourceX.Value);
        if (SourceY.HasValue) SourceY = Math.Max(0, SourceY.Value);
        if (SourceWidth.HasValue) SourceWidth = SourceWidth <= 0 ? null : SourceWidth;
        if (SourceHeight.HasValue) SourceHeight = SourceHeight <= 0 ? null : SourceHeight;
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
        if (CropMode.HasValue && !Enum.IsDefined(CropMode.Value)) CropMode = null;
        if (AutoLightDarkMode.HasValue && !Enum.IsDefined(AutoLightDarkMode.Value)) AutoLightDarkMode = null;
        BackgroundColor = canonicalColor(BackgroundColor);
    }
    // a hex color written the way the readable URL formats write it ("#aabbcc"), so "AABBCC",
    // "#AABBCC" and "#aabbcc" are one picture; anything else is left to the color parser as it is
    static string? canonicalColor(string? color) {
        if (color == null) return null;
        var hex = color.Trim().TrimStart('#');
        if (hex.Length == 0) return null;
        return hex.All(char.IsAsciiHexDigit) ? "#" + hex.ToLowerInvariant() : color.Trim();
    }
    const int CURRENT_VERSION = 4;
    const int FIXED_SIZE = 136; // as below, plus the four source rectangle ints
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
        s[p++] = Temporary ? (byte)1 : (byte)0;
        s[p++] = InvertLuminance.HasValue ? (byte)(InvertLuminance.Value ? 2 : 1) : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(s[p..], (int)(AutoLightDarkMode ?? (AutoLightDarkSwitch)(-1))); p += 4;
        BinaryPrimitives.WriteInt32LittleEndian(s[p..], SourceX ?? int.MinValue); p += 4;
        BinaryPrimitives.WriteInt32LittleEndian(s[p..], SourceY ?? int.MinValue); p += 4;
        BinaryPrimitives.WriteInt32LittleEndian(s[p..], SourceWidth ?? int.MinValue); p += 4;
        BinaryPrimitives.WriteInt32LittleEndian(s[p..], SourceHeight ?? int.MinValue); p += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(s[p..], (ushort)bgBytes.Length); p += 2;
        bgBytes.CopyTo(s[p..]);
        return buf;
    }
    static public new FileAdjustmentImage FromBytes(byte[] bytes) {
        var s = bytes.AsSpan();
        int p = 1; // skip type byte
        var version = BinaryPrimitives.ReadInt32LittleEndian(s[p..]); p += 4;
        if (version < 1 || version > CURRENT_VERSION) throw new NotSupportedException($"Unsupported FileAdjustmentImage version: {version}");
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
        if (version >= 2) obj.Temporary = s[p++] != 0;
        if (version >= 3) {
            var inv = s[p++]; obj.InvertLuminance = inv == 0 ? null : inv == 2;
            obj.AutoLightDarkMode = (ri = BinaryPrimitives.ReadInt32LittleEndian(s[p..])) == -1 ? null : (AutoLightDarkSwitch)ri; p += 4;
        }
        if (version >= 4) {
            obj.SourceX = (ri = BinaryPrimitives.ReadInt32LittleEndian(s[p..])) == int.MinValue ? null : ri; p += 4;
            obj.SourceY = (ri = BinaryPrimitives.ReadInt32LittleEndian(s[p..])) == int.MinValue ? null : ri; p += 4;
            obj.SourceWidth = (ri = BinaryPrimitives.ReadInt32LittleEndian(s[p..])) == int.MinValue ? null : ri; p += 4;
            obj.SourceHeight = (ri = BinaryPrimitives.ReadInt32LittleEndian(s[p..])) == int.MinValue ? null : ri; p += 4;
        }
        var bgLen = BinaryPrimitives.ReadUInt16LittleEndian(s[p..]); p += 2;
        obj.BackgroundColor = bgLen == 0 ? null : System.Text.Encoding.UTF8.GetString(s.Slice(p, bgLen));
        return obj;
    }
}
public class FileAdjustmentVideo : FileAdjustmentBase {
    public int? Width { get; set; } // canvas width
    public int? Height { get; set; } // canvas height
    public double TargetBitRateInMbps { get; set; } // in bits per second
    public bool CropNotZoom { get; set; } = false; // If true, the video will be cropped to fit the target aspect ratio instead of being zoomed.
    public override void BasicSanitization() {
        base.BasicSanitization();
        if (Width.HasValue) Width = Width <= 0 ? null : Math.Clamp(Width.Value, 1, 10_000);
        if (Height.HasValue) Height = Height <= 0 ? null : Math.Clamp(Height.Value, 1, 10_000);
        // 0 (or less) is "not set": the converter picks the bit rate. Clamping it to the minimum asked for 10 kbps.
        TargetBitRateInMbps = TargetBitRateInMbps > 0 ? Math.Clamp(TargetBitRateInMbps, 0.01, 100) : 0;
    }
    public override FileAdjustmentType GetAdjustmentType() => FileAdjustmentType.Video;
    const int CURRENT_VERSION = 2;
    protected override string GenerateStringKey() {
        Span<byte> buf = stackalloc byte[20];
        int p = 0;
        BitConverter.TryWriteBytes(buf[p..], (int)RequestedFormat); p += 4;
        BitConverter.TryWriteBytes(buf[p..], Width ?? int.MinValue); p += 4;
        BitConverter.TryWriteBytes(buf[p..], Height ?? int.MinValue); p += 4;
        BitConverter.TryWriteBytes(buf[p..], TargetBitRateInMbps); p += 8;
        var key = Convert.ToHexString(buf);
        if (CropNotZoom) key += "CropNotZoom";
        if (Temporary) key += "Tmp";
        return key;
    }

    public override byte[] ToBytes() {
        var buf = new byte[27]; // 1+4+4+4+4+8+1+1
        var s = buf.AsSpan();
        int p = 0;
        s[p++] = (byte)FileAdjustmentType.Video;
        BinaryPrimitives.WriteInt32LittleEndian(s[p..], CURRENT_VERSION); p += 4;
        BinaryPrimitives.WriteInt32LittleEndian(s[p..], (int)RequestedFormat); p += 4;
        BinaryPrimitives.WriteInt32LittleEndian(s[p..], Width ?? int.MinValue); p += 4;
        BinaryPrimitives.WriteInt32LittleEndian(s[p..], Height ?? int.MinValue); p += 4;
        BinaryPrimitives.WriteDoubleLittleEndian(s[p..], TargetBitRateInMbps); p += 8;
        s[p++] = CropNotZoom ? (byte)1 : (byte)0;
        s[p] = Temporary ? (byte)1 : (byte)0;
        return buf;
    }
    static public new FileAdjustmentVideo FromBytes(byte[] bytes) {
        var s = bytes.AsSpan();
        int p = 1; // skip type byte
        var version = BinaryPrimitives.ReadInt32LittleEndian(s[p..]); p += 4;
        if (version < 1 || version > CURRENT_VERSION) throw new NotSupportedException($"Unsupported FileAdjustmentVideo version: {version}");
        int ri;
        var obj = new FileAdjustmentVideo {
            RequestedFormat = (FileFormat)BinaryPrimitives.ReadInt32LittleEndian(s[p..])
        }; p += 4;
        obj.Width = (ri = BinaryPrimitives.ReadInt32LittleEndian(s[p..])) == int.MinValue ? null : ri; p += 4;
        obj.Height = (ri = BinaryPrimitives.ReadInt32LittleEndian(s[p..])) == int.MinValue ? null : ri; p += 4;
        obj.TargetBitRateInMbps = BinaryPrimitives.ReadDoubleLittleEndian(s[p..]); p += 8;
        obj.CropNotZoom = s[p++] != 0;
        if (version >= 2) obj.Temporary = s[p] != 0;
        return obj;
    }

}