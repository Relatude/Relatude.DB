using Relatude.DB.Common;
using Relatude.DB.FileConversion.ImageEncoders;
namespace Relatude.DB.FileConversion;

public class SkiaImageConverter : ImageConverterBase {
    // gifs are left to the native converter, which keeps their animations; Skia cannot write gif or bmp
    static FileFormat[] _ins = [FileFormat.Jpeg, FileFormat.Png, FileFormat.Bmp, FileFormat.Webp, FileFormat.Avif];
    static FileFormat[] _outs = [FileFormat.Jpeg, FileFormat.Png, FileFormat.Webp, FileFormat.Avif];
    public SkiaImageConverter(int? threadCount = null) : base(_ins, _outs, SkiaImage.Create, SkiaImage.Load, threadCount) {
        _isNotLinuxOs = !OperatingSystem.IsLinux();
    }
    bool _isNotLinuxOs;
    public override bool SupportsConversion(FileType inBase, FileFormat inDetailed, FileType outBase, FileFormat outDetailed) {
        if (_isNotLinuxOs && (outDetailed == FileFormat.Avif)) {
            return false;  // SkiaSharp on Linux does not support AVIF encoding as of now, so we return false for AVIF output on Linux.
        }
        return base.SupportsConversion(inBase, inDetailed, outBase, outDetailed);
    }
    // an animated webp that stays webp is read natively: Skia reads only its first frame
    public override IImage LoadFor(Stream input, ref FileAdjustmentImage adj, out BasicFileMeta meta) {
        if (adj.RequestedFormat != FileFormat.Webp) return base.LoadFor(input, ref adj, out meta);
        var buffer = new MemoryStream();
        input.CopyTo(buffer);
        var data = buffer.ToArray();
        if (!NativeImage.IsAnimated(data)) return base.LoadFor(new MemoryStream(data), ref adj, out meta);
        var image = NativeImage.LoadFor(new MemoryStream(data), ref adj, out int width, out int height);
        meta = new BasicFileMeta { Width = width, Height = height };
        return image;
    }

}