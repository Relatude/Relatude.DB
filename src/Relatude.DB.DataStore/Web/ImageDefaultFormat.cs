using Relatude.DB.Common;

namespace Relatude.DB.Web;

/// <summary>The concrete format the adaptive <see cref="FileFormat.Image"/> resolves to for most images. See SettingsLocal.ImageDefaultFormat.</summary>
public enum ImageDefaultFormat {
    Jpeg,
    WebP,
    Png,
}
public static class ImageDefaultFormatExtensions {
    public static FileFormat ToFileFormat(this ImageDefaultFormat format) => format switch {
        ImageDefaultFormat.Jpeg => FileFormat.Jpeg,
        ImageDefaultFormat.WebP => FileFormat.Webp,
        ImageDefaultFormat.Png => FileFormat.Png,
        _ => FileFormat.Jpeg,
    };
}
