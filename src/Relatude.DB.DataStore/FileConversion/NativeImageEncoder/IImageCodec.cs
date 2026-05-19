namespace Relatude.DB.FileConversion.NativeImageEncoder;

internal interface IImageCodec
{
    ImageFormat Format { get; }
    bool CanDecode(ReadOnlySpan<byte> header);
    NativeRImage Decode(ReadOnlySpan<byte> data);
    void Encode(NativeRImage image, Stream stream, ImageSaveOptions options);
}
