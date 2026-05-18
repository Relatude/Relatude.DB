using Relatude.DB.Common;
namespace Relatude.DB.FileConverter;

public class DefaultImageMetaConverter : IFileConverter {
    public bool SupportsConversion(FileType inBase, FileFormat inDetailed, FileType outBase, FileFormat outDetailed) {
        if (outBase != FileType.Image || outDetailed != FileFormat.Txt) {
            return false;
        }
        if (inBase == FileType.Image) {
            return inDetailed switch {
                FileFormat.Jpeg => true,
                FileFormat.Png => true,
                FileFormat.Gif => true,
                FileFormat.Bmp => true,
                FileFormat.Svg => true,
                FileFormat.Webp => true,
                _ => false
            };
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

    public Stream GetProgressStream(FileValue fileValue, FileAdjustmentBase adj, FileConversionProgressInfo status) {
        throw new NotImplementedException();
    }
}