using Relatude.DB.Common;

namespace Relatude.DB.FileConverter;

public class FileIdWithAdjustment(Guid fileId, FileAdjustmentBase adj) {
    public Guid FileId { get; } = fileId;
    public FileAdjustmentBase Adjustment { get; } = adj;
    public string Key { get; } = (fileId + "_" + adj.GetLongKey()).GenerateGuid();
}
public abstract class FileAdjustmentBase {
    public FileValueType FileValueType { get; set; }
    public abstract string GetLongKey();
}
public class FileAdjustmentImage : FileAdjustmentBase {
    public int? Width { get; set; } // canvas width
    public int? Height { get; set; } // canvas height
    public double? Scale { get; set; } = null; // in percentage. 100 means no change, 50 means half size, 200 means double size.
    public int? FocusX { get; set; } // in reference to the original image
    public int? FocusY { get; set; } // in reference to the original image
    public int? OffsetX { get; set; } // in reference to the original image
    public int? OffsetY { get; set; } // in reference to the original image
    public int? Brightness { get; set; }
    public int? Contrast { get; set; }
    public int? Saturation { get; set; }
    public int? Colorize { get; set; }
    public int? HueShift { get; set; }
    public string? BackgroundColor { get; set; } = null;
    public ImageCropMode? CropMode { get; set; }
    public string? AIInstructions { get; set; } // Remove background, enhance details, etc.
    public FileImageFormat? Format { get; set; }
    public int? Quality { get; set; }
    public override string GetLongKey() {
        throw new NotImplementedException();
    }
    //public string? JsonParams { get; set; } = null;
}
public class FileConversionProgressInfo(FileConversionStatus status, int progress = 0, int remaining = -1, string? message = null) {
    public FileConversionStatus Status { get; } = status;
    public int ProgressPercentage { get; } = progress;
    public int RemainingSeconds { get; } = remaining;
    public string? Message { get; } = message;
}
public enum ImageCropMode {
    Fill,
    Fit,
    Auto,
    None,
}
public enum FileConversionStatus {
    InProgress,
    Ready,
    Error,
}
public class FileConversionResult(FileConversionProgressInfo progressInfo, Stream? output = null) {
    public FileConversionProgressInfo ProgressInfo { get; } = progressInfo;
    public Stream? Output { get; } = output;
}
public enum ImageObjectType {
    Face,
    Person,
    Text,
    Animal,
    Vehicle,
    Furniture,
    Food,
    Building,
    Other,
}
public class ImageObjectInfo {
    //public string? ObjectName { get; set; }
    public string? ObjectText { get; set; }
    public double Confidence { get; set; }
    public ImageObjectType ObjectType { get; set; } = ImageObjectType.Other;
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}
public class ImageMeta {
    public string? Photographer { get; set; }
    public string? Copyright { get; set; }
    public double? GPSLatitude { get; set; }
    public double? GPSLongitude { get; set; }
    public string? AiDescription { get; set; }
    public string? AiKeywords { get; set; }
    public ImageCropMode? DefaultCropMode { get; set; }
    public int? FocusPointX { get; set; }
    public int? FocusPointY { get; set; }
    public ImageObjectInfo[]? DetectedObjects { get; set; }
    public string? GetAllText() {
        if (DetectedObjects == null) return null;
        return string.Join(Environment.NewLine, DetectedObjects.Where(o => !string.IsNullOrEmpty(o.ObjectText)).Select(o => o.ObjectText));
    }
}
public enum FileImageFormat {
    Jpeg,
    Png,
    Webp,
    Gif,
    Bmp,
}
public interface IFileConverter { // just the conversion, likely calling external services, like ai analysis or video processing
    Task<bool> CancelConversionAsync(string key);
    Task<FileConversionResult> ConvertAsyncAndDisposeStreamWhenDone(Stream input, FileIdWithAdjustment fileIdWithAdjustment, string hash, string fileName, int maxWaitMs);
    Task<ImageMeta> AnalyzeImageAsync(Stream input, FileIdWithAdjustment fileIdWithAdjustment, string hash, string fileName);
    Task<FileConversionProgressInfo> GetStatusAsync(FileIdWithAdjustment fileIdWithAdjustment);
    Task<Stream> GetStreamAsync(FileIdWithAdjustment fileIdWithAdjustment);
}
public interface IUrlProvider { // includes storage and local cache
    string GetUrl(FileIdWithAdjustment fileIdWithAdjustment);
}