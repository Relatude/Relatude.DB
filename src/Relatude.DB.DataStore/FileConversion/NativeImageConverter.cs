using Relatude.DB.Common;
using Relatude.DB.FileConversion.ImageEncoders;
namespace Relatude.DB.FileConversion;

public class NativeImageConverter : ImageConverterBase {
    static FileFormat[] _ins = [FileFormat.Jpeg, FileFormat.Png, FileFormat.Gif, FileFormat.Bmp, FileFormat.Webp];
    static FileFormat[] _outs = [FileFormat.Jpeg, FileFormat.Png, FileFormat.Bmp];
    public NativeImageConverter(int? threadCount = null) : base(_ins, _outs, NativeImage.Create, NativeImage.Load, threadCount) { }
}