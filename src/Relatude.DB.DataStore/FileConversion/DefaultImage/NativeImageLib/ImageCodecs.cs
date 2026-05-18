namespace Relatude.DB.FileConversion.DefaultImage.NativeImageLib;

internal static class ImageCodecs
{
    private static readonly IImageCodec[] Codecs =
    [
        new PngCodec(),
        new JpegCodec(),
        new WebpCodec(),
        new BmpCodec()
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

        throw new ImageFormatException("The stream does not contain a supported JPEG, PNG, WEBP, or BMP image.");
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
