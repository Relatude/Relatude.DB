using Relatude.DB.Common;
using System.Diagnostics.CodeAnalysis;
using System.Net.NetworkInformation;
namespace Relatude.DB.FileConversion;

public abstract class ImageConverterBase : IFileConverter {
    FileFormat[] _ins;
    FileFormat[] _outs;
    public readonly Func<int, int, IImage> Create;
    public readonly Func<Stream, IImage> Load;
    public ImageConverterBase(FileFormat[] ins, FileFormat[] outs, Func<int, int, IImage> create, Func<Stream, IImage> load) {
        _ins = ins;
        _outs = outs;
        Create = create;
        Load = load;
        ThreadCount = Math.Max(1, Environment.ProcessorCount / 2);
        CallDelayMs = 0;
    }
    public int ThreadCount { get; set; }
    public int CallDelayMs { get; set; }
    public bool SupportsConversion(FileType inBase, FileFormat inDetailed, FileType outBase, FileFormat outDetailed) {
        return _ins.Contains(inDetailed) && _outs.Contains(outDetailed);
    }
    public Task<bool> CancelAsync(Guid key) {
        return Task.FromResult(false);
    }
    public async Task<ConversionProgress> DoConvertWork(InputFileSource source, FileConversionInfo info) {
        var imgAdj = (FileAdjustmentImage)info.IdWithAdjustment.Adjustment;
        var input = await source.OpenInputStream();
        var image = Load(input).Adjust(imgAdj);
        var bytes = image.Encode(info.Formats.To);
        var stream = new MemoryStream(bytes);
        return new ConversionProgress(new FileConversionProgressInfo(FileConversionStatus.Ready, 100), stream);
    }
    public bool TryGetLiveStatus(Guid fileId, FileAdjustmentBase adj, [MaybeNullWhen(false)] out FileConversionProgressInfo status) {
        status = null;
        return false;
    }
    public byte[] CreateStatusResponse(FileFormat requestedFormat, int width, int height, List<string> text, string textColor, string fillColor) {
        var img = Create(width, height).GetStatusImage(text, textColor, fillColor);
        var bytes = img.Encode(requestedFormat);
        return bytes;
    }
    public void Initialize(FileConverterLibrary library) {
        // not needed;
    }
}
