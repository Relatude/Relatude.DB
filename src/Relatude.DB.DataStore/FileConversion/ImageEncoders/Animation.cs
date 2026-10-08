namespace Relatude.DB.FileConversion.ImageEncoders;

/// <summary>
/// An animation: every frame the whole picture as it is shown, how long each is shown (milliseconds) and how many
/// times it plays (0: forever).
/// </summary>
internal sealed class Animation(InternalImage[] frames, int[] durations, int loops) {
    // the most pixels the frames of a decoded animation hold together (256 MB, less if ImageLimits says so): the
    // frames beyond are dropped
    public static long MaxPixels => Math.Min(1L << 26, ImageLimits.MaxPixels);

    public InternalImage[] Frames { get; } = frames;
    public int[] Durations { get; } = durations;
    public int Loops { get; } = loops;

    /// <summary>Frames identical to the one before them merged into it, as (frame, duration) pairs.</summary>
    public List<(int Frame, int Duration)> Distinct() {
        var shown = new List<(int Frame, int Duration)>();
        for (int i = 0; i < Frames.Length; i++) {
            if (shown.Count > 0 && Frames[i].Pixels.SequenceEqual(Frames[shown[^1].Frame].Pixels)) shown[^1] = (shown[^1].Frame, shown[^1].Duration + Durations[i]);
            else shown.Add((i, Durations[i]));
        }
        return shown;
    }

    /// <summary>The smallest rectangle holding every position where two pictures of the same size differ, or false when none does.</summary>
    public static bool Changed<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, int width, int height, out RectangleI changed) where T : IEquatable<T> {
        int top = 0, bottom = height, left = width, right = 0;
        while (top < height && a.Slice(top * width, width).SequenceEqual(b.Slice(top * width, width))) top++;
        while (bottom > top && a.Slice((bottom - 1) * width, width).SequenceEqual(b.Slice((bottom - 1) * width, width))) bottom--;
        for (int y = top; y < bottom; y++) {
            var ra = a.Slice(y * width, width);
            var rb = b.Slice(y * width, width);
            left = Math.Min(left, ra.CommonPrefixLength(rb));
            int last = width - 1;
            while (last >= right && ra[last].Equals(rb[last])) last--;
            right = Math.Max(right, last + 1);
        }
        changed = new RectangleI(left, top, Math.Max(0, right - left), bottom - top);
        return top < bottom;
    }
}
