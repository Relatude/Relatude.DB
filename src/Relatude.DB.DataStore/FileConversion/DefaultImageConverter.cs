using Relatude.DB.Common;
using Relatude.DB.FileConverter;
using Relatude.DB.FileConversion.DefaultImage.NativeImageLib;

namespace Relatude.DB.FileConversion;

public class DefaultImageConverter : IFileConverter {
    public bool SupportsConversion(FileType inBase, FileFormat inDetailed, FileType outBase, FileFormat outDetailed) {
        if (inBase != FileType.Image || outBase != FileType.Image) return false;
        return inDetailed is FileFormat.Jpeg or FileFormat.Png or FileFormat.Gif or FileFormat.Bmp or FileFormat.Webp
            && outDetailed is FileFormat.Jpeg or FileFormat.Png or FileFormat.Gif or FileFormat.Bmp or FileFormat.Webp;
    }

    public Task<bool> CancelAsync(string key) => Task.FromResult(false);

    public Task<Stream> ConvertAsync(Stream input, FileConversionInfo info) {

        var image = PureImage.Load(input);
        var adj = info.IdWithAdjustment.Adjustment as FileAdjustmentImage;

        if (adj != null) {
            // Rotation
            if (adj.Rotation is double rot && rot != 0)
                image = image.Rotate(rot);

            // Resize / crop
            var cropMode = adj.CropMode ?? ImageCropMode.Fit;
            int targetW, targetH;
            bool onlyOneDim = (adj.Width == null) != (adj.Height == null);
            if (onlyOneDim && (cropMode == ImageCropMode.Fit || cropMode == ImageCropMode.Fill)) {
                // Only one dimension given: derive the other from image proportions
                if (adj.Width.HasValue) { targetW = adj.Width.Value; targetH = Math.Max(1, (int)Math.Round(targetW * (double)image.Height / image.Width)); } else { targetH = adj.Height!.Value; targetW = Math.Max(1, (int)Math.Round(targetH * (double)image.Width / image.Height)); }
            } else {
                targetW = adj.Width ?? (adj.Scale.HasValue ? (int)(image.Width * adj.Scale.Value / 100.0) : image.Width);
                targetH = adj.Height ?? (adj.Scale.HasValue ? (int)(image.Height * adj.Scale.Value / 100.0) : image.Height);
            }
            if (targetW != image.Width || targetH != image.Height) {
                image = cropMode switch {
                    ImageCropMode.Fill => ResizeAndCropFill(image, targetW, targetH),
                    ImageCropMode.Fit => ResizeToFitCanvas(image, targetW, targetH, ParseBackground(adj.BackgroundColor)),
                    _ => image.Resize(targetW, targetH)
                };
            }

            // Colour adjustments (values are -100..100, convert to -1..1)
            if (adj.Brightness is int b && b != 0) image = image.AdjustBrightness(b / 100.0);
            if (adj.Contrast is int c && c != 0) image = image.AdjustContrast(c / 100.0);
            if (adj.Saturation is int s && s != 0) image = image.AdjustSaturation(s / 100.0);
        }

        var outFormat = ToImageFormat(info.Formats.To);
        var saveOpts = new ImageSaveOptions { Quality = adj?.Quality ?? 90 };
        var outStream = new MemoryStream();
        ImageCodecs.FindEncoder(outFormat).Encode(image, outStream, saveOpts);
        outStream.Position = 0;
        return Task.FromResult<Stream>(outStream);
    }

    static PureImage ResizeToFitCanvas(PureImage image, int canvasW, int canvasH, ColorRgba bg) {
        double scale = Math.Min((double)canvasW / image.Width, (double)canvasH / image.Height);
        var scaled = image.Resize(Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale)));
        if (scaled.Width == canvasW && scaled.Height == canvasH) return scaled;
        // Composite scaled image centered onto canvas filled with background
        int offX = (canvasW - scaled.Width) / 2, offY = (canvasH - scaled.Height) / 2;
        var canvas = PureImage.Create(canvasW, canvasH, (x, y) => {
            int sx = x - offX, sy = y - offY;
            return sx >= 0 && sy >= 0 && sx < scaled.Width && sy < scaled.Height ? scaled[sx, sy] : bg;
        });
        return canvas;
    }

    static ColorRgba ParseBackground(string? hex) {
        if (string.IsNullOrWhiteSpace(hex)) return new ColorRgba(0, 0, 0, 0);
        var s = hex.TrimStart('#');
        if (s.Length == 6 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return new ColorRgba((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        if (s.Length == 8 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var rgba))
            return new ColorRgba((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);
        return new ColorRgba(0, 0, 0, 0);
    }

    static PureImage ResizeAndCropFill(PureImage image, int targetW, int targetH) {
        double scaleW = (double)targetW / image.Width;
        double scaleH = (double)targetH / image.Height;
        double scale = Math.Max(scaleW, scaleH);
        int scaledW = Math.Max(1, (int)Math.Round(image.Width * scale));
        int scaledH = Math.Max(1, (int)Math.Round(image.Height * scale));
        var resized = image.Resize(scaledW, scaledH);
        int x = (scaledW - targetW) / 2;
        int y = (scaledH - targetH) / 2;
        return resized.Crop(new RectangleI(x, y, targetW, targetH));
    }

    static ImageFormat ToImageFormat(FileFormat f) => f switch {
        FileFormat.Jpeg => ImageFormat.Jpeg,
        FileFormat.Png => ImageFormat.Png,
        FileFormat.Webp => ImageFormat.Webp,
        FileFormat.Bmp => ImageFormat.Bmp,
        _ => ImageFormat.Jpeg
    };

    public Stream GetProgressStream(FileValue fileValue, FileAdjustmentBase adj, FileConversionProgressInfo status) {
        var imgAdj = adj as FileAdjustmentImage;
        int w = imgAdj?.Width ?? (fileValue.Width > 0 ? fileValue.Width : 200);
        int h = imgAdj?.Height ?? (fileValue.Height > 0 ? fileValue.Height : 200);
        w = Math.Max(1, w); h = Math.Max(1, h);

        // Background: light grey
        var bg = new ColorRgba(220, 220, 220);
        // Progress bar: dark grey, fills bottom strip proportional to progress
        var barColor = new ColorRgba(100, 100, 100);
        int barH = Math.Max(4, h / 16);
        int barFill = (int)(w * Math.Clamp(status.ProgressPercentage / 100.0, 0, 1));

        var image = PureImage.Create(w, h, (x, y) => {
            if (y >= h - barH) return x < barFill ? barColor : new ColorRgba(180, 180, 180);
            return bg;
        });

        var outFormat = ToImageFormat(adj.RequestedFormat);
        var saveOpts = new ImageSaveOptions { Quality = 80 };
        var outStream = new MemoryStream();
        ImageCodecs.FindEncoder(outFormat).Encode(image, outStream, saveOpts);
        outStream.Position = 0;
        return outStream;
    }
}
