namespace Relatude.DB.FileConversion.ImageEncoders;

internal interface IImageCodec
{
    ImageFormat Format { get; }
    bool CanDecode(ReadOnlySpan<byte> header);
    /// <summary>Decodes the image, at 1/downscale of its size when the format can do that cheaply.</summary>
    InternalImage Decode(byte[] data, int downscale);
    bool TryReadSize(byte[] data, out int width, out int height);
    void Encode(InternalImage image, Stream stream, ImageSaveOptions options);
}
