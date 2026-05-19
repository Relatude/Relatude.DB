using Relatude.DB.Common;

namespace Relatude.DB.FileConversion;


public interface IImage : IDisposable {

    public static IImage Adjust(IImage source, FileAdjustmentImage adj) {
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
        if (adj.Zoom is double zoom && zoom != 100)
            img = img.Zoom(zoom);

        // 4. Resize — proportions are preserved unless CropMode is Stretch
        if (adj.Width.HasValue || adj.Height.HasValue) {
            var cropMode = adj.CropMode ?? ImageCropMode.Fit;
            img = img.Resize(adj.Width, adj.Height, cropMode, adj.BackgroundColor, adj.AutoBackgroundColor ?? false);
        }

        // 5. Colour and tone adjustments (FileAdjustmentImage uses -100..100 / -180..180 ranges)
        if (adj.Brightness is double b && b != 0) img = img.AdjustBrightness(b);
        if (adj.Contrast  is double c && c != 0) img = img.AdjustContrast(c);
        if (adj.Saturation is double s && s != 0) img = img.AdjustSaturation(s);
        if (adj.HueShift  is double h && h != 0) img = img.AdjustHue(h);
        if (adj.Sharpness is double sh && sh != 0) img = img.AdjustSharpness(sh);

        return img;
    }

    int Width { get; }
    int Height { get; }

    /// <summary>Resize the canvas to the given dimensions.</summary>
    IImage Resize(int? width, int? height, ImageCropMode cropMode = ImageCropMode.Fill, string? backgroundColor = null, bool autoBackgroundColor = false);

    /// <summary>Zoom into/out of the image. 100 = 1:1, 200 = 2x, 50 = zoom out.</summary>
    IImage Zoom(double zoom);

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

}
