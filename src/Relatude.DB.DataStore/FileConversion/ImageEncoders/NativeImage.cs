using Relatude.DB.Common;

namespace Relatude.DB.FileConversion.ImageEncoders;

/// <summary>Pure C# implementation. An animated gif or webp keeps its frames, every change made to each of them.</summary>
public sealed class NativeImage : IImage {
    readonly InternalImage[] _frames;
    readonly int[] _durations; // milliseconds
    readonly int _loops; // 0: forever
    InternalImage _image => _frames[0];

    public int Width => _image.Width;
    public int Height => _image.Height;
    public int FrameCount => _frames.Length;

    internal NativeImage(InternalImage image) : this([image], [0], 1) { }
    internal NativeImage(Animation animation) : this(animation.Frames, animation.Durations, animation.Loops) { }
    NativeImage(InternalImage[] frames, int[] durations, int loops) => (_frames, _durations, _loops) = (frames, durations, loops);

    public static NativeImage Load(Stream stream) => Load(InternalImage.ReadAll(stream));

    static NativeImage Load(byte[] data) => ImageCodecs.ReadAnimation(data) is { } animation ? new(animation) : new(InternalImage.Load(data));

    /// <summary>True for a gif or webp with more than one frame.</summary>
    public static bool IsAnimated(byte[] data) => ImageCodecs.IsAnimated(data);

    // the same change to every frame
    NativeImage Map(Func<InternalImage, InternalImage> change) {
        if (_frames.Length == 1) return new(change(_image));
        var frames = new InternalImage[_frames.Length];
        Parallel.For(0, frames.Length, i => frames[i] = change(_frames[i]));
        return new(frames, _durations, _loops);
    }

    /// <summary>
    /// The picture to apply the adjustment to, decoded no larger than the result needs: a jpeg asked for at a
    /// fraction of its size decodes at 1/2, 1/4 or 1/8 of it. The adjustment comes back rescaled to what was
    /// decoded; width and height are the original's.
    /// </summary>
    public static NativeImage LoadFor(Stream stream, ref FileAdjustmentImage adj, out int width, out int height) {
        var data = InternalImage.ReadAll(stream);
        if (!InternalImage.TryReadSize(data, out width, out height) || ImageCodecs.IsAnimated(data)) {
            var full = Load(data);
            (width, height) = (full.Width, full.Height);
            return full;
        }
        var image = InternalImage.Load(data, Downscale(width, height, adj));
        if (image.Width != width && (adj.FocusX.HasValue || adj.FocusY.HasValue)) {
            var scaled = FileAdjustmentImage.FromBytes(adj.ToBytes());
            scaled.FocusX = adj.FocusX * image.Width / width;
            scaled.FocusY = adj.FocusY * image.Height / height;
            adj = scaled;
        }
        return new(image);
    }

    /// <summary>The size of the image in the stream, from its header where the format has one.</summary>
    public static (int Width, int Height) ReadSize(Stream stream) {
        var data = InternalImage.ReadAll(stream);
        if (InternalImage.TryReadSize(data, out var width, out var height)) return (width, height);
        var image = InternalImage.Load(data);
        return (image.Width, image.Height);
    }

    // the largest decode reduction that still leaves at least the pixels the adjustment scales the whole picture to
    static int Downscale(int width, int height, FileAdjustmentImage adj) {
        if (adj.Width == null && adj.Height == null || adj.SourceWidth != null || adj.SourceHeight != null || adj.Zoom != null) return 1;
        double rotation = adj.Rotation ?? 0;
        if (rotation % 90 != 0) return 1;
        if (rotation % 180 != 0) (width, height) = (height, width);
        double needW, needH;
        var mode = adj.CropMode ?? ImageCropMode.Fit;
        if (mode == ImageCropMode.Stretch) {
            needW = adj.Width ?? width;
            needH = adj.Height ?? height;
        } else {
            double sx = (double)(adj.Width ?? 0) / width, sy = (double)(adj.Height ?? 0) / height;
            double scale = adj.Width == null ? sy : adj.Height == null ? sx : mode == ImageCropMode.Fit ? Math.Min(sx, sy) : Math.Max(sx, sy);
            needW = width * scale;
            needH = height * scale;
        }
        int factor = 8;
        while (factor > 1 && ((width + factor - 1) / factor < needW || (height + factor - 1) / factor < needH)) factor /= 2;
        return factor;
    }
    public static NativeImage Create(int width, int height) => new(InternalImage.Create(width, height));

    // ── Metadata  ──────────────────────────────────────────────────────────
    public string? GetJsonDetails() {
        return null;
    }

    // ── Geometry ────────────────────────────────────────────────────────────

    public IImage Rotate(double degrees) => Map(f => f.Rotate(degrees));

    public IImage Crop(int x, int y, int width, int height) {
        var w = Math.Clamp(width, 1, Width);
        var h = Math.Clamp(height, 1, Height);
        var rect = new RectangleI(Math.Clamp(x, 0, Width - w), Math.Clamp(y, 0, Height - h), w, h);
        return Map(f => f.Crop(rect));
    }

    public IImage Resize(int? width, int? height, ImageCropMode cropMode = ImageCropMode.Fill, CropHints hints = default) {
        int srcW = Width, srcH = Height;

        int targetW, targetH;
        if (cropMode == ImageCropMode.Stretch) {
            targetW = width ?? srcW;
            targetH = height ?? srcH;
        } else if (width.HasValue && !height.HasValue) {
            targetW = width.Value;
            targetH = Math.Max(1, (int)Math.Round(targetW * (double)srcH / srcW));
        } else if (height.HasValue && !width.HasValue) {
            targetH = height.Value;
            targetW = Math.Max(1, (int)Math.Round(targetH * (double)srcW / srcH));
        } else {
            targetW = width ?? srcW;
            targetH = height ?? srcH;
        }

        if (targetW == srcW && targetH == srcH) return Map(f => f);

        var bg = ParseBackground(hints.BackgroundColor);

        return cropMode switch {
            ImageCropMode.Stretch => Map(f => f.Resize(targetW, targetH)),
            ImageCropMode.Fill => Map(f => ResizeAndCrop(f, targetW, targetH, hints.FocusX, hints.FocusY, hints.OffsetX, hints.OffsetY)),
            ImageCropMode.Fit => Map(f => ResizeToFit(f, targetW, targetH, bg)),
            _ => Map(f => ResizeAuto(f, targetW, targetH, hints.FocusX, hints.FocusY, hints.OffsetX, hints.OffsetY, bg)),
        };
    }

    // ── Colour adjustments ──────────────────────────────────────────────────
    // PureImage ranges: brightness amount → offset = amount*255; contrast factor = Max(0, 1+amount);
    // IImage ranges: -100..100.  Divide by 100 to bridge them.

    public IImage AdjustBrightness(double brightness) => Map(f => f.AdjustBrightness(brightness / 100.0));
    public IImage AdjustContrast(double contrast) => Map(f => f.AdjustContrast(contrast / 100.0));
    public IImage AdjustSaturation(double saturation) => Map(f => f.AdjustSaturation(saturation / 100.0));
    public IImage AdjustSharpness(double sharpness) {
        if (sharpness < 0) {
            double radius = -sharpness / 100.0 * 19.5 + 0.5;
            return Map(f => f.Blur(radius));
        }
        return Map(f => f.AdjustSharpness(sharpness / 100.0));
    }

    public IImage AdjustHue(double hueShift) => Map(f => f.AdjustHue(hueShift));

    // Invert first, then rotate the hue back: inverting red gives cyan, and rotating cyan by 180
    // returns it to red — at the inverted lightness, which is the whole point. The other order would
    // simply undo itself.
    public IImage InvertLuminance() => Map(f => f.Invert().AdjustHue(180));

    public ImageToneAnalysis AnalyzeTone() {
        var image = _image;
        return ImageToneAnalysis.Analyze(Width, Height, (int x, int y, out byte r, out byte g, out byte b, out byte a) => {
            var c = image[x, y];
            r = c.R; g = c.G; b = c.B; a = c.A;
        });
    }

    // ── Drawing ─────────────────────────────────────────────────────────────

    public IImage DrawLine(int x1, int y1, int x2, int y2, int width, string color) =>
        Map(f => f.DrawLine(x1, y1, x2, y2, width, ParseBackground(color)));

    public IImage DrawText(int x, int y, string text, int fontSizeInPixels, string color, bool sansSerif) =>
        Map(f => f.DrawText(x, y, text, fontSizeInPixels, ParseBackground(color), sansSerif));

    public IImage DrawBox(int x1, int y1, int x2, int y2, int borderWidth, string borderColor, bool filled, string fillColor) =>
        Map(f => f.DrawBox(x1, y1, x2, y2, borderWidth, ParseBackground(borderColor), filled, ParseBackground(fillColor)));

    // ── Encode

    // an animation stays one as gif or webp, any other format gets its first frame
    public byte[] Encode(FileFormat format, int? quality = null) {
        var opts = new ImageSaveOptions { Quality = quality ?? 90 };
        using var ms = new MemoryStream();
        if (_frames.Length > 1 && format == FileFormat.Gif) GifCodec.Write(new Animation(_frames, _durations, _loops), ms);
        else if (_frames.Length > 1 && format == FileFormat.Webp) WebpCodec.WriteAnimation(new Animation(_frames, _durations, _loops), ms, opts);
        else _image.Save(ms, ToNativeFormat(format), opts);
        return ms.ToArray();
    }

    public void Dispose() { /* PureImage is not IDisposable — nothing to release */ }

    // ── Resize helpers ──────────────────────────────────────────────────────

    static InternalImage ResizeAndCrop(InternalImage src, int targetW, int targetH, int? focusX, int? focusY, int? offsetX, int? offsetY) {
        double scale = Math.Max((double)targetW / src.Width, (double)targetH / src.Height);
        int scaledW = Math.Max(1, (int)Math.Round(src.Width * scale));
        int scaledH = Math.Max(1, (int)Math.Round(src.Height * scale));
        var scaled = src.Resize(scaledW, scaledH);
        int fx = focusX.HasValue ? (int)Math.Round(focusX.Value * scale) : scaledW / 2;
        int fy = focusY.HasValue ? (int)Math.Round(focusY.Value * scale) : scaledH / 2;
        int x = Math.Clamp(fx - targetW / 2 + (offsetX ?? 0), 0, Math.Max(0, scaledW - targetW));
        int y = Math.Clamp(fy - targetH / 2 + (offsetY ?? 0), 0, Math.Max(0, scaledH - targetH));
        return scaled.Crop(new RectangleI(x, y, targetW, targetH));
    }

    static InternalImage ResizeToFit(InternalImage src, int canvasW, int canvasH, ColorRgba bg) {
        double scale = Math.Min((double)canvasW / src.Width, (double)canvasH / src.Height);
        int scaledW = Math.Max(1, (int)Math.Round(src.Width * scale));
        int scaledH = Math.Max(1, (int)Math.Round(src.Height * scale));
        var scaled = src.Resize(scaledW, scaledH);
        if (scaledW == canvasW && scaledH == canvasH) return scaled;
        return scaled.Pad(canvasW, canvasH, (canvasW - scaledW) / 2, (canvasH - scaledH) / 2, bg);
    }

    static InternalImage ResizeAuto(InternalImage src, int targetW, int targetH, int? focusX, int? focusY, int? offsetX, int? offsetY, ColorRgba bg) {
        double srcAspect = (double)src.Width / src.Height;
        double canvasAspect = (double)targetW / targetH;
        // Use Fit when aspect ratios are close (less than 5% difference), otherwise Fill
        bool useFit = Math.Abs(srcAspect - canvasAspect) / canvasAspect < 0.05;
        return useFit
            ? ResizeToFit(src, targetW, targetH, bg)
            : ResizeAndCrop(src, targetW, targetH, focusX, focusY, offsetX, offsetY);
    }

    // ── Misc helpers

    static ColorRgba ParseBackground(string? hex) {
        if (string.IsNullOrWhiteSpace(hex)) return new ColorRgba(0, 0, 0, 0);
        var s = hex.TrimStart('#');
        if (s.Length == 6 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return new ColorRgba((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        if (s.Length == 8 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var rgba))
            return new ColorRgba((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);
        return new ColorRgba(0, 0, 0, 0);
    }

    static ImageFormat ToNativeFormat(FileFormat f) => f switch {
        FileFormat.Jpeg => ImageFormat.Jpeg,
        FileFormat.Png => ImageFormat.Png,
        FileFormat.Webp => ImageFormat.Webp,
        FileFormat.Bmp => ImageFormat.Bmp,
        FileFormat.Gif => ImageFormat.Gif,
        _ => ImageFormat.Png,
    };
}
