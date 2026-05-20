using Relatude.DB.Common;
using Relatude.DB.FileConversion;
namespace Relatude.DB.FileConversion;

public class NativeImageConverter : IFileConverter {
    public NativeImageConverter() {
        MaxConcurrentWork = Environment.ProcessorCount / 2;
        MinIntervalBetweenCallsInMs = 0;
    }
    public int MaxConcurrentWork { get; set; }
    public int MinIntervalBetweenCallsInMs { get; set; }
    int _concurrentCount = 0;
    public bool IsTooBusy(FileConversionInfo info) => Volatile.Read(ref _concurrentCount) >= MaxConcurrentWork;
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
    public Task<bool> CancelAsync(Guid key) {
        return Task.FromResult(false);
    }
    public async Task<ConversionProgress> DoConvertWork(Func<Task<Stream>> getInputStream, FileConversionInfo info) {
        try {
            Interlocked.Increment(ref _concurrentCount);
            var imgAdj = (FileAdjustmentImage)info.IdWithAdjustment.Adjustment;
            var input = await getInputStream();
            var image = NativeImage.Load(input).Adjust(imgAdj);            
            var bytes = image.Encode(info.Formats.To);
            var stream = new MemoryStream(bytes);
            return new ConversionProgress(new FileConversionProgressInfo(FileConversionStatus.Ready, 100), stream);
        } finally {
            Interlocked.Decrement(ref _concurrentCount);
        }
    }

    public Stream GetStatusRepresentation(FileValue fileValue, FileAdjustmentBase adj, FileConversionProgressInfo status) {
        if(adj is not FileAdjustmentImage imgAdj) throw new ArgumentException("Expected FileIdWithAdjustment", nameof(adj));
        int width = imgAdj.Width ?? 200;
        int height = imgAdj.Height ?? 150;
        var img = NativeImage.Create(width, height).GetStatusImage(fileValue, adj, status);
        var bytes = img.Encode(FileFormat.Png);
        return new MemoryStream(bytes);
    }
}