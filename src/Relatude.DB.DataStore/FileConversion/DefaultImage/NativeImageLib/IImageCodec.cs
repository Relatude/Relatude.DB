namespace Relatude.DB.FileConversion.DefaultImage.NativeImageLib;

internal interface IImageCodec
{
    ImageFormat Format { get; }
    bool CanDecode(ReadOnlySpan<byte> header);
    PureImage Decode(ReadOnlySpan<byte> data);
    void Encode(PureImage image, Stream stream, ImageSaveOptions options);
}
