using Relatude.DB.Common;
namespace Relatude.DB.FileConversion;

public class SkiaImageConverter : ImageConverterBase {
    static FileFormat[] _ins = [FileFormat.Jpeg, FileFormat.Png, FileFormat.Gif, FileFormat.Bmp, FileFormat.Webp];
    static FileFormat[] _outs = [FileFormat.Jpeg, FileFormat.Png, FileFormat.Gif, FileFormat.Bmp, FileFormat.Webp];
    public SkiaImageConverter() : base(_ins, _outs, SkiaImage.Create, SkiaImage.Load) { }
}