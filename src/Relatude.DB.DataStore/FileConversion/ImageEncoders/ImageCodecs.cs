namespace Relatude.DB.FileConversion.ImageEncoders;

internal static class ImageCodecs
{
    private static readonly GifCodec Gif = new();

    private static readonly IImageCodec[] Codecs =
    [
        new PngCodec(),
        new JpegCodec(),
        new WebpCodec(),
        new BmpCodec(),
        Gif
    ];

    public static ImageFormat DetectFormat(ReadOnlySpan<byte> header)
    {
        foreach (IImageCodec codec in Codecs)
        {
            if (codec.CanDecode(header))
            {
                return codec.Format;
            }
        }

        return ImageFormat.Unknown;
    }

    public static IImageCodec FindDecoder(ReadOnlySpan<byte> header)
    {
        foreach (IImageCodec codec in Codecs)
        {
            if (codec.CanDecode(header))
            {
                return codec;
            }
        }

        throw new ImageFormatException("The stream does not contain a supported JPEG, PNG, WEBP, BMP or GIF image.");
    }

    /// <summary>Every frame of a gif or of an animated webp; null for anything else.</summary>
    public static Animation? ReadAnimation(byte[] data)
    {
        return Gif.CanDecode(data) ? GifCodec.Read(data, int.MaxValue) : WebpCodec.ReadAnimation(data);
    }

    public static bool IsAnimated(byte[] data)
    {
        return Gif.CanDecode(data) ? GifCodec.IsAnimated(data) : WebpCodec.IsAnimated(data);
    }

    public static bool TryReadSize(byte[] data, out int width, out int height)
    {
        width = height = 0;
        foreach (IImageCodec codec in Codecs)
        {
            if (codec.CanDecode(data))
            {
                return codec.TryReadSize(data, out width, out height);
            }
        }

        return false;
    }

    public static IImageCodec FindEncoder(ImageFormat format)
    {
        foreach (IImageCodec codec in Codecs)
        {
            if (codec.Format == format)
            {
                return codec;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported image format.");
    }
}
