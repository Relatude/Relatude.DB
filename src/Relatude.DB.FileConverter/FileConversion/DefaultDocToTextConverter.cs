using Relatude.DB.Common;
namespace Relatude.DB.FileConverter;

public class DefaultDocToTextConverter : IFileConverter {
    public bool SupportsConversion(FileType inBase, FileFormat inDetailed, FileType outBase, FileFormat outDetailed) {
        // Only support Document to Text conversion, and only for specific formats
        if (outBase != FileType.Document || outDetailed != FileFormat.Txt) {
            return false;
        }
        if (inBase == FileType.Document) {
            return inDetailed switch {
                FileFormat.Doc => true,
                FileFormat.Docx => true,
                FileFormat.Pdf => true,
                _ => false
            };
        }
        return false;
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