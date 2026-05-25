using Relatude.DB.Common;
namespace Relatude.DB.FileConversion;

public class SkiaImageConverter : ImageConverterBase {
    static FileFormat[] _ins = [FileFormat.Jpeg, FileFormat.Png, FileFormat.Gif, FileFormat.Bmp, FileFormat.Webp, FileFormat.Avif];
    static FileFormat[] _outs = [FileFormat.Jpeg, FileFormat.Png, FileFormat.Gif, FileFormat.Bmp, FileFormat.Webp, FileFormat.Avif];
    public SkiaImageConverter() : base(_ins, _outs, SkiaImage.Create, SkiaImage.Load) { }
}