using Relatude.DB.Common;
using Relatude.DB.Web;

namespace Relatude.DB.FileConverter;

public class FileIdWithAdjustment {
    const int _folderDepth = 2;
    public FileIdWithAdjustment(Guid fileId, FileAdjustmentBase adj) {
        FileId = fileId;
        Adjustment = adj;
    }
    public Guid FileId { get; }
    public FileAdjustmentBase Adjustment { get; }
    string? _key = null;
    string[]? _path = null;
    public string GetKey() => _key ??= GuidKeyGenerator.Generate(this).ToString();
    public string[] GetFilePath() {
        if (_path == null) {
            var key = GetKey();
            var path = new string[_folderDepth];
            for (int i = 0; i < _folderDepth - 1; i++) path[i] = key.Substring(i * 2, 2);
            path[_folderDepth - 1] = key;
            _path = path;
        }
        return _path;
    }
}
public enum FileAdjustmentType {
    Image,
    ImageMetaData,
}
public abstract class FileAdjustmentBase {
    public FileFormat RequestedFormat { get; set; }
    public abstract FileAdjustmentType GetAdjustmentType();
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
    public int? Quality { get; set; }
    public FileAdjustmentImageMetaData[]? MetaData { get; set; }

    public override FileAdjustmentType GetAdjustmentType() => FileAdjustmentType.Image;
}
public class FileAdjustmentImageMetaData : FileAdjustmentBase {
    public int? Wids1th { get; set; }
    public override FileAdjustmentType GetAdjustmentType() => FileAdjustmentType.ImageMetaData;
}