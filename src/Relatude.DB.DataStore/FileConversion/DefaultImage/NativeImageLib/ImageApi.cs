//namespace Relatude.DB.FileConversion.DefaultImage.NativeImageLib;

//public enum ImageFormat
//{
//    Unknown = 0,
//    Jpeg,
//    Png,
//    Webp,
//    Bmp
//}

//public enum ResizeKernel
//{
//    Nearest,
//    Bilinear,
//    Lanczos3
//}

//public readonly record struct ColorRgba(byte R, byte G, byte B, byte A = 255);

//public readonly record struct RectangleI(int X, int Y, int Width, int Height)
//{
//    public int Right => X + Width;
//    public int Bottom => Y + Height;

//    public void ValidateInside(int width, int height)
//    {
//        if (Width <= 0 || Height <= 0)
//        {
//            throw new ArgumentOutOfRangeException(nameof(Width), "Crop rectangle must have positive dimensions.");
//        }

//        if (X < 0 || Y < 0 || Right > width || Bottom > height)
//        {
//            throw new ArgumentOutOfRangeException(nameof(X), "Crop rectangle must be inside the image bounds.");
//        }
//    }
//}

//public sealed record ResizeOptions(int Width, int Height, ResizeKernel Kernel = ResizeKernel.Lanczos3)
//{
//    public void Validate()
//    {
//        if (Width <= 0 || Height <= 0)
//        {
//            throw new ArgumentOutOfRangeException(nameof(Width), "Resize dimensions must be positive.");
//        }
//    }
//}

//public sealed record ImageLoadOptions
//{
//    public RectangleI? Crop { get; init; }
//    public ResizeOptions? Resize { get; init; }
//}

//public sealed record ImageSaveOptions
//{
//    public RectangleI? Crop { get; init; }
//    public ResizeOptions? Resize { get; init; }

//    public int Quality { get; init; } = 90;
//    public int PngCompressionLevel { get; init; } = 6;
//}

//public interface IImage : IDisposable
//{
//    int Width { get; }
//    int Height { get; }

//    ColorRgba this[int x, int y] { get; set; }

//    IImage Clone();
//    IImage Crop(RectangleI rectangle);
//    IImage Resize(int width, int height, ResizeKernel kernel = ResizeKernel.Lanczos3);
//    IImage Resize(ResizeOptions options);

//    IImage AdjustBrightness(double amount);
//    IImage Brightness(double amount);
//    IImage AdjustSaturation(double amount);
//    IImage Saturation(double amount);
//    IImage AdjustContrast(double amount);
//    IImage Contrast(double amount);

//    IImage FlipHorizontal();
//    IImage FlipVertical();
//    IImage Invert();
//    IImage Inverse();
//    IImage Rotate90Clockwise();
//    IImage Rotate90CounterClockwise();
//    IImage Rotate180();
//    IImage Rotate(double degrees, ColorRgba background = default, ResizeKernel interpolation = ResizeKernel.Bilinear);
//    IImage Blur(double radius = 1);
//    IImage Sharpen(double amount = 1, double radius = 1);
//    IImage AdjustSharpness(double amount = 1, double radius = 1);
//    IImage Sharpness(double amount = 1, double radius = 1);

//    void Save(string path, ImageFormat format, ImageSaveOptions? options = null);
//    void Save(Stream stream, ImageFormat format, ImageSaveOptions? options = null);
//}

//public interface IImageBackend
//{
//    string Name { get; }

//    IImage Create(int width, int height, Func<int, int, ColorRgba> fill);
//    IImage Load(string path, ImageLoadOptions? options = null);
//    IImage Load(Stream stream, ImageLoadOptions? options = null);
//    ImageFormat DetectFormat(Stream stream);
//}
