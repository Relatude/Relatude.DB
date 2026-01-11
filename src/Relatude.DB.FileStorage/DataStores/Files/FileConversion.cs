namespace Relatude.DB.DataStores.Files;
public enum ImageFormat {
    Unknown,
    Original,
    Jpeg,
    Png,
    Gif,
    Bmp,
    WebP,
}
public class ImageRequest {
    public int CanvasHeight { get; set; }
    public int CanvasWidth { get; set; }
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double Zoom { get; set; }
    public ImageFormat Format { get; set; }
}
internal class ImageAction {
}
