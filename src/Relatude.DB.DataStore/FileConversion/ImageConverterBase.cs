using Relatude.DB.Common;
using System.Diagnostics.CodeAnalysis;
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
    public virtual bool SupportsConversion(FileType inBase, FileFormat inDetailed, FileType outBase, FileFormat outDetailed) {
        return _ins.Contains(inDetailed) && (_outs.Contains(outDetailed) || outDetailed == FileFormat.FileMetaJson);
    }
    public Task<bool> CancelAsync(Guid key) {
        return Task.FromResult(false);
    }
    public async Task<ConversionProgress> DoConvertWork(InputFileSource source, FileConversionInfo info) {
        var input = await source.OpenInputStream();
        var image = Load(input);
        if (info.ToFormat == FileFormat.FileMetaJson) {
            var meta = new BasicFileMeta {
                Height = image.Height,
                Width = image.Width,
                AllMetaJson = image.GetJsonDetails(),
            };
            return new(new(FileConversionStatus.Ready, 100), new MemoryStream(meta.ToBytes()));
        }
        var imgAdj = (FileAdjustmentImage)info.IdWithAdjustment.Adjustment;
        image = image.Adjust(imgAdj);
        var bytes = image.Encode(info.Formats.To);
        var stream = new MemoryStream(bytes);
        return new(new(FileConversionStatus.Ready, 100), stream);
    }
    public bool TryGetLiveStatus(Guid key, [MaybeNullWhen(false)] out FileConversionProgressInfo status) {
        status = null;
        return false;
    }
    public byte[] CreateStatusResponse(FileFormat requestedFormat, int width, int height, List<string> text, string textColor, string fillColor) {
        var img = Create(width, height).GetStatusImage(text, textColor, fillColor);
        var bytes = img.Encode(requestedFormat);
        return bytes;
    }
    public void Initialize(FileConversionEngine engine) {
        // not needed;
    }
}
