//using Relatude.DB.Common;
//using SixLabors.ImageSharp;
//using SixLabors.ImageSharp.Formats;
//using SixLabors.ImageSharp.Formats.Jpeg;
//using SixLabors.ImageSharp.Formats.Png;
//namespace Relatude.DB.FileConverter;

//public class DefaultFileConverter : IFileConverter {
//    public FormatPair[] GetSupportedConversions() {
//        return [
//            new (FileFormat.Png, FileFormat.Png),
//            new (FileFormat.Png, FileFormat.Jpeg),
//            new (FileFormat.Png, FileFormat.Webp),
//            new (FileFormat.Jpeg, FileFormat.Png),
//            new (FileFormat.Jpeg, FileFormat.Png),
//            new (FileFormat.Jpeg, FileFormat.Webp),
//            new (FileFormat.Webp, FileFormat.Webp),
//            new (FileFormat.Webp, FileFormat.Jpeg),
//            new (FileFormat.Webp, FileFormat.Png),
//            new (FileFormat.Bmp, FileFormat.Jpeg),
//            new (FileFormat.Bmp, FileFormat.Png),
//            new (FileFormat.Bmp, FileFormat.Webp),
//            new (FileFormat.Gif, FileFormat.Gif),
//            ];
//    }
//    public Task<bool> CancelAsync(string key) {
//        throw new NotImplementedException();
//    }
//    public async Task<FileConversionResult> ConvertAsync(Stream input, FileConversionInfo info, int maxWaitMs) {
//        try {
//            using var img = await SixLabors.ImageSharp.Image.LoadAsync(input);
//            var outputStream = new MemoryStream();
//            var cancellationToken = new CancellationToken();
//            if (info.IdWithAdjustment.Adjustment is not FileAdjustmentImage adj)
//                return new(new(FileConversionStatus.Unsupported, 0, 0, "Unsupported adjustment type"), null);
//            await img.SaveAsync(outputStream, getEncoder(adj), cancellationToken);
//            return new(new(FileConversionStatus.Ready, 100, 0, null), outputStream);
//        } finally {
//            input.Dispose();
//        }
//    }
//    IImageEncoder getEncoder(FileAdjustmentImage adjustment) {
//        return adjustment.RequestedFormat switch {
//            FileFormat.Jpeg => new JpegEncoder() { Quality = adjustment.Quality ?? 90 },
//            FileFormat.Png => new PngEncoder() { CompressionLevel = PngCompressionLevel.NoCompression },
//            _ => throw new NotSupportedException($"Unsupported format: {adjustment.RequestedFormat}")
//        };
//    }
//    public Task<FileConversionProgressInfo> GetStatusAsync(FileIdWithAdjustment fileIdWithAdjustment) {
//        throw new NotImplementedException();
//    }
//    public Task<Stream> GetStreamAsync(FileIdWithAdjustment fileIdWithAdjustment) {
//        throw new NotImplementedException();
//    }
//}