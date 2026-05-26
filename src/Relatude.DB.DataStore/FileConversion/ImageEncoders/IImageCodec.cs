namespace Relatude.DB.FileConversion.ImageEncoders;

internal interface IImageCodec
{
    ImageFormat Format { get; }
    bool CanDecode(ReadOnlySpan<byte> header);
    InternalImage Decode(ReadOnlySpan<byte> data);
    void Encode(InternalImage image, Stream stream, ImageSaveOptions options);
}
