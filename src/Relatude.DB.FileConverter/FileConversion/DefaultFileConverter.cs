using ImageMagick;
using Relatude.DB.Common;

namespace Relatude.DB.FileConverter;

public class DefaultFileConverter : IFileConverter {
    public FormatPair[] GetSupportedConversions() {
        return [
            new (FileFormat.Png, FileFormat.Jpeg),
            new (FileFormat.Jpeg, FileFormat.Png),
            ];
    }
    public Task<bool> CancelAsync(string key) {
        throw new NotImplementedException();
    }
    public async Task<FileConversionResult> ConvertAsync(Stream input, FileConversionInfo info, int maxWaitMs) {
        var outputStream = new MemoryStream();
        using (var image = new MagickImage(input)) {
            await image.WriteAsync(outputStream, getMagickFormat(info.Formats.To));
        }
        input.Dispose();
        return new FileConversionResult(new FileConversionProgressInfo(FileConversionStatus.Ready), outputStream);
    }
    MagickFormat getMagickFormat(FileFormat format) {
        return format switch {
            FileFormat.Png => MagickFormat.Png,
            FileFormat.Jpeg => MagickFormat.Jpeg,
            _ => throw new NotSupportedException($"Unsupported file format: {format}"),
        };
    }
    public Task<FileConversionProgressInfo> GetStatusAsync(FileIdWithAdjustment fileIdWithAdjustment) {
        throw new NotImplementedException();
    }
    public Task<Stream> GetStreamAsync(FileIdWithAdjustment fileIdWithAdjustment) {
        throw new NotImplementedException();
    }
}