using Relatude.DB.Common;
using Relatude.DB.FileConversion.NativeImageEncoder;

namespace Relatude.DB.FileConversion;

/// <summary>Pure C# implementation. </summary>
public sealed class NativeImageI : IImage {
    readonly NativeRImage _image;
    readonly int? _focusX;
    readonly int? _focusY;
    readonly int? _offsetX;
    readonly int? _offsetY;

    public int Width => _image.Width;
    public int Height => _image.Height;

    public NativeImageI(NativeRImage image, int? focusX = null, int? focusY = null, int? offsetX = null, int? offsetY = null) {
        _image = image;
        _focusX = focusX;
        _focusY = focusY;
        _offsetX = offsetX;
        _offsetY = offsetY;
    }

    public static NativeImageI Load(Stream stream) => new(NativeRImage.Load(stream));

    // ── Crop hints ──────────────────────────────────────────────────────────

    public IImage SetFocus(int? focusX, int? focusY) => new NativeImageI(_image, focusX, focusY, _offsetX, _offsetY);
    public IImage SetOffset(int? offsetX, int? offsetY) => new NativeImageI(_image, _focusX, _focusY, offsetX, offsetY);

    // ── Geometry ────────────────────────────────────────────────────────────

    public IImage Rotate(double degrees) => new NativeImageI(_image.Rotate(degrees));

    public IImage Zoom(double zoom) {
        if (zoom <= 0 || zoom == 100) return new NativeImageI(_image, _focusX, _focusY, _offsetX, _offsetY);
        if (zoom > 100) {
            // Zoom in: crop a smaller source window centred on focus, then scale back up to original size
            double factor = 100.0 / zoom;
            int cropW = Math.Max(1, (int)Math.Round(Width * factor));
            int cropH = Math.Max(1, (int)Math.Round(Height * factor));
            int fx = _focusX ?? Width / 2;
            int fy = _focusY ?? Height / 2;
            int x = Math.Clamp(fx - cropW / 2, 0, Width - cropW);
            int y = Math.Clamp(fy - cropH / 2, 0, Height - cropH);
            var cropped = _image.Crop(new RectangleI(x, y, cropW, cropH));
            return new NativeImageI(cropped.Resize(Width, Height));
        } else {
            // Zoom out: shrink image and centre on a transparent canvas of the original size
            int scaledW = Math.Max(1, (int)Math.Round(Width * zoom / 100.0));
            int scaledH = Math.Max(1, (int)Math.Round(Height * zoom / 100.0));
            var scaled = _image.Resize(scaledW, scaledH);
            int offX = (Width - scaledW) / 2;
            int offY = (Height - scaledH) / 2;
            var canvas = NativeRImage.Create(Width, Height, (px, py) => {
                int sx = px - offX, sy = py - offY;
                return sx >= 0 && sy >= 0 && sx < scaledW && sy < scaledH ? scaled[sx, sy] : new ColorRgba(0, 0, 0, 0);
            });
            return new NativeImageI(canvas);
        }
    }

    public IImage Resize(int? width, int? height, ImageCropMode cropMode = ImageCropMode.Fill, string? backgroundColor = null, bool autoBackgroundColor = false) {
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

        if (targetW == srcW && targetH == srcH) return new NativeImageI(_image);

        var bg = ParseBackground(backgroundColor);

        return cropMode switch {
            ImageCropMode.Stretch => new NativeImageI(_image.Resize(targetW, targetH)),
            ImageCropMode.Fill => new NativeImageI(ResizeAndCrop(_image, targetW, targetH, _focusX, _focusY, _offsetX, _offsetY)),
            ImageCropMode.Fit => new NativeImageI(ResizeToFit(_image, targetW, targetH, bg)),
            _ => new NativeImageI(ResizeAuto(_image, targetW, targetH, _focusX, _focusY, _offsetX, _offsetY, bg)),
        };
    }

    // ── Colour adjustments ──────────────────────────────────────────────────
    // PureImage ranges: brightness amount → offset = amount*255; contrast factor = Max(0, 1+amount);
    // IImage ranges: -100..100.  Divide by 100 to bridge them.

    public IImage AdjustBrightness(double brightness) => new NativeImageI(_image.AdjustBrightness(brightness / 100.0));
    public IImage AdjustContrast(double contrast) => new NativeImageI(_image.AdjustContrast(contrast / 100.0));
    public IImage AdjustSaturation(double saturation) => new NativeImageI(_image.AdjustSaturation(saturation / 100.0));
    public IImage AdjustSharpness(double sharpness) => new NativeImageI(_image.AdjustSharpness(sharpness / 100.0));

    public IImage AdjustHue(double hueShift) => new NativeImageI(_image.AdjustHue(hueShift));

    // ── Encode

    public byte[] Encode(FileFormat format, int? quality = null) {
        var nativeFormat = ToNativeFormat(format);
        var opts = new ImageSaveOptions { Quality = quality ?? 90 };
        using var ms = new MemoryStream();
        _image.Save(ms, nativeFormat, opts);
        return ms.ToArray();
    }

    public void Dispose() { /* PureImage is not IDisposable — nothing to release */ }

    // ── Resize helpers ──────────────────────────────────────────────────────

    static NativeRImage ResizeAndCrop(NativeRImage src, int targetW, int targetH, int? focusX, int? focusY, int? offsetX, int? offsetY) {
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

    static NativeRImage ResizeToFit(NativeRImage src, int canvasW, int canvasH, ColorRgba bg) {
        double scale = Math.Min((double)canvasW / src.Width, (double)canvasH / src.Height);
        int scaledW = Math.Max(1, (int)Math.Round(src.Width * scale));
        int scaledH = Math.Max(1, (int)Math.Round(src.Height * scale));
        var scaled = src.Resize(scaledW, scaledH);
        if (scaledW == canvasW && scaledH == canvasH) return scaled;
        int offX = (canvasW - scaledW) / 2, offY = (canvasH - scaledH) / 2;
        return NativeRImage.Create(canvasW, canvasH, (x, y) => {
            int sx = x - offX, sy = y - offY;
            return sx >= 0 && sy >= 0 && sx < scaledW && sy < scaledH ? scaled[sx, sy] : bg;
        });
    }

    static NativeRImage ResizeAuto(NativeRImage src, int targetW, int targetH, int? focusX, int? focusY, int? offsetX, int? offsetY, ColorRgba bg) {
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
        _ => ImageFormat.Png,
    };
}
