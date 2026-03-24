using Relatude.DB.Common;

namespace Relatude.DB.FileConverter;

public class FileTypeInfo {
}
public enum FileInputType {
    Image,
    Text,
    Audio,
}
public class FileMetaBase {
    public FileValueType FileValueType { get; set; }
}
public abstract class FileAdjustmentBase {
    public FileValueType FileValueType { get; set; }
}
public class FileAdjustmentImage : FileAdjustmentBase {
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? FocusX { get; set; }
    public int? FocusY { get; set; }
    public double? Scale { get; set; } = null;
    public string? Background { get; set; } = null;
    public ImageCropMode CropMode { get; set; }
    public FileImageFormat? Format { get; set; }
    public int? Quality{ get; set; }
    //public string? JsonParams { get; set; } = null;
}
public class FileConversionProgressInfo {
    public FileConversionStatus Status { get; set; }
    public int ProgressPercentage { get; set; } = 0;
    public int RemainingSeconds { get; set; } = 0;
    public string? Message { get; set; }
}
public enum ImageCropMode{ 
    Fill,
    Fit,
    Auto,
    None,
}
public enum FileConversionStatus {
    Unknown,
    Queued,
    InProgress,
    Done,
    Error,
}
public class FileConversionResult {
    public Guid Id { get; set; }
    public FileConversionProgressInfo ProgressInfo { get; set; }
    public Stream? Output { get; set; }
}
public class ImageMeta{
    public string? Photographer { get; set; }
    public string? Copyright { get; set; }
    public double? GPSLatitude { get; set; }
    public double? GPSLongitude { get; set; }
    public string? AutoDescription { get; set; }
    public string? AutoKeywords { get; set; }
}
public enum FileImageFormat {
    Jpeg,
    Png,
    Webp,
    Gif,
    Bmp,
}
public enum FileOutputType {
}
public interface Converter { // just the convertion
    Task<ImageMeta?> AnalyzeImageAsync(Stream input, FileValueType inputType);
    Task<FileConversionResult?> ConvertAsync(Stream input, FileValueType inputType, FileAdjustmentBase adjustments);
    Task<FileConversionProgressInfo> GetStatusAsync(Guid id);
    Task<Stream> GetStreamAsync(Stream input, string inputHash, FileValueType inputType, FileAdjustmentBase adjustments);
}
public interface ConvertServer{ // includes storage and cached previous results
}
