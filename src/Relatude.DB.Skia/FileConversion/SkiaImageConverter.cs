using Relatude.DB.Common;
namespace Relatude.DB.FileConversion;

public class SkiaImageConverter : ImageConverterBase {
    static FileFormat[] _ins = [FileFormat.Jpeg, FileFormat.Png, FileFormat.Gif, FileFormat.Bmp, FileFormat.Webp, FileFormat.Avif];
    static FileFormat[] _outs = [FileFormat.Jpeg, FileFormat.Png, FileFormat.Gif, FileFormat.Bmp, FileFormat.Webp, FileFormat.Avif];
    public SkiaImageConverter() : base(_ins, _outs, SkiaImage.Create, SkiaImage.Load) {
        _isNotLinuxOs = !OperatingSystem.IsLinux();
    }
    bool _isNotLinuxOs;
    public override bool SupportsConversion(FileType inBase, FileFormat inDetailed, FileType outBase, FileFormat outDetailed) {
        if (_isNotLinuxOs && (outDetailed == FileFormat.Avif)) {
            return false;  // SkiaSharp on Linux does not support AVIF encoding as of now, so we return false for AVIF output on Linux.
        }
        return base.SupportsConversion(inBase, inDetailed, outBase, outDetailed);
    }

}