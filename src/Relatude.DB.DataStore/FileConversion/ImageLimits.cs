using Relatude.DB.FileConversion.ImageEncoders;

namespace Relatude.DB.FileConversion;

/// <summary>
/// What keeps a crafted picture from taking the server's memory: a file of a few bytes can declare a picture of
/// gigabytes. Decoders compare the size a file declares with this before they allocate its pixels, and adjustments
/// the size they would produce.
/// </summary>
public static class ImageLimits {
    /// <summary>
    /// The most pixels a picture may have, decoded or as an adjustment makes it: 200 megapixels unless set, room
    /// for the largest phone photos (16320 x 12240). A decoded pixel takes 4 bytes.
    /// </summary>
    public static long MaxPixels { get; set; } = 200_000_000;

    /// <summary>
    /// The most pixels an adjustment may enlarge a picture to: 4096 x 4096 unless set. Sizes can come from a URL,
    /// and a tiny picture enlarged to the full <see cref="MaxPixels"/> costs as much as the largest photo. A result no
    /// larger than its source is bounded by <see cref="MaxPixels"/> alone.
    /// </summary>
    public static long MaxEnlargedPixels { get; set; } = 4096 * 4096;

    /// <summary>Refuses a picture of more than <see cref="MaxPixels"/>.</summary>
    public static void ThrowIfTooLarge(long width, long height) {
        if ((double)width * height > MaxPixels)
            throw new ImageFormatException($"The image is {width} x {height} pixels, more than the {MaxPixels:N0} allowed (ImageLimits.MaxPixels).");
    }

    /// <summary>Refuses an adjustment's result of more than <see cref="MaxPixels"/>, or enlarged beyond <see cref="MaxEnlargedPixels"/>.</summary>
    public static void ThrowIfResultTooLarge(long width, long height, long sourceWidth, long sourceHeight) {
        ThrowIfTooLarge(width, height);
        double pixels = (double)width * height;
        if (pixels > (double)sourceWidth * sourceHeight && pixels > MaxEnlargedPixels)
            throw new ImageFormatException($"{width} x {height} pixels enlarges the image beyond the {MaxEnlargedPixels:N0} allowed (ImageLimits.MaxEnlargedPixels).");
    }
}
