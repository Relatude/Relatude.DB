using Relatude.DB.Common;
using Relatude.DB.FileConversion.Images;
using System.Collections.Concurrent;
namespace Relatude.DB.FileConverter;

public class DefaultImageConverter : IFileConverter {
    public bool SupportsConversion(FileType inBase, FileFormat inDetailed, FileType outBase, FileFormat outDetailed) {
        // basically only image conversions between the supported formats
        if (inBase != FileType.Image || outBase != FileType.Image) {
            return false;
        }
        switch (inDetailed) {
            case FileFormat.Jpeg:
            case FileFormat.Png:
            case FileFormat.Gif:
            case FileFormat.Bmp:
            case FileFormat.Webp:
                break;
            default:
                return false;
        }
        switch (outDetailed) {
            case FileFormat.Jpeg:
            case FileFormat.Png:
            case FileFormat.Gif:
            case FileFormat.Bmp:
            case FileFormat.Webp:
                break;
            default:
                return false;
        }
        return true;
    }

    public Task<bool> CancelAsync(string key) {
        throw new NotImplementedException();
    }
    public async Task<FileConversionResult> ConvertAsync(Stream input, FileConversionInfo info, int maxWaitMs) {
        try {
            var imgAdj = (FileAdjustmentImage)info.IdWithAdjustment.Adjustment;
            var stream = SkiaImageConverter.Convert(input, imgAdj);
            return new(new(FileConversionStatus.Ready), stream);
        } catch (Exception ex) {
            return new(new(FileConversionStatus.Error, 0, 0, ex.Message));
        }
    }
    public Stream GetProgressStream(FileValue fileValue, FileAdjustmentBase adj, FileConversionProgressInfo status) {
        var text = status.Status.ToString();
        var imgAdj = (FileAdjustmentImage)adj;
        var w = imgAdj.Width ?? (fileValue.Width > 0 && fileValue.Width <= 1000 ? fileValue.Width : 200);
        var h = imgAdj.Height ?? (fileValue.Height > 0 && fileValue.Height <= 1000 ? fileValue.Height : 200);
        return SkiaImageConverter.CreateMessageImage(text, w, h, imgAdj.RequestedFormat);
    }
}