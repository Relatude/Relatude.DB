using Relatude.DB.Common;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Net.NetworkInformation;

namespace Relatude.DB.FileConversion;

/// <summary>What a resize needs beyond its dimensions: where to keep the crop, and what to pad with.</summary>
public readonly struct CropHints {
    public int? FocusX { get; init; }
    public int? FocusY { get; init; }
    public int? OffsetX { get; init; }
    public int? OffsetY { get; init; }
    public string? BackgroundColor { get; init; }
    public bool AutoBackgroundColor { get; init; }
    public static CropHints From(FileAdjustmentImage adj) => new() {
        FocusX = adj.FocusX, FocusY = adj.FocusY, OffsetX = adj.OffsetX, OffsetY = adj.OffsetY,
        BackgroundColor = adj.BackgroundColor, AutoBackgroundColor = adj.AutoBackgroundColor ?? false,
    };
    /// <summary>What is left once a crop has already consumed the focus and the offset.</summary>
    public static CropHints Background(FileAdjustmentImage adj) => new() {
        BackgroundColor = adj.BackgroundColor, AutoBackgroundColor = adj.AutoBackgroundColor ?? false,
    };
}
public static class IImageExt {
    public static IImage Adjust(this IImage source, FileAdjustmentImage adj) {
        var img = source;

        // 1. Rotation — applied first so subsequent resize works on the rotated canvas
        if (adj.Rotation is double rot && rot != 0)
            img = img.Rotate(rot);

        // 2. The part of the source to work from: the rectangle asked for, or the window a zoom
        //    means. Cropping to it first is what keeps any magnification to a single resample.
        var cropped = SourceRect(img.Width, img.Height, adj, out var cropX, out var cropY, out var cropW, out var cropH);
        if (cropped) {
            img = img.Crop(cropX, cropY, cropW, cropH);
            // a zoom with no size asked for keeps the canvas it had, which is what zoom has always meant
            if (adj.SourceWidth == null && adj.SourceHeight == null && adj.Width == null && adj.Height == null)
                img = img.Resize(source.Width, source.Height, ImageCropMode.Stretch);
        }

        // 3. Resize — proportions are preserved unless CropMode is Stretch
        if (adj.Width.HasValue || adj.Height.HasValue) {
            var cropMode = adj.CropMode ?? ImageCropMode.Fit;
            img = img.Resize(adj.Width, adj.Height, cropMode, cropped ? CropHints.Background(adj) : CropHints.From(adj));
        }

        // 5. Light/dark adaptation — before the fine-grained colour adjustments below, so that those
        //    apply to the final look rather than to the image as it happened to be stored
        var invert = adj.InvertLuminance == true;
        if (!invert && adj.AutoLightDarkMode is AutoLightDarkSwitch mode && mode != AutoLightDarkSwitch.None)
            invert = img.AnalyzeTone().ShouldInvertLuminance(mode);
        if (invert) img = img.InvertLuminance();

        // 6. Colour and tone adjustments (FileAdjustmentImage uses -100..100 / -180..180 ranges)
        if (adj.Brightness is double b && b != 0) img = img.AdjustBrightness(b);
        if (adj.Contrast is double c && c != 0) img = img.AdjustContrast(c);
        if (adj.Saturation is double s && s != 0) img = img.AdjustSaturation(s);
        if (adj.HueShift is double h && h != 0) img = img.AdjustHue(h);
        if (adj.Sharpness is double sh && sh != 0) img = img.AdjustSharpness(sh);

        return img;
    }

    /// <summary>
    /// The rectangle of a source of these dimensions that an adjustment works from, clamped to it:
    /// the one it names, or the one a zoom above 100% implies about the focus point. False when the
    /// whole source is the subject.
    /// </summary>
    public static bool SourceRect(int width, int height, FileAdjustmentImage adj, out int x, out int y, out int w, out int h) {
        if (adj.SourceWidth != null || adj.SourceHeight != null) {
            w = adj.SourceWidth ?? width;
            h = adj.SourceHeight ?? height;
            x = adj.SourceX ?? 0;
            y = adj.SourceY ?? 0;
        } else if (adj.Zoom is double zoom && zoom > 100) {
            w = Math.Max(1, (int)Math.Round(width * 100 / zoom));
            h = Math.Max(1, (int)Math.Round(height * 100 / zoom));
            x = (adj.FocusX ?? width / 2) - w / 2 + (adj.OffsetX ?? 0);
            y = (adj.FocusY ?? height / 2) - h / 2 + (adj.OffsetY ?? 0);
        } else {
            x = y = w = h = 0;
            return false;
        }
        w = Math.Clamp(w, 1, width);
        h = Math.Clamp(h, 1, height);
        x = Math.Clamp(x, 0, width - w);
        y = Math.Clamp(y, 0, height - h);
        return w != width || h != height || x != 0 || y != 0;
    }
    public static IImage GetStatusImage(this IImage img, List<string> text, string textColor, string fillColor) {
        var fontSizePx = 13;
        var borderColor = textColor;
        var leftMargin = 20;
        var topMargin = 20;
        var lineHeight = fontSizePx + 3;
        img = img.DrawBox(0, 0, img.Width - 2, img.Height - 2, 1, borderColor, true, fillColor);
        var estimatedMaxCharsPerLine = (int)((img.Width - leftMargin * 2) / (fontSizePx * 0.7)); // rough estimate based on font size
        text = textWrap(estimatedMaxCharsPerLine, text);
        foreach (var line in text) {
            img = img.DrawText(leftMargin, topMargin, line, fontSizePx, textColor, true);
            topMargin += string.IsNullOrEmpty(line) ? lineHeight / 2 : lineHeight;
        }
        return img;
    }
    static List<string> textWrap(int maxCharsPerLine, List<string> lines) {
        var result = new List<string>();
        foreach (var line in lines) {
            if (line.Length <= maxCharsPerLine) { result.Add(line); continue; }
            var words = line.Split(' ');
            var current = new System.Text.StringBuilder();
            foreach (var word in words) {
                if (word.Length > maxCharsPerLine) {
                    // Flush current buffer first
                    if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                    // Hard-break the oversized word
                    int i = 0;
                    while (i < word.Length) {
                        int take = Math.Min(maxCharsPerLine, word.Length - i);
                        result.Add(word.Substring(i, take));
                        i += take;
                    }
                    continue;
                }
                int needed = current.Length == 0 ? word.Length : current.Length + 1 + word.Length;
                if (needed > maxCharsPerLine) { result.Add(current.ToString()); current.Clear(); }
                if (current.Length > 0) current.Append(' ');
                current.Append(word);
            }
            if (current.Length > 0) result.Add(current.ToString());
        }
        return result;
    }

}
public interface IImage : IDisposable {

    int Width { get; }
    int Height { get; }
    string? GetJsonDetails();

    /// <summary>The given rectangle of this image, which is expected to be within it.</summary>
    IImage Crop(int x, int y, int width, int height);

    /// <summary>Resize the canvas to the given dimensions.</summary>
    IImage Resize(int? width, int? height, ImageCropMode cropMode = ImageCropMode.Fill, CropHints hints = default);

    /// <summary>Rotate the image by the given number of degrees.</summary>
    IImage Rotate(double degrees);

    /// <summary>Adjust brightness. Range: -100..100, 0 = no change.</summary>
    IImage AdjustBrightness(double brightness);

    /// <summary>Adjust contrast. Range: -100..100, 0 = no change.</summary>
    IImage AdjustContrast(double contrast);

    /// <summary>Adjust saturation. Range: -100..100, 0 = no change.</summary>
    IImage AdjustSaturation(double saturation);

    /// <summary>Shift the hue. Range: -180..180, 0 = no change.</summary>
    IImage AdjustHue(double hueShift);

    /// <summary>Adjust sharpness. Range: 0..100, 0 = no change.</summary>
    IImage AdjustSharpness(double sharpness);

    /// <summary>Invert the luminance: invert all colors, then shift the hue 180 degrees back so hues
    /// survive. Light becomes dark and dark becomes light, but red stays red.</summary>
    IImage InvertLuminance();

    /// <summary>Summarize the tone of the image, so a caller can decide whether inverting the
    /// luminance would improve it. See <see cref="AutoLightDarkSwitch"/>.</summary>
    ImageToneAnalysis AnalyzeTone();

    /// <summary>Encode and return the image in the requested format.</summary>
    byte[] Encode(FileFormat format, int? quality = null);

    /// <summary>Draw a line on the image.</summary>
    IImage DrawLine(int x1, int y1, int x2, int y2, int width, string color);

    /// <summary>Draw text on the image.</summary>
    IImage DrawText(int x, int y, string text, int fontSizeInPixels, string color, bool sansSerif);

    /// <summary>Draw a box on the image. (x1,y1) and (x2,y2) are the outer corners of the box including the border. </summary>
    IImage DrawBox(int x1, int y1, int x2, int y2, int borderWidth, string borderColor, bool filled, string fillColor);


}
