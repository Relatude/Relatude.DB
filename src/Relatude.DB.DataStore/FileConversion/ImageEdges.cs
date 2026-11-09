using Relatude.DB.FileConversion.ImageEncoders;

namespace Relatude.DB.FileConversion;

/// <summary>
/// The colour of a picture's edges, for the background of the room around it where it is fitted whole: read from a band
/// along all four sides (<see cref="Band"/> of its shorter side, a pixel at least), the commonest colour there (its
/// pixels and those a step from it, averaged, so a JPEG's noise and a soft gradient count as one) when it holds
/// <see cref="Common"/> of the band, else the band's average. So a logo on white gets white and a drawing on a coloured
/// ground its ground. Null when most of the band is see-through. The CMS interface's edgeColourOf (imaging/auto.ts) is the same.
/// </summary>
public static class ImageEdges {
    public const double Band = 0.02;
    public const double Common = 1.0 / 3;
    /// <summary>How large a copy of the picture is read, each way at most.</summary>
    public const int Sample = 256;

    /// <summary>"#rrggbb", or null for edges mostly see-through; the picture decoded here, small (BMP, GIF, JPEG, PNG and WebP).</summary>
    public static string? ColorOf(byte[] picture) {
        var adjustment = new FileAdjustmentImage { Width = Sample, Height = Sample, CropMode = ImageCropMode.Fit };
        using var image = NativeImage.LoadFor(new MemoryStream(picture, false), ref adjustment, out _, out _);
        return image.EdgeColor();
    }

    public static string? ColorOf(int width, int height, PixelReader read) {
        if (width < 1 || height < 1) return null;
        var band = Math.Max(1, (int)Math.Round(Math.Min(width, height) * Band));
        var count = new int[4096];
        var sums = new double[4096 * 3];
        long total = 0, clear = 0;
        double r = 0, g = 0, b = 0;
        for (var y = 0; y < height; y++) {
            var across = y < band || y >= height - band;
            for (var x = 0; x < width; x++) {
                if (!across && x == band && width - band > band) x = width - band; // the inside is skipped
                read(x, y, out var pr, out var pg, out var pb, out var pa);
                total++;
                if (pa < 128) { clear++; continue; }
                var k = ((pr >> 4) << 8) | ((pg >> 4) << 4) | (pb >> 4);
                count[k]++;
                sums[k * 3] += pr; sums[k * 3 + 1] += pg; sums[k * 3 + 2] += pb;
                r += pr; g += pg; b += pb;
            }
        }
        var solid = total - clear;
        if (total == 0 || clear * 2 > total || solid == 0) return null;
        int best = -1, most = 0;
        for (var k = 0; k < 4096; k++) {
            if (count[k] == 0) continue;
            var n = 0;
            around(k, j => n += count[j]);
            if (n > most) { most = n; best = k; }
        }
        if (most >= solid * Common) {
            double sr = 0, sg = 0, sb = 0;
            around(best, j => { sr += sums[j * 3]; sg += sums[j * 3 + 1]; sb += sums[j * 3 + 2]; });
            return hex(sr / most, sg / most, sb / most);
        }
        return hex(r / solid, g / solid, b / solid);
    }

    /// <summary>The steps a step or none from step <paramref name="k"/> on each channel: the 27 about it, those in range.</summary>
    static void around(int k, Action<int> visit) {
        int r = k >> 8, g = (k >> 4) & 15, b = k & 15;
        for (var dr = -1; dr <= 1; dr++) {
            if (r + dr is < 0 or > 15) continue;
            for (var dg = -1; dg <= 1; dg++) {
                if (g + dg is < 0 or > 15) continue;
                for (var db = -1; db <= 1; db++) {
                    if (b + db is < 0 or > 15) continue;
                    visit(((r + dr) << 8) | ((g + dg) << 4) | (b + db));
                }
            }
        }
    }

    static string hex(double r, double g, double b) {
        static int c(double v) => (int)Math.Round(Math.Clamp(v, 0, 255), MidpointRounding.AwayFromZero);
        return $"#{c(r):x2}{c(g):x2}{c(b):x2}";
    }
}
