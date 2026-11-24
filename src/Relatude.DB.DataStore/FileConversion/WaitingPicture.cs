using Relatude.DB.Common;
using Relatude.DB.FileConversion.ImageEncoders;

namespace Relatude.DB.FileConversion;

/// <summary>
/// What a request for a picture gets when the picture is not made within the time it waits: a clock, grey and black on
/// light grey, at the size of the picture it stands in for. A failed conversion keeps its text.
/// </summary>
internal static class WaitingPicture {
    static readonly FileFormat[] nativeFormats = [FileFormat.Jpeg, FileFormat.Png, FileFormat.Webp, FileFormat.Bmp, FileFormat.Gif];
    static readonly ColorRgba ground = new(0xEC, 0xEC, 0xEC);
    static readonly ColorRgba ring = new(0x80, 0x80, 0x80);
    static readonly ColorRgba hands = new(0x1E, 0x1E, 0x1E);

    public static bool CanEncode(FileFormat format, FileConverterLibrary converters) =>
        FileFormatUtil.GetFileType(format) == FileType.Image
        && (nativeFormats.Contains(format) || converters.TryGetConverter(new FormatPair(FileFormat.Png, format), out var c) && c is ImageConverterBase);

    public static byte[] Render(FileFormat format, int width, int height, FileConverterLibrary converters) {
        var image = new NativeImage(Draw(width, height));
        if (nativeFormats.Contains(format)) return image.Encode(format);
        // a format the native encoders do not write (AVIF): the PNG read again by the converter that does
        converters.TryGetConverter(new FormatPair(FileFormat.Png, format), out var converter);
        using var read = ((ImageConverterBase)converter!).Load(new MemoryStream(image.Encode(FileFormat.Png)));
        return read.Encode(format);
    }

    internal static InternalImage Draw(int width, int height) {
        var pixels = new byte[checked(width * height * 4)];
        for (var x = 0; x < width; x++) set(pixels, x * 4, ground);
        for (var y = 1; y < height; y++) Array.Copy(pixels, 0, pixels, y * width * 4, width * 4);

        double cx = width / 2.0, cy = height / 2.0;
        var radius = Math.Max(4.0, Math.Min(width, height) * 0.2);
        var stroke = Math.Max(1.5, radius * 0.14);
        var middle = radius - stroke / 2;
        // the minute hand at twelve, the hour hand at three
        (double x, double y) minute = (cx, cy - middle * 0.7), hour = (cx + middle * 0.48, cy);

        int left = Math.Max(0, (int)(cx - radius - 1)), right = Math.Min(width - 1, (int)Math.Ceiling(cx + radius + 1));
        int top = Math.Max(0, (int)(cy - radius - 1)), bottom = Math.Min(height - 1, (int)Math.Ceiling(cy + radius + 1));
        for (var y = top; y <= bottom; y++) {
            for (var x = left; x <= right; x++) {
                double px = x + 0.5, py = y + 0.5;
                var onRing = cover(Math.Abs(Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy)) - middle) - stroke / 2);
                var onHands = Math.Max(
                    cover(toSegment(px, py, cx, cy, minute.x, minute.y) - stroke / 2),
                    cover(toSegment(px, py, cx, cy, hour.x, hour.y) - stroke / 2));
                if (onRing <= 0 && onHands <= 0) continue;
                set(pixels, (y * width + x) * 4, mix(mix(ground, ring, onRing), hands, onHands));
            }
        }
        return new InternalImage(width, height, pixels);
    }

    // how much of a pixel a shape covers, from the distance of its centre to the shape's edge (negative inside)
    static double cover(double distance) => Math.Clamp(0.5 - distance, 0, 1);

    static double toSegment(double px, double py, double ax, double ay, double bx, double by) {
        double dx = bx - ax, dy = by - ay;
        var t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy), 0, 1);
        double ex = px - (ax + t * dx), ey = py - (ay + t * dy);
        return Math.Sqrt(ex * ex + ey * ey);
    }

    static ColorRgba mix(ColorRgba a, ColorRgba b, double t) => t <= 0 ? a : t >= 1 ? b : new(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    static void set(byte[] pixels, int at, ColorRgba c) {
        pixels[at] = c.R;
        pixels[at + 1] = c.G;
        pixels[at + 2] = c.B;
        pixels[at + 3] = 255;
    }
}
