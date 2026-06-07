using LibAvifSharp;
using LibAvifSharp.NativeTypes;
using Relatude.DB.Common;
using SkiaSharp;
using System.Text.Json;

namespace Relatude.DB.FileConversion;

/// <summary>SkiaSharp-backed implementation of <see cref="IImage"/>.</summary>
internal sealed class SkiaImage : IImage {
    readonly SKBitmap _bitmap;
    readonly int? _focusX;
    readonly int? _focusY;
    readonly int? _offsetX;
    readonly int? _offsetY;
    readonly string? _backgroundColor;

    public int Width => _bitmap.Width;
    public int Height => _bitmap.Height;

    public SkiaImage(SKBitmap bitmap, int? focusX = null, int? focusY = null, int? offsetX = null, int? offsetY = null, string? backgroundColor = null) {
        _bitmap = bitmap;
        _focusX = focusX;
        _focusY = focusY;
        _offsetX = offsetX;
        _offsetY = offsetY;
        _backgroundColor = backgroundColor;
    }

    public static SkiaImage Load(Stream stream) => new(SKBitmap.Decode(stream) ?? throw new InvalidOperationException("Failed to decode image."));

    // ── Metadata  ──────────────────────────────────────────────────────────
    public string? GetJsonDetails() {
        if (_bitmap == null) return null;
        var cs = _bitmap.Info.ColorSpace;
        var meta = new {
            Width = _bitmap.Width,
            Height = _bitmap.Height,
            AspectRatio = Math.Round((double)_bitmap.Width / _bitmap.Height, 4),
            TotalPixels = _bitmap.Width * _bitmap.Height,
            ColorType = _bitmap.ColorType.ToString(),
            AlphaType = _bitmap.AlphaType.ToString(),
            IsOpaque = _bitmap.Info.IsOpaque,
            BitsPerPixel = _bitmap.Info.BitsPerPixel,
            BytesPerPixel = _bitmap.BytesPerPixel,
            RowBytes = _bitmap.RowBytes,
            ByteCount = _bitmap.ByteCount,
            MemorySizeMB = Math.Round(_bitmap.ByteCount / (1024.0 * 1024.0), 4),
            IsImmutable = _bitmap.IsImmutable,
            IsEmpty = _bitmap.IsEmpty,
            IsNull = _bitmap.IsNull,
            ColorSpaceIsSRgb = cs?.IsSrgb ?? true,
            ColorSpaceGammaIsLinear = cs?.GammaIsLinear ?? false,
        };
        return JsonSerializer.Serialize(meta, new JsonSerializerOptions());
    }


    // ── Crop hints ──────────────────────────────────────────────────────────

    public IImage SetFocus(int? focusX, int? focusY) => new SkiaImage(_bitmap.Copy(), focusX, focusY, _offsetX, _offsetY, _backgroundColor);
    public IImage SetOffset(int? offsetX, int? offsetY) => new SkiaImage(_bitmap.Copy(), _focusX, _focusY, offsetX, offsetY, _backgroundColor);
    public IImage SetBackgroundColor(string? color) => new SkiaImage(_bitmap.Copy(), _focusX, _focusY, _offsetX, _offsetY, color);

    // ── Geometry ────────────────────────────────────────────────────────────

    public IImage Rotate(double degrees) {
        double norm = ((degrees % 360) + 360) % 360;
        if (norm == 0) return new SkiaImage(_bitmap.Copy(), _focusX, _focusY, _offsetX, _offsetY);

        // Fast paths for cardinal angles
        if (norm == 90) return new SkiaImage(Rotate90CW(_bitmap));
        if (norm == 180) return new SkiaImage(Rotate180(_bitmap));
        if (norm == 270) return new SkiaImage(Rotate90CCW(_bitmap));

        // Arbitrary angle: rotate around centre, expand canvas to fit
        float rad = (float)(degrees * Math.PI / 180.0);
        float cos = MathF.Abs(MathF.Cos(rad)), sin = MathF.Abs(MathF.Sin(rad));
        int newW = (int)MathF.Ceiling(Width * cos + Height * sin);
        int newH = (int)MathF.Ceiling(Width * sin + Height * cos);
        using var surface = SKSurface.Create(new SKImageInfo(newW, newH));
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        c.Translate(newW / 2f, newH / 2f);
        c.RotateDegrees((float)degrees);
        c.Translate(-Width / 2f, -Height / 2f);
        c.DrawBitmap(_bitmap, 0, 0);
        return new SkiaImage(SKBitmap.FromImage(surface.Snapshot()));
    }

    public IImage Zoom(double zoom) {
        if (zoom <= 0 || zoom == 100) return new SkiaImage(_bitmap.Copy(), _focusX, _focusY, _offsetX, _offsetY, _backgroundColor);
        // Zoom > 100 = zoom in: crop to a smaller source window then scale up to original size
        // Zoom < 100 = zoom out: scale down the image onto a canvas the original size (with transparent border)
        if (zoom > 100) {
            double factor = 100.0 / zoom;
            int cropW = Math.Max(1, (int)Math.Round(Width * factor));
            int cropH = Math.Max(1, (int)Math.Round(Height * factor));
            int fx = _focusX ?? Width / 2, fy = _focusY ?? Height / 2;
            int x = Math.Clamp(fx - cropW / 2, 0, Width - cropW);
            int y = Math.Clamp(fy - cropH / 2, 0, Height - cropH);
            using var cropped = new SKBitmap(cropW, cropH);
            using var c2 = new SKCanvas(cropped);
            c2.DrawBitmap(_bitmap, new SKRect(x, y, x + cropW, y + cropH), new SKRect(0, 0, cropW, cropH));
            return new SkiaImage(ResizeBitmap(cropped, Width, Height));
        } else {
            int scaledW = Math.Max(1, (int)Math.Round(Width * zoom / 100.0));
            int scaledH = Math.Max(1, (int)Math.Round(Height * zoom / 100.0));
            using var scaled = ResizeBitmap(_bitmap, scaledW, scaledH);
            var canvas = new SKBitmap(Width, Height);
            using var c2 = new SKCanvas(canvas);
            c2.Clear(ParseColor(_backgroundColor));
            c2.DrawBitmap(scaled, (Width - scaledW) / 2f, (Height - scaledH) / 2f);
            return new SkiaImage(canvas);
        }
    }

    public IImage Resize(int? width, int? height, ImageCropMode cropMode = ImageCropMode.Fill, string? backgroundColor = null, bool autoBackgroundColor = false) {
        int srcW = Width, srcH = Height;
        // Derive missing dimension preserving aspect ratio (unless Stretch)
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

        if (targetW == srcW && targetH == srcH) return new SkiaImage(_bitmap.Copy());

        var bg = ParseColor(backgroundColor);

        return cropMode switch {
            ImageCropMode.Stretch => new SkiaImage(ResizeBitmap(_bitmap, targetW, targetH)),
            ImageCropMode.Fill => new SkiaImage(ResizeAndCrop(_bitmap, targetW, targetH, _focusX, _focusY, _offsetX, _offsetY)),
            ImageCropMode.Fit => new SkiaImage(ResizeToFit(_bitmap, targetW, targetH, bg)),
            ImageCropMode.Auto => new SkiaImage(ResizeAuto(_bitmap, targetW, targetH, _focusX, _focusY, _offsetX, _offsetY, bg)),
            _ => new SkiaImage(ResizeAuto(_bitmap, targetW, targetH, _focusX, _focusY, _offsetX, _offsetY, bg)),
        };
    }

    // ── Colour adjustments ──────────────────────────────────────────────────

    public IImage AdjustBrightness(double brightness) => ApplyColorMatrix(ColorMatrix.ContrastBrightness(1f, (float)(brightness / 100.0)));
    public IImage AdjustContrast(double contrast) => ApplyColorMatrix(ColorMatrix.ContrastBrightness((float)((contrast + 100.0) / 100.0), 0f));
    public IImage AdjustSaturation(double saturation) => ApplyColorMatrix(ColorMatrix.Saturation((float)((saturation + 100.0) / 100.0)));
    public IImage AdjustHue(double hueShift) => ApplyColorMatrix(ColorMatrix.HueRotation((float)hueShift));

    public IImage AdjustSharpness(double sharpness) {
        if (sharpness == 0) return new SkiaImage(_bitmap.Copy());
        if (sharpness < 0) {
            // Gaussian blur: map -1..-100 → sigma 0.5..20
            float sigma = (float)(-sharpness / 100.0 * 19.5 + 0.5);
            using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma) };
            return DrawWithPaint(paint);
        }
        // Unsharp-mask sharpening: amount 0..1
        float amount = (float)(sharpness / 100.0);
        using var sharpenPaint = new SKPaint {
            ImageFilter = SKImageFilter.CreateMatrixConvolution(
            new SKSizeI(3, 3),
            [  0, -amount,        0,
              -amount, 1 + 4 * amount, -amount,
               0, -amount,        0 ],
            1f, 0f, new SKPointI(1, 1), SKShaderTileMode.Clamp, true)
        };
        return DrawWithPaint(sharpenPaint);
    }

    // ── Drawing ─────────────────────────────────────────────────────────────

    public IImage DrawLine(int x1, int y1, int x2, int y2, int width, string color) {
        var dst = new SKBitmap(Width, Height);
        using var c = new SKCanvas(dst);
        c.DrawBitmap(_bitmap, 0, 0);
        using var paint = new SKPaint { Color = ParseColor(color), StrokeWidth = width, IsAntialias = false, IsStroke = true };
        c.DrawLine(x1, y1, x2, y2, paint);
        return new SkiaImage(dst);
    }

    public IImage DrawText(int x, int y, string text, int fontSizeInPixels, string color, bool sansSerif) {
        var dst = new SKBitmap(Width, Height);
        using var c = new SKCanvas(dst);
        c.DrawBitmap(_bitmap, 0, 0);
        using var font = new SKFont(sansSerif ? SKTypeface.FromFamilyName("Arial") : SKTypeface.FromFamilyName("Georgia"), fontSizeInPixels);
        using var paint = new SKPaint { Color = ParseColor(color) };
        c.DrawText(text, x, y + fontSizeInPixels, font, paint);
        return new SkiaImage(dst);
    }

    public IImage DrawBox(int x1, int y1, int x2, int y2, int borderWidth, string borderColor, bool filled, string fillColor) {
        var dst = new SKBitmap(Width, Height);
        using var c = new SKCanvas(dst);
        c.DrawBitmap(_bitmap, 0, 0);
        var rect = new SKRect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
        if (filled) {
            using var fillPaint = new SKPaint { Color = ParseColor(fillColor), IsStroke = false };
            var inner = SKRect.Inflate(rect, -borderWidth, -borderWidth);
            if (inner.Width > 0 && inner.Height > 0) c.DrawRect(inner, fillPaint);
        }
        if (borderWidth > 0) {
            using var borderPaint = new SKPaint { Color = ParseColor(borderColor), StrokeWidth = borderWidth, IsStroke = true, IsAntialias = false };
            var borderRect = SKRect.Inflate(rect, -borderWidth / 2f, -borderWidth / 2f);
            c.DrawRect(borderRect, borderPaint);
        }
        return new SkiaImage(dst);
    }

    // ── Encode ──────────────────────────────────────────────────────────────

    public byte[] Encode(FileFormat format, int? quality = null) {
        var (skFormat, q) = ToSkiaFormat(format, quality);
        if (format == FileFormat.Avif) {
            using var ds = AvifEncoder.Encode(_bitmap, settings => {
                settings.PixelFormat = AvifPixelFormat.AVIF_PIXEL_FORMAT_YUV420;
                settings.CodecChoice = AvifCodecChoice.AVIF_CODEC_CHOICE_SVT;
            });
            return ds.MemorySpan.ToArray();
        } else {
            using var data = _bitmap.Encode(skFormat, q);
            if (data == null) throw new Exception("Unable to encode format: " + skFormat);
            return data.ToArray();
        }
    }

    public void Dispose() => _bitmap.Dispose();

    // ── Helpers ─────────────────────────────────────────────────────────────

    static SKBitmap ResizeBitmap(SKBitmap src, int w, int h) {
        var dst = new SKBitmap(w, h);
        src.ScalePixels(dst, SKSamplingOptions.Default);
        return dst;
    }

    static SKBitmap ResizeAndCrop(SKBitmap src, int targetW, int targetH, int? focusX, int? focusY, int? offsetX, int? offsetY) {
        double scale = Math.Max((double)targetW / src.Width, (double)targetH / src.Height);
        int scaledW = Math.Max(1, (int)Math.Round(src.Width * scale));
        int scaledH = Math.Max(1, (int)Math.Round(src.Height * scale));
        using var scaled = ResizeBitmap(src, scaledW, scaledH);
        // Map focus point into scaled space
        int fx = focusX.HasValue ? (int)Math.Round(focusX.Value * scale) : scaledW / 2;
        int fy = focusY.HasValue ? (int)Math.Round(focusY.Value * scale) : scaledH / 2;
        int x = Math.Clamp(fx - targetW / 2 + (offsetX ?? 0), 0, Math.Max(0, scaledW - targetW));
        int y = Math.Clamp(fy - targetH / 2 + (offsetY ?? 0), 0, Math.Max(0, scaledH - targetH));
        return CropBitmap(scaled, x, y, targetW, targetH);
    }

    static SKBitmap ResizeToFit(SKBitmap src, int canvasW, int canvasH, SKColor bg) {
        double scale = Math.Min((double)canvasW / src.Width, (double)canvasH / src.Height);
        int scaledW = Math.Max(1, (int)Math.Round(src.Width * scale));
        int scaledH = Math.Max(1, (int)Math.Round(src.Height * scale));
        using var scaled = ResizeBitmap(src, scaledW, scaledH);
        var canvas = new SKBitmap(canvasW, canvasH);
        using var c = new SKCanvas(canvas);
        c.Clear(bg);
        c.DrawBitmap(scaled, (canvasW - scaledW) / 2f, (canvasH - scaledH) / 2f);
        return canvas;
    }

    static SKBitmap ResizeAuto(SKBitmap src, int targetW, int targetH, int? focusX, int? focusY, int? offsetX, int? offsetY, SKColor bg) {
        // Auto: use Fit when edges are mostly uniform, otherwise Fill
        double canvasAspect = (double)targetW / targetH;
        double imgAspect = (double)src.Width / src.Height;
        bool useFit = Math.Abs(canvasAspect - imgAspect) < 0.05; // close enough aspect → fit
        return useFit
            ? ResizeToFit(src, targetW, targetH, bg)
            : ResizeAndCrop(src, targetW, targetH, focusX, focusY, offsetX, offsetY);
    }

    static SKBitmap CropBitmap(SKBitmap src, int x, int y, int w, int h) {
        var dst = new SKBitmap(w, h);
        using var c = new SKCanvas(dst);
        c.DrawBitmap(src, new SKRect(x, y, x + w, y + h), new SKRect(0, 0, w, h));
        return dst;
    }

    static SKBitmap Rotate90CW(SKBitmap src) {
        var dst = new SKBitmap(src.Height, src.Width);
        using var c = new SKCanvas(dst);
        c.Translate(dst.Width, 0);
        c.RotateDegrees(90);
        c.DrawBitmap(src, 0, 0);
        return dst;
    }

    static SKBitmap Rotate90CCW(SKBitmap src) {
        var dst = new SKBitmap(src.Height, src.Width);
        using var c = new SKCanvas(dst);
        c.Translate(0, dst.Height);
        c.RotateDegrees(-90);
        c.DrawBitmap(src, 0, 0);
        return dst;
    }

    static SKBitmap Rotate180(SKBitmap src) {
        var dst = new SKBitmap(src.Width, src.Height);
        using var c = new SKCanvas(dst);
        c.Translate(src.Width, src.Height);
        c.RotateDegrees(180);
        c.DrawBitmap(src, 0, 0);
        return dst;
    }

    IImage ApplyColorMatrix(float[] matrix) {
        using var filter = SKColorFilter.CreateColorMatrix(matrix);
        using var paint = new SKPaint { ColorFilter = filter };
        return DrawWithPaint(paint);
    }

    IImage DrawWithPaint(SKPaint paint) {
        var dst = new SKBitmap(Width, Height);
        using var c = new SKCanvas(dst);
        c.DrawBitmap(_bitmap, 0, 0, paint);
        return new SkiaImage(dst);
    }

    static SKColor ParseColor(string? hex) =>
        string.IsNullOrWhiteSpace(hex) ? SKColors.Transparent
        : SKColor.TryParse(hex, out var c) ? c : SKColors.Transparent;

    static (SKEncodedImageFormat format, int quality) ToSkiaFormat(FileFormat fmt, int? quality) => fmt switch {
        FileFormat.Jpeg => (SKEncodedImageFormat.Jpeg, quality ?? 85),
        FileFormat.Png => (SKEncodedImageFormat.Png, 100),
        FileFormat.Gif => (SKEncodedImageFormat.Gif, 100),
        FileFormat.Bmp => (SKEncodedImageFormat.Bmp, 100),
        FileFormat.Webp => (SKEncodedImageFormat.Webp, quality ?? 85),
        FileFormat.Avif => (SKEncodedImageFormat.Avif, quality ?? 85),
        _ => (SKEncodedImageFormat.Png, 100),
    };

    internal static SkiaImage Create(int width, int height) {
        var bitmap = new SKBitmap(width, height);
        return new SkiaImage(bitmap);
    }

    // ── Colour matrix helpers ────────────────────────────────────────────────
    // All matrices are 4×5 (row-major) as expected by SKColorFilter.CreateColorMatrix.

    static class ColorMatrix {
        static float[] Identity() => [
            1, 0, 0, 0, 0,
            0, 1, 0, 0, 0,
            0, 0, 1, 0, 0,
            0, 0, 0, 1, 0,
        ];

        /// <param name="contrast">1 = no change, 0 = grey, 2 = double contrast</param>
        /// <param name="brightness">0 = no change, 1 = white, -1 = black (pre-scaled from -1..1)</param>
        public static float[] ContrastBrightness(float contrast, float brightness) {
            float t = (1f - contrast) / 2f + brightness;
            return [
                contrast, 0, 0, 0, t,
                0, contrast, 0, 0, t,
                0, 0, contrast, 0, t,
                0, 0, 0,        1, 0,
            ];
        }

        /// <param name="s">1 = no change, 0 = greyscale, 2 = double saturation</param>
        public static float[] Saturation(float s) {
            float r = 0.213f * (1 - s), g = 0.715f * (1 - s), b = 0.072f * (1 - s);
            return [
                r + s,     g,     b, 0, 0,
                    r, g + s,     b, 0, 0,
                    r,     g, b + s, 0, 0,
                    0,     0,     0, 1, 0,
            ];
        }

        public static float[] HueRotation(float degrees) {
            float rad = degrees * MathF.PI / 180f;
            float cos = MathF.Cos(rad), sin = MathF.Sin(rad);
            return [
                0.213f + cos * 0.787f - sin * 0.213f,  0.715f - cos * 0.715f - sin * 0.715f,  0.072f - cos * 0.072f + sin * 0.928f, 0, 0,
                0.213f - cos * 0.213f + sin * 0.143f,  0.715f + cos * 0.285f + sin * 0.140f,  0.072f - cos * 0.072f - sin * 0.283f, 0, 0,
                0.213f - cos * 0.213f - sin * 0.787f,  0.715f - cos * 0.715f + sin * 0.715f,  0.072f + cos * 0.928f + sin * 0.072f, 0, 0,
                0, 0, 0, 1, 0,
            ];
        }
    }
}
