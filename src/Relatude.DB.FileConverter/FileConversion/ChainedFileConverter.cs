using Relatude.DB.Common;
namespace Relatude.DB.FileConverter;

public class ChainedFileConverter : IFileConverter {
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
    public Task<Stream> ConvertAsync(Stream input, FileConversionInfo info) {
        throw new NotImplementedException();
    }

    public Task<bool> CancelAsync(string key) {
        throw new NotImplementedException();
    }

    public Stream GetProgressStream(FileValue fileValue, FileAdjustmentBase adj, FileConversionProgressInfo status) {
        throw new NotImplementedException();
    }
}