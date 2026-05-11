using Relatude.DB.Common;
namespace Relatude.DB.FileConverter;

public class DefaultVideoConverter : IFileConverter {
    public bool SupportsConversion(FileType inBase, FileFormat inDetailed, FileType outBase, FileFormat outDetailed) {
        // basically only video conversions, but also thumbnail generation (video to image) is supported 
        if (inBase != FileType.Video || (outBase != FileType.Video && outBase != FileType.Image)) {
            return false;
        }
        switch (inDetailed) {
            case FileFormat.Flac:
            case FileFormat.Flv:
            case FileFormat.Mp4:
            case FileFormat.Wmv:
                break;
            default:
                return false;
        }
        switch (outDetailed) {
            case FileFormat.Mp4:
            case FileFormat.Jpeg:
            case FileFormat.Webp:
            case FileFormat.Png:
                break;
            default:
                return false;
        }
        return true;
    }
    public Task<bool> CancelAsync(string key) {
        throw new NotImplementedException();
    }

    public Task<FileConversionResult> ConvertAsync(Stream input, FileConversionInfo info, int maxWaitMs) {
        throw new NotImplementedException();
    }

    public Task<FileConversionProgressInfo> GetStatusAsync(FileIdWithAdjustment fileIdWithAdjustment) {
        throw new NotImplementedException();
    }

    public Task<Stream> GetStreamAsync(FileIdWithAdjustment fileIdWithAdjustment) {
        throw new NotImplementedException();
    }

}