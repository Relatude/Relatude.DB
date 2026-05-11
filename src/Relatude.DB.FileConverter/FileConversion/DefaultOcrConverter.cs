using Relatude.DB.Common;
namespace Relatude.DB.FileConverter;

public class DefaultOcrConverter : IFileConverter {
    public bool SupportsConversion(FileType inBase, FileFormat inDetailed, FileType outBase, FileFormat outDetailed) {
        if (outBase != FileType.Image || outDetailed != FileFormat.Txt) {
            return false;
        }
        switch (inDetailed) {
            case FileFormat.Jpeg:
            case FileFormat.Png:
            case FileFormat.Gif:
            case FileFormat.Bmp:
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