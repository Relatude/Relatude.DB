using Relatude.DB.Common;

namespace Relatude.DB.FileConverter;

public class FileIdWithAdjustment {
    const int _folderDepth = 2;
    public FileIdWithAdjustment(Guid fileId, FileAdjustmentBase adj) {
        FileId = fileId;
        Adjustment = adj;
        Key = (fileId + "_" + adj.GetUniqueKey()).GenerateGuidSafe().ToString("N");
        Path = new string[_folderDepth];
        for (int i = 0; i < _folderDepth - 1; i++) Path[i] = Key.Substring(i * 2, 2);
        Path[_folderDepth - 1] = Key;
    }
    public Guid FileId { get; }
    public FileAdjustmentBase Adjustment { get; }
    public string Key { get; }
    public string[] Path { get; }
}
public abstract class FileAdjustmentBase {
    public FileFormat RequestedFormat { get; set; }
    public abstract string GetUniqueKey();
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
    public override string GetUniqueKey() {
        throw new NotImplementedException();
    }
}
public class FileAdjustmentImageMetaData : FileAdjustmentBase {
    public override string GetUniqueKey() {
        return "FileAdjustmentImageMetaData";
    }
}