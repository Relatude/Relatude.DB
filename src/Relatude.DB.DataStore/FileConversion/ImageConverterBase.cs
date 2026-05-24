using Relatude.DB.Common;
namespace Relatude.DB.FileConversion;
public abstract class ImageConverterBase : IFileConverter {
    FileFormat[] _ins;
    FileFormat[] _outs;
    Func<int, int, IImage> _create;
    Func<Stream, IImage> _load;
    public ImageConverterBase(FileFormat[] ins, FileFormat[] outs, Func<int, int, IImage> create, Func<Stream, IImage> load) {
        _ins = ins;
        _outs = outs;
        _create = create;
        _load = load;
        MaxConcurrentWork = Math.Max(1, Environment.ProcessorCount / 2);
        MinIntervalBetweenCallsInMs = 0;
    }
    public int MaxConcurrentWork { get; set; }
    public int MinIntervalBetweenCallsInMs { get; set; }
    public bool SupportsConversion(FileType inBase, FileFormat inDetailed, FileType outBase, FileFormat outDetailed) {
        return _ins.Contains(inDetailed) && _outs.Contains(outDetailed);
    }
    public Task<bool> CancelAsync(Guid key) {
        return Task.FromResult(false);
    }
    public async Task<ConversionProgress> DoConvertWork(InputFileSource source, FileConversionInfo info) {
        var imgAdj = (FileAdjustmentImage)info.IdWithAdjustment.Adjustment;
        var input = await source.OpenInputStream();
        var image = _load(input).Adjust(imgAdj);
        var bytes = image.Encode(info.Formats.To);
        var stream = new MemoryStream(bytes);
        return new ConversionProgress(new FileConversionProgressInfo(FileConversionStatus.Ready, 100), stream);
    }
    public Stream GetStatus(FileValue fileValue, FileAdjustmentBase adj, FileConversionProgressInfo status) {
        if (adj is not FileAdjustmentImage imgAdj) throw new ArgumentException("Expected FileIdWithAdjustment", nameof(adj));
        int width = imgAdj.Width ?? 200;
        int height = imgAdj.Height ?? 150;
        var img = _create(width, height).GetStatusImage(fileValue, adj, status);
        var bytes = img.Encode(FileFormat.Png);
        return new MemoryStream(bytes);
    }

}
