using Relatude.DB.Common;
using Relatude.DB.FileConversion.ImageEncoders;
namespace Relatude.DB.FileConversion;

public class NativeImageConverter : ImageConverterBase {
    static FileFormat[] _ins = [FileFormat.Jpeg, FileFormat.Png, FileFormat.Bmp];
    static FileFormat[] _outs = [FileFormat.Jpeg, FileFormat.Png, FileFormat.Bmp];
    public NativeImageConverter(int? threadCount = null) : base(_ins, _outs, NativeImage.Create, NativeImage.Load, threadCount) { }
    public override IImage LoadFor(Stream input, ref FileAdjustmentImage adj, out BasicFileMeta meta) {
        var image = NativeImage.LoadFor(input, ref adj, out var width, out var height);
        meta = new BasicFileMeta { Width = width, Height = height };
        return image;
    }
    protected override BasicFileMeta ReadMeta(Stream input) {
        var (width, height) = NativeImage.ReadSize(input);
        return new BasicFileMeta { Width = width, Height = height };
    }
}