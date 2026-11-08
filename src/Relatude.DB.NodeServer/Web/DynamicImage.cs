using Microsoft.AspNetCore.Html;
using Relatude.DB.FileConversion;
using Relatude.DB.Nodes;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

// In the namespace of FileValue, so the method shows wherever a file value is used.
namespace Relatude.DB.Common;

/// <summary>How <see cref="DynamicImageExtensions.GetDynamicImageHtml"/> writes a picture.</summary>
public sealed record DynamicImageOptions {
    /// <summary>
    /// What the server does to the picture before it is sent, as for <c>NodeStore.GetUrl(file, adjustment)</c>: format,
    /// quality, brightness, contrast and the other colour steps, a rotation, a source rectangle or a zoom. Its width and
    /// height are not used, as the file is the picture fitted inside the maximum size. Its crop mode, focus point
    /// (<c>FocusX</c>, <c>FocusY</c>, <c>OffsetX</c>, <c>OffsetY</c>) and background colour override the picture's own, as
    /// they would in a URL, and are applied in the browser. <c>AutoBackgroundColor</c> is not used.
    /// </summary>
    public FileAdjustmentImage? Adjustment { get; init; }
    /// <summary>The crop mode when neither <see cref="Adjustment"/> nor the picture names one. Fit, the engine's, when null.</summary>
    public ImageCropMode? DefaultCropMode { get; init; }
    /// <summary>The picture's alternative text. Null takes the file's description, else its title, else none.</summary>
    public string? Alt { get; init; }
    /// <summary>Classes of the box, after <c>relatude-image</c>, which it always has.</summary>
    public string? CssClass { get; init; }
    /// <summary>
    /// CSS put last in the box's style, so it can override the box's size: <c>"aspect-ratio:4/3"</c> or <c>"height:240px"</c>
    /// when the element the box is in does not give it its size.
    /// </summary>
    public string? Style { get; init; }
    /// <summary>More attributes of the box (not class or style). An attribute whose value is null is left out.</summary>
    public IReadOnlyDictionary<string, string?>? Attributes { get; init; }
    /// <summary>Whether the browser loads the picture only as it comes near the screen (<c>loading="lazy"</c>). True by default.</summary>
    public bool Lazy { get; init; } = true;
    /// <summary>Whether the picture's URL is absolute.</summary>
    public bool Absolute { get; init; }
}

public static class DynamicImageExtensions {
    /// <summary>
    /// HTML that shows the picture cropped to whatever size the page gives it, as the server would crop it to that size:
    /// by the picture's crop mode (Fill, Fit, Stretch or Auto; Fit when none is set), about its focus point, over its
    /// background colour. For a template whose picture changes shape with the browser's width, where a URL with a size
    /// would be the right crop for one shape only.
    /// <para>
    /// The HTML is a box, <c>&lt;span class="relatude-image"&gt;</c>, holding the picture. The box fills the element it is put
    /// in, which gives it its size (an element with a width and a height or an aspect ratio, a grid cell, a flex item that
    /// stretches); in an element with no height of its own it takes the picture's proportions, and nothing is cut. The crop
    /// is CSS alone: the box is a size container, and the picture is placed by the same arithmetic as the server's, written
    /// in container query units. No script runs.
    /// </para>
    /// <para>
    /// The file sent is the picture uncropped, fitted inside <paramref name="maxWidthPx"/> × <paramref name="maxHeightPx"/>
    /// (proportions kept, never enlarged): a limit on the file, not on what is shown. The crop is made once, in the browser,
    /// from the picture's proportions and the focus point as a share of its width and height, so the focus is where it was
    /// whatever size the file was made at.
    /// </para>
    /// </summary>
    /// <param name="file">A picture. Anything else, or an empty value, gives no HTML.</param>
    /// <param name="db">The store the picture is read with, which makes its URL: the request's, so a preview shows its own version.</param>
    /// <param name="maxWidthPx">The widest the file sent may be; 0 for no limit.</param>
    /// <param name="maxHeightPx">The highest the file sent may be; 0 for no limit.</param>
    /// <param name="options">An adjustment for the server, a crop mode for pictures that name none, and the HTML's attributes.</param>
    public static HtmlString GetDynamicImageHtml(this FileValue? file, NodeStore db, int maxWidthPx = 1000, int maxHeightPx = 1000, DynamicImageOptions? options = null) {
        ArgumentNullException.ThrowIfNull(db);
        if (file == null || file.IsEmpty || file.PropertyPath == null || file.FileType != FileType.Image) return HtmlString.Empty;
        options ??= new();
        var picture = DynamicImage.Plan(file, maxWidthPx, maxHeightPx, options);
        var url = picture.Request == null ? db.GetUrl(file, options.Absolute) : db.GetUrl(file, picture.Request, options.Absolute);
        var meta = file.GetMeta();
        var alt = options.Alt ?? (!string.IsNullOrWhiteSpace(meta.Description) ? meta.Description : meta.Title) ?? "";
        return new HtmlString(DynamicImage.Html(picture, url, alt, options));
    }
}

/// <summary>
/// What a dynamic picture is: the picture the browser crops (what the server makes of the file before its size: a turn,
/// a source rectangle), its focus point as shares of its width and height, the crop mode and background, and the request
/// for the file.
/// </summary>
/// <param name="Width">The width of the picture the browser crops, in the file's pixels after a turn or source rectangle; 0 when not known.</param>
/// <param name="Height">Its height; 0 when not known.</param>
/// <param name="FocusX">The point Fill keeps in the middle, as a share of the width.</param>
/// <param name="FocusY">The same, of the height.</param>
/// <param name="Mode">The crop mode; Auto is decided in the browser, by the shape of the box.</param>
/// <param name="Background">The background as a CSS colour, or null for none.</param>
/// <param name="Request">What the file is asked for with; null for the file as it is (a drawing the browser draws).</param>
/// <param name="FileWidth">The width of the file sent; 0 when not known.</param>
/// <param name="FileHeight">Its height; 0 when not known.</param>
internal sealed record DynamicPicture(int Width, int Height, double FocusX, double FocusY, ImageCropMode Mode, string? Background,
    FileAdjustmentImage? Request, int FileWidth, int FileHeight);

internal static partial class DynamicImage {
    internal static DynamicPicture Plan(FileValue file, int maxWidthPx, int maxHeightPx, DynamicImageOptions options) {
        var meta = file.GetMeta<ImageFileMeta>();
        var asked = options.Adjustment == null ? new FileAdjustmentImage() : (FileAdjustmentImage)options.Adjustment.Normalized();
        var mode = asked.CropMode ?? meta?.CropMode ?? options.DefaultCropMode ?? ImageCropMode.Fit;
        if (!Enum.IsDefined(mode)) mode = ImageCropMode.Fit;
        // a colour the adjustment names, even one wholly see-through, is used over the picture's, as in a URL
        var background = cssColor(asked.BackgroundColor ?? meta?.BackgroundColor, out var opaque);
        var pictureHasBackground = IImageExt.IsVisibleColor(meta?.BackgroundColor);
        var turn = asked.Rotation ?? 0;
        var quarterTurn = Math.Abs(turn / 90 - Math.Round(turn / 90)) < 1e-9;

        // the geometry the server makes before the size, in the order IImageExt.Adjust makes it: the turn, then the rectangle
        int width = 0, height = 0;
        double focusX = .5, focusY = .5;
        bool cropped = false;
        int rx = 0, ry = 0, rw = 0, rh = 0;
        if (file.Width > 0 && file.Height > 0) {
            var (w1, h1) = IImageExt.TurnedSize(file.Width, file.Height, turn);
            // the adjustment's focus is in the pixels of the turned picture, as the engine reads it; the picture's own is in
            // the file's, and turns with it. Like the crop mode, the picture's is used where the adjustment names none, as
            // the store uses it (DataStoreLocal.applyPictureDefaults).
            double? px = null, py = null;
            if (meta?.FocusPoint is { } point && IImageExt.TurnedPoint(point.X, point.Y, file.Width, file.Height, turn) is { } turned) (px, py) = turned;
            double? fx = asked.FocusX ?? px, fy = asked.FocusY ?? py;
            var rect = (FileAdjustmentImage)asked.Normalized();
            rect.FocusX = fx is double x ? (int)Math.Round(x) : null;
            rect.FocusY = fy is double y ? (int)Math.Round(y) : null;
            cropped = IImageExt.SourceRect(w1, h1, rect, out rx, out ry, out rw, out rh);
            if (cropped) {
                // the rectangle has taken the focus and the offset: what is left is cropped about its middle, as on the server
                (width, height) = (rw, rh);
            } else {
                (width, height) = (w1, h1);
                focusX = Math.Clamp(((fx ?? w1 / 2.0) + (asked.OffsetX ?? 0)) / w1, 0, 1);
                focusY = Math.Clamp(((fy ?? h1 / 2.0) + (asked.OffsetY ?? 0)) / h1, 0, 1);
            }
        }

        // what the server is asked for: the picture uncropped, fitted inside the maximum, with what the adjustment asks of it
        var work = (FileAdjustmentImage)asked.Normalized();
        work.Width = work.Height = null;
        work.CropMode = null;
        work.FocusX = work.FocusY = work.OffsetX = work.OffsetY = null;
        work.BackgroundColor = null;
        work.AutoBackgroundColor = null;
        if (file.Format == FileFormat.Svg && work.IsPlainRequest()) {
            // a drawing, sharp at any size: the browser draws it as it is
            return new(width, height, focusX, focusY, mode, background, null, width, height);
        }
        var request = work;
        if (cropped) (request.SourceX, request.SourceY, request.SourceWidth, request.SourceHeight, request.Zoom) = (rx, ry, rw, rh, null);
        else (request.SourceX, request.SourceY, request.SourceWidth, request.SourceHeight, request.Zoom) = (null, null, null, null, null);
        int fileWidth = 0, fileHeight = 0;
        if (width > 0 && height > 0) {
            var scale = 1.0;
            if (maxWidthPx > 0) scale = Math.Min(scale, (double)maxWidthPx / width);
            if (maxHeightPx > 0) scale = Math.Min(scale, (double)maxHeightPx / height);
            fileWidth = Math.Max(1, (int)Math.Round(width * scale));
            fileHeight = Math.Max(1, (int)Math.Round(fileWidth * (double)height / width)); // as NativeImage.Resize makes the other side
            request.Width = fileWidth;
        }
        // The background goes under the whole box in the browser, the picture's see-through parts included. A background
        // that shows through nothing is left alone; an opaque one may as well be under the picture in the file too, which
        // then needs no transparency; one partly see-through must not be laid on twice, so the picture's default is held off.
        var mayShowThrough = file.Format != FileFormat.Jpeg || !quarterTurn;
        if (!mayShowThrough) request.BackgroundColor = null;
        else if (opaque) request.BackgroundColor = background;
        else request.BackgroundColor = pictureHasBackground ? "#00000000" : null;
        // what may be see-through is sent in a format that keeps it: the adaptive default may be JPEG. A gif stays a gif.
        if (request.RequestedFormat is FileFormat.Image or FileFormat.Unknown && mayShowThrough && !opaque && file.Format != FileFormat.Gif)
            request.RequestedFormat = FileFormat.Webp;
        return new(width, height, focusX, focusY, mode, background, request.IsPlainRequest() ? null : request, fileWidth, fileHeight);
    }

    internal static string Html(DynamicPicture p, string url, string alt, DynamicImageOptions options) {
        var known = p.Width > 0 && p.Height > 0;
        var box = new StringBuilder("display:block;position:relative;overflow:hidden;width:100%;height:100%;");
        // the picture's proportions where nothing gives the box a height; a size container, so the picture is placed by its size
        if (known) box.Append("aspect-ratio:").Append(p.Width).Append(" / ").Append(p.Height).Append(";container-type:size;");
        if (p.Background != null) box.Append("background-color:").Append(p.Background).Append(';');
        if (!string.IsNullOrWhiteSpace(options.Style)) box.Append(options.Style.Trim());

        var html = new StringBuilder("<span class=\"relatude-image");
        if (!string.IsNullOrWhiteSpace(options.CssClass)) html.Append(' ').Append(encode(options.CssClass.Trim()));
        html.Append('"');
        if (options.Attributes != null) {
            foreach (var (name, value) in options.Attributes) {
                if (!attributeName().IsMatch(name)) throw new ArgumentException("\"" + name + "\" is not an attribute name.", nameof(options));
                if (name.Equals("class", StringComparison.OrdinalIgnoreCase) || name.Equals("style", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("The box's class and style are given by CssClass and Style.", nameof(options));
                if (value != null) html.Append(' ').Append(name).Append("=\"").Append(encode(value)).Append('"');
            }
        }
        html.Append(" style=\"").Append(encode(box.ToString())).Append("\">");
        html.Append("<img src=\"").Append(encode(url)).Append("\" alt=\"").Append(encode(alt)).Append('"');
        if (p.FileWidth > 0 && p.FileHeight > 0) html.Append(" width=\"").Append(p.FileWidth).Append("\" height=\"").Append(p.FileHeight).Append('"');
        if (options.Lazy) html.Append(" loading=\"lazy\"");
        html.Append(" decoding=\"async\" style=\"").Append(encode(PictureStyle(p))).Append("\"></span>");
        return html.ToString();
    }

    /// <summary>
    /// The picture's style: its size and place in the box, by the server's arithmetic (NativeImage.Resize), with the box's
    /// width as 100cqw and its height as 100cqh. Each value is a whole expression, never a custom property, so a browser
    /// without container query units drops just those declarations and keeps the ones before them: the picture filling the
    /// box by object-fit, about the focus point.
    /// </summary>
    internal static string PictureStyle(DynamicPicture p) {
        const string reset = "margin:0;padding:0;border:0;min-width:0;min-height:0;max-width:none;max-height:none;";
        var fit = p.Mode switch {
            ImageCropMode.Fit => "object-fit:contain;object-position:50% 50%;",
            ImageCropMode.Stretch => "object-fit:fill;",
            _ => "object-fit:cover;object-position:" + number(p.FocusX * 100) + "% " + number(p.FocusY * 100) + "%;",
        };
        // with no size known nothing can be worked out: the picture fills the box, and gives it its height where it has none.
        // Its least and most height are the box's, so a box held to a height by max-height crops it by object-fit too.
        if (p.Width <= 0 || p.Height <= 0)
            return "display:block;margin:0;padding:0;border:0;min-width:0;min-height:inherit;max-width:none;max-height:inherit;width:100%;height:100%;" + fit;
        var css = "position:absolute;display:block;" + reset + "left:0;top:0;width:100%;height:100%;" + fit;
        if (p.Mode == ImageCropMode.Stretch) return css;

        var r = number((double)p.Width / p.Height);
        var ir = number((double)p.Height / p.Width);
        var fx = number(p.FocusX);
        var fy = number(p.FocusY);
        // Fill: scaled to cover the box, the focus point in its middle as far as the picture reaches
        var coverW = $"max(100cqw,100cqh*{r})";
        var coverH = $"max(100cqh,100cqw*{ir})";
        var coverLeft = $"clamp(100cqw - {coverW},50cqw - {coverW}*{fx},0px)";
        var coverTop = $"clamp(100cqh - {coverH},50cqh - {coverH}*{fy},0px)";
        // Fit: scaled to fit inside the box, in its middle, the background around it
        var fitW = $"min(100cqw,100cqh*{r})";
        var fitH = $"min(100cqh,100cqw*{ir})";
        var fitLeft = $"(100cqw - {fitW})/2";
        var fitTop = $"(100cqh - {fitH})/2";
        return p.Mode switch {
            ImageCropMode.Fill => css + $"width:{coverW};height:{coverH};left:{coverLeft};top:{coverTop};",
            ImageCropMode.Fit => css + $"width:{fitW};height:{fitH};left:calc({fitLeft});top:calc({fitTop});",
            // Auto fits a picture within 5% of the box's shape and fills any other (NativeImage.ResizeAuto). A length can be
            // compared with nothing in CSS, so the choice is a length that is 0 for Fill and very large for Fit: the box
            // and the picture are of a shape when 0.95·boxW < ratio·boxH < 1.05·boxW, the smaller of the two margins
            // positive. Fill's size less it, and Fill's place plus it, then give way to Fit's.
            _ => css + autoStyle(coverW, coverH, coverLeft, coverTop, fitW, fitH, fitLeft, fitTop,
                $"clamp(0px,min(100cqh*{r} - 95cqw,105cqw - 100cqh*{r})*1000000,1000000px)"),
        };
    }

    static string autoStyle(string coverW, string coverH, string coverLeft, string coverTop, string fitW, string fitH, string fitLeft, string fitTop, string fits) =>
        $"width:max({fitW},{coverW} - {fits});height:max({fitH},{coverH} - {fits});"
        + $"left:min({fitLeft},{coverLeft} + {fits});top:min({fitTop},{coverTop} + {fits});";

    // "#rrggbb" or "#rrggbbaa" for a colour that shows, else null
    static string? cssColor(string? hex, out bool opaque) {
        opaque = false;
        if (!IImageExt.TryParseHexColor(hex, out var r, out var g, out var b, out var a) || a == 0) return null;
        opaque = a == 255;
        return opaque ? $"#{r:x2}{g:x2}{b:x2}" : $"#{r:x2}{g:x2}{b:x2}{a:x2}";
    }

    static string number(double value) => Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);

    static string encode(string value) => WebUtility.HtmlEncode(value);

    [GeneratedRegex("^[A-Za-z_:][-A-Za-z0-9_:.]*$")]
    private static partial Regex attributeName();
}
