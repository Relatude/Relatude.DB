using Relatude.DB.Common;
using Relatude.DB.FileConverter;
using SkiaSharp;

namespace Relatude.DB.FileConversion.Images;

public static class SkiaLib {
    public static Stream Convert(Stream input, FileAdjustmentImage adj) {

        //Thread.Sleep(2000);

        using var original = SKBitmap.Decode(input) ?? throw new InvalidOperationException("Failed to decode image.");

        // Apply rotation to determine source dimensions
        double rotation = adj.Rotation ?? 0;
        bool swapDims = rotation == 90 || rotation == -90 || rotation == 270 || rotation == -270;
        int srcW = swapDims ? original.Height : original.Width;
        int srcH = swapDims ? original.Width : original.Height;

        // Resolve canvas size
        int canvasW = adj.Width ?? (adj.Scale.HasValue ? (int)(srcW * adj.Scale.Value / 100.0) : srcW);
        int canvasH = adj.Height.HasValue ? adj.Height.Value : (adj.Scale.HasValue ? (int)(srcH * adj.Scale.Value / 100.0) : (adj.Width.HasValue ? (int)((double)srcH / srcW * canvasW) : srcH));

        // Compute draw rect based on crop mode
        // (drawRect is used after rotation transform is established)
        using var surface = SKSurface.Create(new SKImageInfo(canvasW, canvasH)) ?? throw new InvalidOperationException("Failed to create surface.");
        var canvas = surface.Canvas;

        // Background color
        canvas.Clear(ParseColor(adj.BackgroundColor));

        using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };

        // Apply color filter
        var colorFilter = BuildColorFilter(adj);
        if (colorFilter != null) paint.ColorFilter = colorFilter;

        canvas.Save();

        // Apply rotation. For cardinal angles use translate+rotate so the full image
        // fills the (dimension-swapped) canvas without clipping. For arbitrary angles
        // rotate around the canvas centre (may clip corners).
        if (rotation != 0) {
            if (rotation == 90) { canvas.Translate(canvasW, 0); canvas.RotateDegrees(90); } else if (rotation == -90 || rotation == 270) { canvas.Translate(0, canvasH); canvas.RotateDegrees(-90); } else if (rotation == 180 || rotation == -180) { canvas.Translate(canvasW, canvasH); canvas.RotateDegrees(180); } else canvas.RotateDegrees((float)rotation, canvasW / 2f, canvasH / 2f);
        }

        // After a cardinal rotate+translate the draw coordinate space is the original
        // image orientation, so use the pre-swap dimensions.
        int drawW = swapDims ? original.Width : canvasW;
        int drawH = swapDims ? original.Height : canvasH;

        // Apply offset in source-image space (comment: "in reference to the original image")
        float offsetX = adj.OffsetX ?? 0;
        float offsetY = adj.OffsetY ?? 0;

        int focusX = adj.FocusX ?? original.Width / 2;
        int focusY = adj.FocusY ?? original.Height / 2;
        SKRect srcRect = ComputeSrcRect(original.Width, original.Height, drawW, drawH, focusX, focusY, adj);
        // Shift crop window by offset, clamped to image bounds
        float srcMaxX = original.Width - srcRect.Width, srcMaxY = original.Height - srcRect.Height;
        float ox = Math.Clamp(srcRect.Left + offsetX, 0, Math.Max(0, srcMaxX));
        float oy = Math.Clamp(srcRect.Top + offsetY, 0, Math.Max(0, srcMaxY));
        srcRect = new SKRect(ox, oy, ox + srcRect.Width, oy + srcRect.Height);

        SKRect dest = ComputeDrawRect(drawW, drawH, (int)srcRect.Width, (int)srcRect.Height, adj);

        // Zoom: >100 = zoom in (shrink srcRect, crops tighter into the image)
        //        <100 = zoom out (shrink dest, image appears smaller with background around it)
        if (adj.Zoom.HasValue && adj.Zoom.Value > 0 && adj.Zoom.Value != 100) {
            float zoomFactor = (float)(100.0 / adj.Zoom.Value);
            if (adj.Zoom.Value > 100) {
                // Zoom in: shrink the source crop window around the focus point
                float zoomedW = Math.Clamp(srcRect.Width * zoomFactor, 1, original.Width);
                float zoomedH = Math.Clamp(srcRect.Height * zoomFactor, 1, original.Height);
                float zx = Math.Clamp(focusX - zoomedW / 2, 0, original.Width - zoomedW);
                float zy = Math.Clamp(focusY - zoomedH / 2, 0, original.Height - zoomedH);
                srcRect = new SKRect(zx, zy, zx + zoomedW, zy + zoomedH);
            } else {
                // Zoom out: shrink the dest rect and centre it; background fills the remainder
                float zoomedW = dest.Width * (float)(adj.Zoom.Value / 100.0);
                float zoomedH = dest.Height * (float)(adj.Zoom.Value / 100.0);
                float dx = dest.Left + (dest.Width - zoomedW) / 2f;
                float dy = dest.Top + (dest.Height - zoomedH) / 2f;
                dest = new SKRect(dx, dy, dx + zoomedW, dy + zoomedH);
            }
        }

        canvas.DrawBitmap(original, srcRect, dest, paint);
        canvas.Restore();

        colorFilter?.Dispose();

        var format = adj.RequestedFormat;
        var (skFormat, quality) = GetEncodeFormat(format, adj.Quality);
        using var image = surface.Snapshot();
        using var encoded = image.Encode(skFormat, quality);
        var output = new MemoryStream();
        encoded.SaveTo(output);
        output.Position = 0;
        return output;
    }
    // Returns the dest rect on the draw surface for the given crop/fit mode.
    // For Fill/Auto the image covers the full surface. For Fit it is centred with letterboxing.
    static SKRect ComputeDrawRect(int surfaceW, int surfaceH, int cropW, int cropH, FileAdjustmentImage adj) {
        var cropMode = adj.CropMode ?? ImageCropMode.Auto;
        if (cropMode != ImageCropMode.Fit) return new SKRect(0, 0, surfaceW, surfaceH);

        // Scale to fit while preserving aspect ratio
        float scale = Math.Min((float)surfaceW / cropW, (float)surfaceH / cropH);
        float w = cropW * scale, h = cropH * scale;
        float x = (surfaceW - w) / 2f, y = (surfaceH - h) / 2f;
        return new SKRect(x, y, x + w, y + h);
    }
    static SKRect ComputeSrcRect(int imgW, int imgH, int canvasW, int canvasH, int focusX, int focusY, FileAdjustmentImage adj) {
        var cropMode = adj.CropMode ?? ImageCropMode.Auto;
        if (cropMode == ImageCropMode.None) return new SKRect(0, 0, imgW, imgH);

        double canvasAspect = (double)canvasW / canvasH;
        double imgAspect = (double)imgW / imgH;

        float cropW, cropH;
        if (cropMode == ImageCropMode.Fit || (cropMode == ImageCropMode.Auto && imgAspect <= canvasAspect)) {
            // fit: show all, letterbox
            cropW = imgW; cropH = imgH;
        } else {
            // fill / auto-fill: crop to fill canvas
            if (imgAspect > canvasAspect) {
                cropH = imgH; cropW = (float)(imgH * canvasAspect);
            } else {
                cropW = imgW; cropH = (float)(imgW / canvasAspect);
            }
        }

        float x = Math.Clamp(focusX - cropW / 2, 0, imgW - cropW);
        float y = Math.Clamp(focusY - cropH / 2, 0, imgH - cropH);
        return new SKRect(x, y, x + cropW, y + cropH);
    }
    static SKColor ParseColor(string? hex) {
        if (string.IsNullOrWhiteSpace(hex)) return SKColors.Transparent;
        return SKColor.TryParse(hex, out var c) ? c : SKColors.Transparent;
    }
    static SKColorFilter? BuildColorFilter(FileAdjustmentImage adj) {
        float brightness = (adj.Brightness ?? 0) / 100f;
        float contrast = adj.Contrast.HasValue ? (adj.Contrast.Value + 100) / 100f : 1f;
        float saturation = adj.Saturation.HasValue ? (adj.Saturation.Value + 100) / 100f : 1f;

        // Build a color matrix: contrast + brightness + saturation
        float[] m = ColorMatrix.Identity();
        if (adj.Contrast.HasValue || adj.Brightness.HasValue)
            m = ColorMatrix.Multiply(m, ColorMatrix.ContrastBrightness(contrast, brightness));
        if (adj.Saturation.HasValue)
            m = ColorMatrix.Multiply(m, ColorMatrix.Saturation(saturation));
        if (adj.HueShift.HasValue)
            m = ColorMatrix.Multiply(m, ColorMatrix.HueRotation(adj.HueShift.Value));
        if (adj.Colorize.HasValue)
            m = ColorMatrix.Multiply(m, ColorMatrix.Colorize(adj.Colorize.Value));

        bool isIdentity = m.SequenceEqual(ColorMatrix.Identity());
        return isIdentity ? null : SKColorFilter.CreateColorMatrix(m);
    }
    static (SKEncodedImageFormat format, int quality) GetEncodeFormat(Common.FileFormat fmt, int? quality) => fmt switch {
        Common.FileFormat.Jpeg => (SKEncodedImageFormat.Jpeg, quality ?? 85),
        Common.FileFormat.Png => (SKEncodedImageFormat.Png, 100),
        Common.FileFormat.Gif => (SKEncodedImageFormat.Gif, 100),
        Common.FileFormat.Bmp => (SKEncodedImageFormat.Bmp, 100),
        Common.FileFormat.Webp => (SKEncodedImageFormat.Webp, quality ?? 85),
        _ => (SKEncodedImageFormat.Png, 100),
    };
    static class ColorMatrix {
        public static float[] Identity() => [
            1, 0, 0, 0, 0,
            0, 1, 0, 0, 0,
            0, 0, 1, 0, 0,
            0, 0, 0, 1, 0
        ];

        public static float[] ContrastBrightness(float contrast, float brightness) {
            float t = (1f - contrast) / 2f + brightness;
            return [
                contrast, 0, 0, 0, t,
                0, contrast, 0, 0, t,
                0, 0, contrast, 0, t,
                0, 0, 0, 1, 0
            ];
        }

        public static float[] Saturation(float s) {
            float r = 0.213f * (1 - s), g = 0.715f * (1 - s), b = 0.072f * (1 - s);
            return [
                r + s,     g,     b, 0, 0,
                r,     g + s,     b, 0, 0,
                r,         g, b + s, 0, 0,
                0,         0,     0, 1, 0
            ];
        }

        public static float[] HueRotation(float degrees) {
            float rad = degrees * MathF.PI / 180f;
            float cos = MathF.Cos(rad), sin = MathF.Sin(rad);
            return [
                0.213f + cos * 0.787f - sin * 0.213f,  0.715f - cos * 0.715f - sin * 0.715f,  0.072f - cos * 0.072f + sin * 0.928f, 0, 0,
                0.213f - cos * 0.213f + sin * 0.143f,  0.715f + cos * 0.285f + sin * 0.140f,  0.072f - cos * 0.072f - sin * 0.283f, 0, 0,
                0.213f - cos * 0.213f - sin * 0.787f,  0.715f - cos * 0.715f + sin * 0.715f,  0.072f + cos * 0.928f + sin * 0.072f, 0, 0,
                0, 0, 0, 1, 0
            ];
        }

        public static float[] Colorize(int hue) {
            float rad = hue * MathF.PI / 180f;
            float r = 0.5f + 0.5f * MathF.Cos(rad);
            float g = 0.5f + 0.5f * MathF.Cos(rad - 2f * MathF.PI / 3f);
            float b = 0.5f + 0.5f * MathF.Cos(rad + 2f * MathF.PI / 3f);
            // Desaturate then tint
            float[] desat = Saturation(0);
            float[] tint = [
                r, 0, 0, 0, 0,
                0, g, 0, 0, 0,
                0, 0, b, 0, 0,
                0, 0, 0, 1, 0
            ];
            return Multiply(desat, tint);
        }

        public static float[] Multiply(float[] a, float[] b) {
            // 4x5 color matrix multiply.
            // Columns 0-3 are the colour transform; column 4 is the translation vector.
            // The implicit 5th row is [0,0,0,0,1], so b's translation propagates directly.
            float[] r = new float[20];
            for (int row = 0; row < 4; row++) {
                for (int col = 0; col < 4; col++) {
                    float sum = 0;
                    for (int k = 0; k < 4; k++) sum += a[row * 5 + k] * b[k * 5 + col];
                    r[row * 5 + col] = sum;
                }
                // Translation column: A * b_translation + a_translation
                float t = a[row * 5 + 4];
                for (int k = 0; k < 4; k++) t += a[row * 5 + k] * b[k * 5 + 4];
                r[row * 5 + 4] = t;
            }
            return r;
        }
    }

    public static Stream CreateMessageImage(string text, int w, int h, FileFormat format) {
        using var surface = SKSurface.Create(new SKImageInfo(w, h)) ?? throw new InvalidOperationException("Failed to create surface.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.LightGray);
        using var paint = new SKPaint {
            Color = SKColors.Black,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center,
            TextSize = 24
        };
        canvas.DrawText(text, w / 2f, h / 2f - (paint.FontMetrics.Ascent + paint.FontMetrics.Descent) / 2f, paint);
        var (skFormat, quality) = GetEncodeFormat(format, null);
        using var image = surface.Snapshot();
        using var encoded = image.Encode(skFormat, quality);
        var output = new MemoryStream();
        encoded.SaveTo(output);
        output.Position = 0;
        return output;

    }

}
