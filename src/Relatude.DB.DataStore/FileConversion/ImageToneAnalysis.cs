namespace Relatude.DB.FileConversion;

/// <summary>Reads a single pixel. Declared as a delegate rather than a Func returning a tuple so
/// that sampling an image allocates nothing per pixel.</summary>
public delegate void PixelReader(int x, int y, out byte r, out byte g, out byte b, out byte a);

/// <summary>
/// A cheap statistical summary of an image, used by <see cref="AutoLightDarkSwitch"/> to decide
/// whether inverting the luminance is likely to give a better result than leaving the image alone.
/// The numbers come from a subsampled grid, never the full image, so this is safe to run as part of
/// every conversion.
/// </summary>
public readonly record struct ImageToneAnalysis {

    /// <summary>Pixels below this luminance count as dark.</summary>
    public const double DarkThreshold = 0.25;

    /// <summary>Pixels above this luminance count as light.</summary>
    public const double LightThreshold = 0.75;

    // A graphic — logo, icon, diagram, screenshot, scanned document — still reads correctly after a
    // luminance inversion. A photograph does not: inverting it produces a negative, never a better
    // picture. Three independent signals separate the two, and all three have to agree before the
    // automatic modes are allowed to invert anything.
    const double _maxPaletteRichness = 0.25;   // photographs draw on far more colours
    const double _minFlatFraction = 0.45;      // photographs have almost no identical neighbours
    const double _minExtremeFraction = 0.40;   // photographs live in the mid-tones

    // Below this opacity the visible pixels are the artwork's ink rather than its background.
    const double _minOpaqueFractionForBackground = 0.75;

    // Dead zone around the middle: an image that is neither clearly light nor clearly dark is left
    // alone rather than flipped on a coin toss.
    const double _darkSurfaceCeiling = 0.45;
    const double _lightSurfaceFloor = 0.55;

    /// <summary>Number of pixels sampled.</summary>
    public int SampleCount { get; init; }

    /// <summary>Number of sampled pixels that were not (near) transparent.</summary>
    public int VisibleSampleCount { get; init; }

    /// <summary>Mean Rec.709 luminance (0..1) across the visible samples.</summary>
    public double MeanLuminance { get; init; }

    /// <summary>Share of the visible samples darker than <see cref="DarkThreshold"/>.</summary>
    public double DarkFraction { get; init; }

    /// <summary>Share of the visible samples lighter than <see cref="LightThreshold"/>.</summary>
    public double LightFraction { get; init; }

    /// <summary>Share of all samples that were not (near) transparent.</summary>
    public double OpaqueFraction { get; init; }

    /// <summary>Share of samples identical to their left-hand neighbour. High for flat artwork,
    /// close to zero for photographs, which are never entirely free of noise.</summary>
    public double FlatFraction { get; init; }

    /// <summary>Distinct colours, quantized to four bits per channel, relative to how many distinct
    /// colours the sample could have held. Low for a limited palette, high for a photograph.</summary>
    public double PaletteRichness { get; init; }

    /// <summary>Share of the visible samples sitting at either end of the luminance range.</summary>
    public double ExtremeFraction => DarkFraction + LightFraction;

    /// <summary>True when the image looks like flat artwork rather than a photograph, and therefore
    /// survives a luminance inversion.</summary>
    public bool IsInvertibleArtwork =>
        VisibleSampleCount > 0
        && PaletteRichness <= _maxPaletteRichness
        && FlatFraction >= _minFlatFraction
        && ExtremeFraction >= _minExtremeFraction;

    /// <summary>
    /// The luminance of the surface the image appears to have been made for. For a mostly opaque
    /// image that is its own dominant tone — the background it carries with it. For artwork with a
    /// large transparent area the visible pixels are the ink instead, and ink is drawn to contrast
    /// with its surface, so the reading flips: dark ink implies a light surface.
    /// </summary>
    public double AssumedSurfaceLuminance =>
        OpaqueFraction >= _minOpaqueFractionForBackground ? MeanLuminance : 1 - MeanLuminance;

    /// <summary>Decide whether <see cref="FileAdjustmentImage.InvertLuminance"/> should be applied
    /// to reach the mode asked for. Returns false for photographs and for images that are neither
    /// clearly light nor clearly dark.</summary>
    public bool ShouldInvertLuminance(AutoLightDarkSwitch mode) {
        if (mode == AutoLightDarkSwitch.None) return false;
        if (!IsInvertibleArtwork) return false;
        return mode switch {
            AutoLightDarkSwitch.AdaptToLightModeIfNeeded => AssumedSurfaceLuminance < _darkSurfaceCeiling,
            AutoLightDarkSwitch.AdaptToDarkModeIfNeeded => AssumedSurfaceLuminance > _lightSurfaceFloor,
            _ => false,
        };
    }

    /// <summary>Sample an image on a grid of at most 160×160 pixels and summarize its tone.</summary>
    public static ImageToneAnalysis Analyze(int width, int height, PixelReader read) {
        const int maxSamplesPerAxis = 160;
        const int paletteBuckets = 16 * 16 * 16;
        if (width <= 0 || height <= 0) return new();
        var strideX = Math.Max(1, (width + maxSamplesPerAxis - 1) / maxSamplesPerAxis);
        var strideY = Math.Max(1, (height + maxSamplesPerAxis - 1) / maxSamplesPerAxis);
        var palette = new HashSet<int>();
        int samples = 0, visible = 0, dark = 0, light = 0, flat = 0, comparisons = 0;
        var luminanceSum = 0d;
        for (var y = 0; y < height; y += strideY) {
            var previous = -1L; // -1 marks the start of a row, where there is nothing to compare to
            for (var x = 0; x < width; x += strideX) {
                read(x, y, out var r, out var g, out var b, out var a);
                samples++;
                var packed = ((long)r << 24) | ((long)g << 16) | ((long)b << 8) | a;
                if (previous >= 0) {
                    comparisons++;
                    if (packed == previous) flat++;
                }
                previous = packed;
                palette.Add(((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4));
                if (a < 128) continue; // near-transparent pixels carry no tone
                visible++;
                var luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
                luminanceSum += luminance;
                if (luminance < DarkThreshold) dark++;
                else if (luminance > LightThreshold) light++;
            }
        }
        return new ImageToneAnalysis {
            SampleCount = samples,
            VisibleSampleCount = visible,
            MeanLuminance = visible == 0 ? 0 : luminanceSum / visible,
            DarkFraction = visible == 0 ? 0 : (double)dark / visible,
            LightFraction = visible == 0 ? 0 : (double)light / visible,
            OpaqueFraction = samples == 0 ? 0 : (double)visible / samples,
            FlatFraction = comparisons == 0 ? 0 : (double)flat / comparisons,
            PaletteRichness = samples == 0 ? 0 : palette.Count / Math.Min(samples, (double)paletteBuckets),
        };
    }
}
