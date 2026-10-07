using Relatude.DB.Common;
using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.FileConversion;

public abstract class ImageConverterBase : IFileConverter {
    FileFormat[] _ins;
    FileFormat[] _outs;
    public readonly Func<int, int, IImage> Create;
    public readonly Func<Stream, IImage> Load;
    public ImageConverterBase(FileFormat[] ins, FileFormat[] outs, Func<int, int, IImage> create, Func<Stream, IImage> load, int? threadCount) {
        _ins = ins;
        _outs = outs;
        Create = create;
        Load = load;
        ThreadCount = threadCount ?? Math.Max(1, Environment.ProcessorCount / 2);
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
    BasicFileMeta getMeta(IImage image) {
        return new BasicFileMeta {
            Height = image.Height,
            Width = image.Width,
            AllMetaJson = image.GetJsonDetails(),
        };
    }
    public async Task<ConversionProgress> DoConvertWork(InputFileSource source, FileConversionInfo info) {
        await using var input = await source.OpenInputStream();
        if (info.ToFormat == FileFormat.FileMetaJson) {
            return new(new(FileConversionStatus.Ready, 100), new MemoryStream(ReadMeta(input).ToBytes()));
        }
        var adj = (FileAdjustmentImage)info.IdWithAdjustment.Adjustment;
        using var image = LoadFor(input, ref adj, out var meta);
        if (_engine != null) {
            // to avoid later meta lookups as image is already opened and meta is available
            _engine.Store.UpdateFileMetaIfNotSet(info.IdWithAdjustment.PropertyPath, info.IdWithAdjustment.FileId, meta);
        }
        using var adjusted = image.Adjust(adj);
        var bytes = adjusted.Encode(info.Formats.To, adj.Quality);
        return new(new(FileConversionStatus.Ready, 100), new MemoryStream(bytes));
    }
    /// <summary>
    /// The image an adjustment is to be applied to, and the adjustment to apply. A converter may decode a
    /// smaller picture when the result does not need every pixel, handing back the adjustment rescaled to it;
    /// the meta always describes the original.
    /// </summary>
    public virtual IImage LoadFor(Stream input, ref FileAdjustmentImage adj, out BasicFileMeta meta) {
        var image = Load(input);
        meta = getMeta(image);
        return image;
    }
    protected virtual BasicFileMeta ReadMeta(Stream input) {
        using var image = Load(input);
        return getMeta(image);
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
    FileConversionEngine? _engine;
    public void Initialize(FileConversionEngine engine) {
        _engine = engine;
    }
}
