using Relatude.DB.Common;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Net.NetworkInformation;

namespace Relatude.DB.FileConversion;

public static class IImageExt {
    public static IImage Adjust(this IImage source, FileAdjustmentImage adj) {
        var img = source;

        // 1. Rotation — applied first so subsequent resize works on the rotated canvas
        if (adj.Rotation is double rot && rot != 0)
            img = img.Rotate(rot);

        // 2. Crop hints — influence how the implementation positions the image during resize
        if (adj.FocusX.HasValue || adj.FocusY.HasValue)
            img = img.SetFocus(adj.FocusX, adj.FocusY);
        if (adj.OffsetX.HasValue || adj.OffsetY.HasValue)
            img = img.SetOffset(adj.OffsetX, adj.OffsetY);

        // 3. Zoom — scale the viewport before resize
        if (adj.BackgroundColor != null || adj.AutoBackgroundColor == true)
            img = img.SetBackgroundColor(adj.BackgroundColor);
        if (adj.Zoom is double zoom && zoom != 100)
            img = img.Zoom(zoom);

        // 4. Resize — proportions are preserved unless CropMode is Stretch
        if (adj.Width.HasValue || adj.Height.HasValue) {
            var cropMode = adj.CropMode ?? ImageCropMode.Fit;
            img = img.Resize(adj.Width, adj.Height, cropMode, adj.BackgroundColor, adj.AutoBackgroundColor ?? false);
        }

        // 5. Colour and tone adjustments (FileAdjustmentImage uses -100..100 / -180..180 ranges)
        if (adj.Brightness is double b && b != 0) img = img.AdjustBrightness(b);
        if (adj.Contrast is double c && c != 0) img = img.AdjustContrast(c);
        if (adj.Saturation is double s && s != 0) img = img.AdjustSaturation(s);
        if (adj.HueShift is double h && h != 0) img = img.AdjustHue(h);
        if (adj.Sharpness is double sh && sh != 0) img = img.AdjustSharpness(sh);

        return img;
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

    /// <summary>Resize the canvas to the given dimensions.</summary>
    IImage Resize(int? width, int? height, ImageCropMode cropMode = ImageCropMode.Fill, string? backgroundColor = null, bool autoBackgroundColor = false);

    /// <summary>Zoom into/out of the image. 100 = 1:1, 200 = 2x, 50 = zoom out.</summary>
    IImage Zoom(double zoom);

    /// <summary>Set the background color used when padding (e.g. zoom-out, fit). Hex #RRGGBB or #RRGGBBAA.</summary>
    IImage SetBackgroundColor(string? color);

    /// <summary>Set the focus point (relative to the original image) used when cropping.</summary>
    IImage SetFocus(int? focusX, int? focusY);

    /// <summary>Apply an offset to the image (relative to the original image).</summary>
    IImage SetOffset(int? offsetX, int? offsetY);

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

    /// <summary>Encode and return the image in the requested format.</summary>
    byte[] Encode(FileFormat format, int? quality = null);

    /// <summary>Draw a line on the image.</summary>
    IImage DrawLine(int x1, int y1, int x2, int y2, int width, string color);

    /// <summary>Draw text on the image.</summary>
    IImage DrawText(int x, int y, string text, int fontSizeInPixels, string color, bool sansSerif);

    /// <summary>Draw a box on the image. (x1,y1) and (x2,y2) are the outer corners of the box including the border. </summary>
    IImage DrawBox(int x1, int y1, int x2, int y2, int borderWidth, string borderColor, bool filled, string fillColor);


}
