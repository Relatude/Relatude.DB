using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Relatude.DB.FileConversion.NativeImageEncoder;

public sealed class NativeRImage
{
    private const int LinearShift = 14;
    private const int LinearOne = 1 << LinearShift;
    private const int LinearHalf = LinearOne >> 1;
    private const int ResampleShift = 14;
    private const int ResampleOne = 1 << ResampleShift;
    private const int ResampleHalf = ResampleOne >> 1;

    private readonly byte[] _rgba;

    public NativeRImage(int width, int height)
        : this(width, height, new byte[CheckedPixelByteCount(width, height)])
    {
    }

    public NativeRImage(int width, int height, byte[] rgba)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        }

        if (rgba.Length != CheckedPixelByteCount(width, height))
        {
            throw new ArgumentException("Pixel buffer length must equal width * height * 4.", nameof(rgba));
        }

        Width = width;
        Height = height;
        _rgba = rgba;
    }

    public int Width { get; }
    public int Height { get; }
    public ReadOnlySpan<byte> Pixels => _rgba;
    public Span<byte> MutablePixels => _rgba;

    public static NativeRImage Load(string path, ImageLoadOptions? options = null)
    {
        byte[] data = File.ReadAllBytes(path);
        NativeRImage image = ImageCodecs.FindDecoder(data).Decode(data);
        return image.Apply(options);
    }

    public static NativeRImage Load(Stream stream, ImageLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] data = ReadStreamToEnd(stream);
        NativeRImage image = ImageCodecs.FindDecoder(data).Decode(data);
        return image.Apply(options);
    }

    public static NativeRImage Create(int width, int height, Func<int, int, ColorRgba> fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        byte[] pixels = new byte[CheckedPixelByteCount(width, height)];
        for (int y = 0; y < height; y++)
        {
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                ColorRgba color = fill(x, y);
                int offset = row + x * 4;
                pixels[offset] = color.R;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.B;
                pixels[offset + 3] = color.A;
            }
        }

        return new NativeRImage(width, height, pixels);
    }

    public static ImageFormat DetectFormat(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[32];
        long original = stream.CanSeek ? stream.Position : 0;
        int read = stream.Read(header);
        if (stream.CanSeek)
        {
            stream.Position = original;
        }

        return ImageCodecs.DetectFormat(header[..read]);
    }

    public ColorRgba this[int x, int y]
    {
        get
        {
            ValidateCoordinates(x, y);
            int offset = PixelOffset(x, y);
            return new ColorRgba(_rgba[offset], _rgba[offset + 1], _rgba[offset + 2], _rgba[offset + 3]);
        }
        set
        {
            ValidateCoordinates(x, y);
            int offset = PixelOffset(x, y);
            _rgba[offset] = value.R;
            _rgba[offset + 1] = value.G;
            _rgba[offset + 2] = value.B;
            _rgba[offset + 3] = value.A;
        }
    }

    public NativeRImage Clone()
    {
        return new NativeRImage(Width, Height, (byte[])_rgba.Clone());
    }

    public NativeRImage Crop(RectangleI rectangle)
    {
        rectangle.ValidateInside(Width, Height);
        if (rectangle.X == 0 && rectangle.Y == 0 && rectangle.Width == Width && rectangle.Height == Height)
        {
            return Clone();
        }

        byte[] pixels = new byte[CheckedPixelByteCount(rectangle.Width, rectangle.Height)];
        int destinationStride = rectangle.Width * 4;
        for (int y = 0; y < rectangle.Height; y++)
        {
            Buffer.BlockCopy(
                _rgba,
                ((rectangle.Y + y) * Width + rectangle.X) * 4,
                pixels,
                y * destinationStride,
                destinationStride);
        }

        return new NativeRImage(rectangle.Width, rectangle.Height, pixels);
    }

    public NativeRImage Resize(int width, int height, ResizeKernel kernel = ResizeKernel.Lanczos3)
    {
        return Resize(new ResizeOptions(width, height, kernel));
    }

    public NativeRImage Resize(ResizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (options.Width == Width && options.Height == Height)
        {
            return Clone();
        }

        return options.Kernel switch
        {
            ResizeKernel.Nearest => ResizeNearest(options.Width, options.Height),
            ResizeKernel.Bilinear => ResizeBilinear(options.Width, options.Height),
            _ => ResizeLanczos(options.Width, options.Height)
        };
    }

    public NativeRImage AdjustBrightness(double amount)
    {
        ValidateFinite(amount, nameof(amount));
        int offset = (int)Math.Round(amount * 255);
        if (offset == 0) return Clone();
        byte[] destination = new byte[_rgba.Length];
        ApplyRgbLut(destination, BuildOffsetLut(offset));
        return new NativeRImage(Width, Height, destination);
    }

    public NativeRImage AdjustSaturation(double amount)
    {
        ValidateFinite(amount, nameof(amount));
        double factor = Math.Max(0, 1 + amount);
        if (factor == 1) return Clone();
        byte[] destination = new byte[_rgba.Length];
        ApplySaturation(destination, factor);
        return new NativeRImage(Width, Height, destination);
    }

    public NativeRImage AdjustContrast(double amount)
    {
        ValidateFinite(amount, nameof(amount));
        double factor = Math.Max(0, 1 + amount);
        if (factor == 1) return Clone();
        byte[] destination = new byte[_rgba.Length];
        ApplyRgbLut(destination, BuildContrastLut(factor));
        return new NativeRImage(Width, Height, destination);
    }

    public NativeRImage FlipHorizontal()
    {
        byte[] destination = new byte[_rgba.Length];
        if (ShouldParallelize(Width, Height))
        {
            unsafe
            {
                fixed (byte* sourceBase = _rgba)
                fixed (byte* destinationBase = destination)
                {
                    nint sourceAddress = (nint)sourceBase;
                    nint destinationAddress = (nint)destinationBase;
                    Parallel.For(0, Height, y =>
                    {
                        int rowStart = y * Width;
                        FlipHorizontalRow((uint*)sourceAddress + rowStart, (uint*)destinationAddress + rowStart, Width);
                    });
                }
            }

            return new NativeRImage(Width, Height, destination);
        }

        ReadOnlySpan<uint> sourcePixels = MemoryMarshal.Cast<byte, uint>(_rgba);
        Span<uint> destinationPixels = MemoryMarshal.Cast<byte, uint>(destination);

        for (int y = 0; y < Height; y++)
        {
            int row = y * Width;
            for (int x = 0; x < Width; x++)
            {
                destinationPixels[row + x] = sourcePixels[row + Width - 1 - x];
            }
        }

        return new NativeRImage(Width, Height, destination);
    }

    public NativeRImage FlipVertical()
    {
        byte[] destination = new byte[_rgba.Length];
        int stride = Width * 4;
        if (ShouldParallelize(Width, Height))
        {
            Parallel.For(0, Height, y => Buffer.BlockCopy(_rgba, (Height - 1 - y) * stride, destination, y * stride, stride));
            return new NativeRImage(Width, Height, destination);
        }

        for (int y = 0; y < Height; y++)
        {
            Buffer.BlockCopy(_rgba, (Height - 1 - y) * stride, destination, y * stride, stride);
        }

        return new NativeRImage(Width, Height, destination);
    }

    public NativeRImage Invert()
    {
        byte[] destination = new byte[_rgba.Length];
        InvertCore(destination);
        return new NativeRImage(Width, Height, destination);
    }

    public NativeRImage Rotate90Clockwise()
    {
        byte[] destination = new byte[CheckedPixelByteCount(Height, Width)];
        if (ShouldParallelize(Width, Height))
        {
            unsafe
            {
                fixed (byte* sourceBase = _rgba)
                fixed (byte* destinationBase = destination)
                {
                    nint sourceAddress = (nint)sourceBase;
                    nint destinationAddress = (nint)destinationBase;
                    Parallel.For(0, Height, y => Rotate90ClockwiseRow((uint*)sourceAddress, (uint*)destinationAddress, Width, Height, y));
                }
            }

            return new NativeRImage(Height, Width, destination);
        }

        ReadOnlySpan<uint> sourcePixels = MemoryMarshal.Cast<byte, uint>(_rgba);
        Span<uint> destinationPixels = MemoryMarshal.Cast<byte, uint>(destination);

        for (int y = 0; y < Height; y++)
        {
            int sourceRow = y * Width;
            int destinationX = Height - 1 - y;
            for (int x = 0; x < Width; x++)
            {
                destinationPixels[x * Height + destinationX] = sourcePixels[sourceRow + x];
            }
        }

        return new NativeRImage(Height, Width, destination);
    }

    public NativeRImage Rotate90CounterClockwise()
    {
        byte[] destination = new byte[CheckedPixelByteCount(Height, Width)];
        if (ShouldParallelize(Width, Height))
        {
            unsafe
            {
                fixed (byte* sourceBase = _rgba)
                fixed (byte* destinationBase = destination)
                {
                    nint sourceAddress = (nint)sourceBase;
                    nint destinationAddress = (nint)destinationBase;
                    Parallel.For(0, Height, y => Rotate90CounterClockwiseRow((uint*)sourceAddress, (uint*)destinationAddress, Width, Height, y));
                }
            }

            return new NativeRImage(Height, Width, destination);
        }

        ReadOnlySpan<uint> sourcePixels = MemoryMarshal.Cast<byte, uint>(_rgba);
        Span<uint> destinationPixels = MemoryMarshal.Cast<byte, uint>(destination);

        for (int y = 0; y < Height; y++)
        {
            int sourceRow = y * Width;
            for (int x = 0; x < Width; x++)
            {
                destinationPixels[(Width - 1 - x) * Height + y] = sourcePixels[sourceRow + x];
            }
        }

        return new NativeRImage(Height, Width, destination);
    }

    public NativeRImage Rotate180()
    {
        byte[] destination = new byte[_rgba.Length];
        if (ShouldParallelize(Width, Height))
        {
            unsafe
            {
                fixed (byte* sourceBase = _rgba)
                fixed (byte* destinationBase = destination)
                {
                    nint sourceAddress = (nint)sourceBase;
                    nint destinationAddress = (nint)destinationBase;
                    Parallel.For(0, Height, y => Rotate180Row((uint*)sourceAddress, (uint*)destinationAddress, Width, Height, y));
                }
            }

            return new NativeRImage(Width, Height, destination);
        }

        ReadOnlySpan<uint> sourcePixels = MemoryMarshal.Cast<byte, uint>(_rgba);
        Span<uint> destinationPixels = MemoryMarshal.Cast<byte, uint>(destination);

        for (int i = 0, j = sourcePixels.Length - 1; i < sourcePixels.Length; i++, j--)
        {
            destinationPixels[i] = sourcePixels[j];
        }

        return new NativeRImage(Width, Height, destination);
    }

    public NativeRImage Rotate(double degrees, ColorRgba background = default, ResizeKernel interpolation = ResizeKernel.Bilinear)
    {
        ValidateFinite(degrees, nameof(degrees));
        double normalized = NormalizeDegrees(degrees);
        if (IsNear(normalized, 0)) return Clone();
        if (IsNear(normalized, 90)) return Rotate90Clockwise();
        if (IsNear(normalized, 180)) return Rotate180();
        if (IsNear(normalized, 270)) return Rotate90CounterClockwise();

        double radians = normalized * Math.PI / 180;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        int destinationWidth = Math.Max(1, (int)Math.Ceiling(Math.Abs(Width * cos) + Math.Abs(Height * sin)));
        int destinationHeight = Math.Max(1, (int)Math.Ceiling(Math.Abs(Width * sin) + Math.Abs(Height * cos)));
        byte[] destination = new byte[CheckedPixelByteCount(destinationWidth, destinationHeight)];
        Fill(destination, background);

        double sourceCenterX = (Width - 1) * 0.5;
        double sourceCenterY = (Height - 1) * 0.5;
        double destinationCenterX = (destinationWidth - 1) * 0.5;
        double destinationCenterY = (destinationHeight - 1) * 0.5;

        bool nearest = interpolation == ResizeKernel.Nearest;
        if (ShouldParallelize(destinationWidth, destinationHeight))
        {
            Parallel.For(0, destinationHeight, nearest ? ProcessNearestRow : ProcessBilinearRow);
        }
        else
        {
            for (int y = 0; y < destinationHeight; y++)
            {
                if (nearest) ProcessNearestRow(y);
                else ProcessBilinearRow(y);
            }
        }

        return new NativeRImage(destinationWidth, destinationHeight, destination);

        void ProcessNearestRow(int y)
        {
            double dy = y - destinationCenterY;
            double sourceX = cos * -destinationCenterX + sin * dy + sourceCenterX;
            double sourceY = -sin * -destinationCenterX + cos * dy + sourceCenterY;
            int destinationRow = y * destinationWidth * 4;
            for (int x = 0; x < destinationWidth; x++)
            {
                int sx = (int)Math.Floor(sourceX + 0.5);
                int sy = (int)Math.Floor(sourceY + 0.5);
                if ((uint)sx < (uint)Width && (uint)sy < (uint)Height)
                {
                    int sourceOffset = (sy * Width + sx) * 4;
                    int destinationOffset = destinationRow + x * 4;
                    destination[destinationOffset] = _rgba[sourceOffset];
                    destination[destinationOffset + 1] = _rgba[sourceOffset + 1];
                    destination[destinationOffset + 2] = _rgba[sourceOffset + 2];
                    destination[destinationOffset + 3] = _rgba[sourceOffset + 3];
                }

                sourceX += cos;
                sourceY -= sin;
            }
        }

        void ProcessBilinearRow(int y)
        {
            double dy = y - destinationCenterY;
            double sourceX = cos * -destinationCenterX + sin * dy + sourceCenterX;
            double sourceY = -sin * -destinationCenterX + cos * dy + sourceCenterY;
            int destinationRow = y * destinationWidth * 4;
            int maxX = Width - 1;
            int maxY = Height - 1;

            for (int x = 0; x < destinationWidth; x++)
            {
                if (sourceX >= 0 && sourceY >= 0 && sourceX <= maxX && sourceY <= maxY)
                {
                    int x0 = (int)sourceX;
                    int y0 = (int)sourceY;
                    int x1 = x0 == maxX ? x0 : x0 + 1;
                    int y1 = y0 == maxY ? y0 : y0 + 1;
                    int wx = (int)Math.Round((sourceX - x0) * LinearOne);
                    int wy = (int)Math.Round((sourceY - y0) * LinearOne);
                    int inverseWx = LinearOne - wx;
                    int inverseWy = LinearOne - wy;
                    int o00 = (y0 * Width + x0) * 4;
                    int o10 = (y0 * Width + x1) * 4;
                    int o01 = (y1 * Width + x0) * 4;
                    int o11 = (y1 * Width + x1) * 4;
                    int destinationOffset = destinationRow + x * 4;

                    BilinearChannel(_rgba[o00], _rgba[o10], _rgba[o01], _rgba[o11], inverseWx, wx, inverseWy, wy, destination, destinationOffset);
                    BilinearChannel(_rgba[o00 + 1], _rgba[o10 + 1], _rgba[o01 + 1], _rgba[o11 + 1], inverseWx, wx, inverseWy, wy, destination, destinationOffset + 1);
                    BilinearChannel(_rgba[o00 + 2], _rgba[o10 + 2], _rgba[o01 + 2], _rgba[o11 + 2], inverseWx, wx, inverseWy, wy, destination, destinationOffset + 2);
                    BilinearChannel(_rgba[o00 + 3], _rgba[o10 + 3], _rgba[o01 + 3], _rgba[o11 + 3], inverseWx, wx, inverseWy, wy, destination, destinationOffset + 3);
                }

                sourceX += cos;
                sourceY -= sin;
            }
        }
    }

    public NativeRImage Blur(double radius = 1)
    {
        ValidateFinite(radius, nameof(radius));
        if (radius < 0) throw new ArgumentOutOfRangeException(nameof(radius), "Blur radius cannot be negative.");
        if (radius <= 0) return Clone();
        return new NativeRImage(Width, Height, BlurPixels(radius));
    }

    public NativeRImage Sharpen(double amount = 1, double radius = 1)
    {
        ValidateFinite(amount, nameof(amount));
        ValidateFinite(radius, nameof(radius));
        if (radius < 0) throw new ArgumentOutOfRangeException(nameof(radius), "Sharpen radius cannot be negative.");
        if (amount == 0 || radius == 0) return Clone();
        byte[] blurPixels = BlurPixels(radius);
        byte[] destination = new byte[_rgba.Length];
        ApplySharpen(destination, blurPixels, amount);
        return new NativeRImage(Width, Height, destination);
    }

    public NativeRImage AdjustSharpness(double amount = 1, double radius = 1) => Sharpen(amount, radius);

    public NativeRImage AdjustHue(double degrees)
    {
        if (degrees == 0) return Clone();
        float rad = (float)(degrees * Math.PI / 180.0);
        float cos = MathF.Cos(rad), sin = MathF.Sin(rad);
        const int shift = 14, one = 1 << shift, half = one >> 1;
        int m00 = (int)MathF.Round((0.213f + cos * 0.787f - sin * 0.213f) * one);
        int m01 = (int)MathF.Round((0.715f - cos * 0.715f - sin * 0.715f) * one);
        int m02 = (int)MathF.Round((0.072f - cos * 0.072f + sin * 0.928f) * one);
        int m10 = (int)MathF.Round((0.213f - cos * 0.213f + sin * 0.143f) * one);
        int m11 = (int)MathF.Round((0.715f + cos * 0.285f + sin * 0.140f) * one);
        int m12 = (int)MathF.Round((0.072f - cos * 0.072f - sin * 0.283f) * one);
        int m20 = (int)MathF.Round((0.213f - cos * 0.213f - sin * 0.787f) * one);
        int m21 = (int)MathF.Round((0.715f - cos * 0.715f + sin * 0.715f) * one);
        int m22 = (int)MathF.Round((0.072f + cos * 0.928f + sin * 0.072f) * one);
        byte[] dst = new byte[_rgba.Length];
        int stride = Width * 4;
        void ProcessRow(int y) {
            int row = y * stride;
            for (int x = 0; x < Width; x++) {
                int o = row + x * 4;
                int r = _rgba[o], g = _rgba[o + 1], b = _rgba[o + 2];
                dst[o]     = (byte)ClampToByte((m00 * r + m01 * g + m02 * b + half) >> shift);
                dst[o + 1] = (byte)ClampToByte((m10 * r + m11 * g + m12 * b + half) >> shift);
                dst[o + 2] = (byte)ClampToByte((m20 * r + m21 * g + m22 * b + half) >> shift);
                dst[o + 3] = _rgba[o + 3];
            }
        }
        if (ShouldParallelize(Width, Height)) Parallel.For(0, Height, ProcessRow);
        else for (int y = 0; y < Height; y++) ProcessRow(y);
        return new NativeRImage(Width, Height, dst);
    }

    public void Save(string path, ImageFormat format, ImageSaveOptions? options = null)
    {
        using FileStream stream = File.Create(path);
        Save(stream, format, options);
    }

    public void Save(Stream stream, ImageFormat format, ImageSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new ImageSaveOptions();
        NativeRImage image = Apply(new ImageLoadOptions { Crop = options.Crop, Resize = options.Resize });
        ImageCodecs.FindEncoder(format).Encode(image, stream, options);
    }

    internal static int ClampToByte(int value) => value < 0 ? 0 : value > 255 ? 255 : value;

    internal static byte ClampToByte(double value)
    {
        if (value <= 0) return 0;
        if (value >= 255) return 255;
        return (byte)Math.Round(value);
    }

    internal int PixelOffset(int x, int y) => (y * Width + x) * 4;

    private NativeRImage Apply(ImageLoadOptions? options)
    {
        if (options is null) return this;
        NativeRImage image = this;
        if (options.Crop is { } crop) image = image.Crop(crop);
        if (options.Resize is { } resize) image = image.Resize(resize);
        return image;
    }

    private static byte[] ReadStreamToEnd(Stream stream)
    {
        if (stream.CanSeek)
        {
            long remaining = stream.Length - stream.Position;
            if (remaining is >= 0 and <= int.MaxValue)
            {
                byte[] data = new byte[remaining];
                int offset = 0;
                while (offset < data.Length)
                {
                    int read = stream.Read(data, offset, data.Length - offset);
                    if (read == 0) break;
                    offset += read;
                }

                if (offset == data.Length) return data;
                Array.Resize(ref data, offset);
                return data;
            }
        }

        using MemoryStream memory = new();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private byte[] BlurPixels(double radius)
    {
        int[] kernel = BuildGaussianKernel(radius);
        int kernelRadius = kernel.Length / 2;
        byte[] temp = new byte[_rgba.Length];
        byte[] destination = new byte[_rgba.Length];
        int[] horizontalOffsets = BuildClampedOffsetMap(Width, kernelRadius, 4);
        int[] verticalOffsets = BuildClampedOffsetMap(Height, kernelRadius, Width * 4);
        ApplyPremultipliedHorizontalBlur(kernel, horizontalOffsets, temp);
        ApplyPremultipliedVerticalBlur(kernel, verticalOffsets, temp, destination);
        return destination;
    }

    private void ApplyRgbLut(byte[] destination, byte[] lut)
    {
        int stride = Width * 4;
        if (ShouldParallelize(Width, Height))
        {
            unsafe
            {
                fixed (byte* sourceBase = _rgba)
                fixed (byte* destinationBase = destination)
                fixed (byte* lutBase = lut)
                {
                    nint sourceAddress = (nint)sourceBase;
                    nint destinationAddress = (nint)destinationBase;
                    nint lutAddress = (nint)lutBase;
                    Parallel.For(0, Height, y =>
                    {
                        int rowStart = y * stride;
                        ApplyRgbLutRow((byte*)sourceAddress + rowStart, (byte*)destinationAddress + rowStart, (byte*)lutAddress, Width);
                    });
                }
            }

            return;
        }

        unsafe
        {
            fixed (byte* sourceBase = _rgba)
            fixed (byte* destinationBase = destination)
            fixed (byte* lutBase = lut)
            {
                for (int y = 0; y < Height; y++)
                {
                    int rowStart = y * stride;
                    ApplyRgbLutRow(sourceBase + rowStart, destinationBase + rowStart, lutBase, Width);
                }
            }
        }
    }

    private void ApplySaturation(byte[] destination, double factor)
    {
        int fixedFactor = ToFixedFactor(factor);
        int stride = Width * 4;
        if (ShouldParallelize(Width, Height))
        {
            unsafe
            {
                fixed (byte* sourceBase = _rgba)
                fixed (byte* destinationBase = destination)
                {
                    nint sourceAddress = (nint)sourceBase;
                    nint destinationAddress = (nint)destinationBase;
                    Parallel.For(0, Height, y =>
                    {
                        int rowStart = y * stride;
                        ApplySaturationRow((byte*)sourceAddress + rowStart, (byte*)destinationAddress + rowStart, Width, fixedFactor);
                    });
                }
            }

            return;
        }

        unsafe
        {
            fixed (byte* sourceBase = _rgba)
            fixed (byte* destinationBase = destination)
            {
                for (int y = 0; y < Height; y++)
                {
                    int rowStart = y * stride;
                    ApplySaturationRow(sourceBase + rowStart, destinationBase + rowStart, Width, fixedFactor);
                }
            }
        }
    }

    private void InvertCore(byte[] destination)
    {
        int stride = Width * 4;
        if (ShouldParallelize(Width, Height))
        {
            unsafe
            {
                fixed (byte* sourceBase = _rgba)
                fixed (byte* destinationBase = destination)
                {
                    nint sourceAddress = (nint)sourceBase;
                    nint destinationAddress = (nint)destinationBase;
                    Parallel.For(0, Height, y =>
                    {
                        int rowStart = y * stride;
                        InvertRow((byte*)sourceAddress + rowStart, (byte*)destinationAddress + rowStart, Width);
                    });
                }
            }

            return;
        }

        unsafe
        {
            fixed (byte* sourceBase = _rgba)
            fixed (byte* destinationBase = destination)
            {
                for (int y = 0; y < Height; y++)
                {
                    int rowStart = y * stride;
                    InvertRow(sourceBase + rowStart, destinationBase + rowStart, Width);
                }
            }
        }
    }

    private void ApplySharpen(byte[] destination, byte[] blurPixels, double amount)
    {
        int fixedAmount = ToSignedFixedFactor(amount);
        int stride = Width * 4;
        if (ShouldParallelize(Width, Height))
        {
            unsafe
            {
                fixed (byte* sourceBase = _rgba)
                fixed (byte* blurBase = blurPixels)
                fixed (byte* destinationBase = destination)
                {
                    nint sourceAddress = (nint)sourceBase;
                    nint blurAddress = (nint)blurBase;
                    nint destinationAddress = (nint)destinationBase;
                    Parallel.For(0, Height, y =>
                    {
                        int rowStart = y * stride;
                        ApplySharpenRow((byte*)sourceAddress + rowStart, (byte*)blurAddress + rowStart, (byte*)destinationAddress + rowStart, Width, fixedAmount);
                    });
                }
            }

            return;
        }

        unsafe
        {
            fixed (byte* sourceBase = _rgba)
            fixed (byte* blurBase = blurPixels)
            fixed (byte* destinationBase = destination)
            {
                for (int y = 0; y < Height; y++)
                {
                    int rowStart = y * stride;
                    ApplySharpenRow(sourceBase + rowStart, blurBase + rowStart, destinationBase + rowStart, Width, fixedAmount);
                }
            }
        }
    }

    private static unsafe void ApplyRgbLutRow(byte* source, byte* destination, byte* lut, int width)
    {
        for (int x = 0; x < width; x++)
        {
            destination[0] = lut[source[0]];
            destination[1] = lut[source[1]];
            destination[2] = lut[source[2]];
            destination[3] = source[3];
            source += 4;
            destination += 4;
        }
    }

    private static unsafe void ApplySaturationRow(byte* source, byte* destination, int width, int fixedFactor)
    {
        for (int x = 0; x < width; x++)
        {
            int r = source[0];
            int g = source[1];
            int b = source[2];
            int gray = (77 * r + 150 * g + 29 * b + 128) >> 8;
            destination[0] = (byte)ClampToByte(gray + DivideLinearFixed((long)(r - gray) * fixedFactor));
            destination[1] = (byte)ClampToByte(gray + DivideLinearFixed((long)(g - gray) * fixedFactor));
            destination[2] = (byte)ClampToByte(gray + DivideLinearFixed((long)(b - gray) * fixedFactor));
            destination[3] = source[3];
            source += 4;
            destination += 4;
        }
    }

    private static unsafe void ApplySharpenRow(byte* source, byte* blur, byte* destination, int width, int fixedAmount)
    {
        for (int x = 0; x < width; x++)
        {
            destination[0] = (byte)ClampToByte(source[0] + DivideLinearFixed((long)(source[0] - blur[0]) * fixedAmount));
            destination[1] = (byte)ClampToByte(source[1] + DivideLinearFixed((long)(source[1] - blur[1]) * fixedAmount));
            destination[2] = (byte)ClampToByte(source[2] + DivideLinearFixed((long)(source[2] - blur[2]) * fixedAmount));
            destination[3] = source[3];
            source += 4;
            blur += 4;
            destination += 4;
        }
    }

    private static unsafe void InvertRow(byte* source, byte* destination, int width)
    {
        uint* sourcePixels = (uint*)source;
        uint* destinationPixels = (uint*)destination;
        for (int x = 0; x < width; x++)
        {
            destinationPixels[x] = sourcePixels[x] ^ 0x00FF_FFFFu;
        }
    }

    private static unsafe void FlipHorizontalRow(uint* sourcePixels, uint* destinationPixels, int width)
    {
        for (int x = 0; x < width; x++)
        {
            destinationPixels[x] = sourcePixels[width - 1 - x];
        }
    }

    private static unsafe void Rotate90ClockwiseRow(uint* sourcePixels, uint* destinationPixels, int width, int height, int y)
    {
        uint* sourceRow = sourcePixels + y * width;
        int destinationX = height - 1 - y;
        for (int x = 0; x < width; x++)
        {
            destinationPixels[x * height + destinationX] = sourceRow[x];
        }
    }

    private static unsafe void Rotate90CounterClockwiseRow(uint* sourcePixels, uint* destinationPixels, int width, int height, int y)
    {
        uint* sourceRow = sourcePixels + y * width;
        for (int x = 0; x < width; x++)
        {
            destinationPixels[(width - 1 - x) * height + y] = sourceRow[x];
        }
    }

    private static unsafe void Rotate180Row(uint* sourcePixels, uint* destinationPixels, int width, int height, int y)
    {
        uint* sourceRow = sourcePixels + (height - 1 - y) * width;
        uint* destinationRow = destinationPixels + y * width;
        for (int x = 0; x < width; x++)
        {
            destinationRow[x] = sourceRow[width - 1 - x];
        }
    }

    private static unsafe void ResizeNearestRow(uint* sourcePixels, uint* destinationPixels, int sourceWidth, int destinationWidth, int* sourceXs, int sourceY, int destinationY)
    {
        uint* sourceRow = sourcePixels + sourceY * sourceWidth;
        uint* destinationRow = destinationPixels + destinationY * destinationWidth;
        for (int x = 0; x < destinationWidth; x++)
        {
            destinationRow[x] = sourceRow[sourceXs[x]];
        }
    }

    private void ApplyPremultipliedHorizontalBlur(int[] kernel, int[] sourceOffsets, byte[] temp)
    {
        if (ShouldParallelize(Width, Height)) Parallel.For(0, Height, ProcessRow);
        else for (int y = 0; y < Height; y++) ProcessRow(y);

        void ProcessRow(int y)
        {
            int row = y * Width * 4;
            for (int x = 0; x < Width; x++)
            {
                long sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                for (int k = 0; k < kernel.Length; k++)
                {
                    int sourceOffset = row + sourceOffsets[x + k];
                    int weight = kernel[k];
                    int alpha = _rgba[sourceOffset + 3];
                    sumR += (long)_rgba[sourceOffset] * alpha * weight;
                    sumG += (long)_rgba[sourceOffset + 1] * alpha * weight;
                    sumB += (long)_rgba[sourceOffset + 2] * alpha * weight;
                    sumA += (long)alpha * weight;
                }

                int destinationOffset = row + x * 4;
                temp[destinationOffset]     = DividePremultipliedFixed(sumR);
                temp[destinationOffset + 1] = DividePremultipliedFixed(sumG);
                temp[destinationOffset + 2] = DividePremultipliedFixed(sumB);
                temp[destinationOffset + 3] = (byte)ClampToByte(DivideFixed(sumA));
            }
        }
    }

    private void ApplyPremultipliedVerticalBlur(int[] kernel, int[] sourceOffsets, byte[] temp, byte[] destination)
    {
        if (ShouldParallelize(Width, Height)) Parallel.For(0, Height, ProcessRow);
        else for (int y = 0; y < Height; y++) ProcessRow(y);

        void ProcessRow(int y)
        {
            int destinationRow = y * Width * 4;
            for (int x = 0; x < Width; x++)
            {
                long sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                for (int k = 0; k < kernel.Length; k++)
                {
                    int sourceOffset = sourceOffsets[y + k] + x * 4;
                    int weight = kernel[k];
                    sumR += (long)temp[sourceOffset] * weight;
                    sumG += (long)temp[sourceOffset + 1] * weight;
                    sumB += (long)temp[sourceOffset + 2] * weight;
                    sumA += (long)temp[sourceOffset + 3] * weight;
                }

                int alpha = ClampToByte(DivideFixed(sumA));
                int premulR = DivideFixed(sumR);
                int premulG = DivideFixed(sumG);
                int premulB = DivideFixed(sumB);
                int destinationOffset = destinationRow + x * 4;
                if (alpha == 0)
                {
                    destination[destinationOffset] = 0;
                    destination[destinationOffset + 1] = 0;
                    destination[destinationOffset + 2] = 0;
                    destination[destinationOffset + 3] = 0;
                    continue;
                }

                destination[destinationOffset]     = (byte)ClampToByte((premulR * 255 + alpha / 2) / alpha);
                destination[destinationOffset + 1] = (byte)ClampToByte((premulG * 255 + alpha / 2) / alpha);
                destination[destinationOffset + 2] = (byte)ClampToByte((premulB * 255 + alpha / 2) / alpha);
                destination[destinationOffset + 3] = (byte)alpha;
            }
        }
    }

    private NativeRImage ResizeNearest(int width, int height)
    {
        byte[] destination = new byte[CheckedPixelByteCount(width, height)];
        int[] sourceXs = BuildNearestMap(Width, width);
        int[] sourceYs = BuildNearestMap(Height, height);

        if (ShouldParallelize(width, height))
        {
            unsafe
            {
                fixed (byte* sourceBase = _rgba)
                fixed (byte* destinationBase = destination)
                fixed (int* sourceXBase = sourceXs)
                fixed (int* sourceYBase = sourceYs)
                {
                    nint sourceAddress = (nint)sourceBase;
                    nint destinationAddress = (nint)destinationBase;
                    nint sourceXAddress = (nint)sourceXBase;
                    nint sourceYAddress = (nint)sourceYBase;
                    Parallel.For(0, height, y =>
                    {
                        int sourceY = ((int*)sourceYAddress)[y];
                        ResizeNearestRow((uint*)sourceAddress, (uint*)destinationAddress, Width, width, (int*)sourceXAddress, sourceY, y);
                    });
                }
            }

            return new NativeRImage(width, height, destination);
        }

        ReadOnlySpan<uint> sourcePixels = MemoryMarshal.Cast<byte, uint>(_rgba);
        Span<uint> destinationPixels = MemoryMarshal.Cast<byte, uint>(destination);
        for (int y = 0; y < height; y++)
        {
            int sourceRow = sourceYs[y] * Width;
            int destinationRow = y * width;
            for (int x = 0; x < width; x++)
            {
                destinationPixels[destinationRow + x] = sourcePixels[sourceRow + sourceXs[x]];
            }
        }

        return new NativeRImage(width, height, destination);
    }

    private NativeRImage ResizeBilinear(int width, int height)
    {
        byte[] destination = new byte[CheckedPixelByteCount(width, height)];
        LinearContribution[] xMap = BuildLinearMap(Width, width);
        LinearContribution[] yMap = BuildLinearMap(Height, height);

        if (ShouldParallelize(width, height)) Parallel.For(0, height, ProcessRow);
        else for (int y = 0; y < height; y++) ProcessRow(y);

        return new NativeRImage(width, height, destination);

        void ProcessRow(int y)
        {
            LinearContribution yContribution = yMap[y];
            int y0Offset = yContribution.First * Width * 4;
            int y1Offset = yContribution.Second * Width * 4;
            int wy = yContribution.Weight;
            int inverseWy = LinearOne - wy;
            int destinationRow = y * width * 4;

            for (int x = 0; x < width; x++)
            {
                LinearContribution xContribution = xMap[x];
                int wx = xContribution.Weight;
                int inverseWx = LinearOne - wx;
                int x0 = xContribution.First * 4;
                int x1 = xContribution.Second * 4;
                int destinationOffset = destinationRow + x * 4;

                BilinearChannel(_rgba[y0Offset + x0], _rgba[y0Offset + x1], _rgba[y1Offset + x0], _rgba[y1Offset + x1], inverseWx, wx, inverseWy, wy, destination, destinationOffset);
                BilinearChannel(_rgba[y0Offset + x0 + 1], _rgba[y0Offset + x1 + 1], _rgba[y1Offset + x0 + 1], _rgba[y1Offset + x1 + 1], inverseWx, wx, inverseWy, wy, destination, destinationOffset + 1);
                BilinearChannel(_rgba[y0Offset + x0 + 2], _rgba[y0Offset + x1 + 2], _rgba[y1Offset + x0 + 2], _rgba[y1Offset + x1 + 2], inverseWx, wx, inverseWy, wy, destination, destinationOffset + 2);
                BilinearChannel(_rgba[y0Offset + x0 + 3], _rgba[y0Offset + x1 + 3], _rgba[y1Offset + x0 + 3], _rgba[y1Offset + x1 + 3], inverseWx, wx, inverseWy, wy, destination, destinationOffset + 3);
            }
        }
    }

    private NativeRImage ResizeLanczos(int width, int height)
    {
        ResamplePlan horizontalPlan = BuildResamplePlan(Width, width);
        ResamplePlan verticalPlan = BuildResamplePlan(Height, height);
        NativeRImage horizontal = ResizeLanczosHorizontal(horizontalPlan);
        return horizontal.ResizeLanczosVertical(verticalPlan);
    }

    private NativeRImage ResizeLanczosHorizontal(ResamplePlan plan)
    {
        byte[] destination = new byte[CheckedPixelByteCount(plan.DestinationSize, Height)];
        int destinationStride = plan.DestinationSize * 4;

        if (ShouldParallelize(plan.DestinationSize, Height)) Parallel.For(0, Height, ProcessRow);
        else for (int y = 0; y < Height; y++) ProcessRow(y);

        return new NativeRImage(plan.DestinationSize, Height, destination);

        void ProcessRow(int y)
        {
            int sourceRow = y * Width * 4;
            int destinationRow = y * destinationStride;
            for (int x = 0; x < plan.DestinationSize; x++)
            {
                long sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                int start = plan.Offsets[x];
                int end = start + plan.Counts[x];

                for (int i = start; i < end; i++)
                {
                    int sourceOffset = sourceRow + plan.Indices[i] * 4;
                    int weight = plan.Weights[i];
                    sumR += _rgba[sourceOffset] * weight;
                    sumG += _rgba[sourceOffset + 1] * weight;
                    sumB += _rgba[sourceOffset + 2] * weight;
                    sumA += _rgba[sourceOffset + 3] * weight;
                }

                int destinationOffset = destinationRow + x * 4;
                destination[destinationOffset]     = (byte)ClampToByte(DivideFixed(sumR));
                destination[destinationOffset + 1] = (byte)ClampToByte(DivideFixed(sumG));
                destination[destinationOffset + 2] = (byte)ClampToByte(DivideFixed(sumB));
                destination[destinationOffset + 3] = (byte)ClampToByte(DivideFixed(sumA));
            }
        }
    }

    private NativeRImage ResizeLanczosVertical(ResamplePlan plan)
    {
        byte[] destination = new byte[CheckedPixelByteCount(Width, plan.DestinationSize)];
        int sourceStride = Width * 4;

        if (ShouldParallelize(Width, plan.DestinationSize)) Parallel.For(0, plan.DestinationSize, ProcessRow);
        else for (int y = 0; y < plan.DestinationSize; y++) ProcessRow(y);

        return new NativeRImage(Width, plan.DestinationSize, destination);

        void ProcessRow(int y)
        {
            int destinationRow = y * sourceStride;
            int start = plan.Offsets[y];
            int end = start + plan.Counts[y];

            for (int x = 0; x < Width; x++)
            {
                long sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                int channelOffset = x * 4;

                for (int i = start; i < end; i++)
                {
                    int sourceOffset = plan.Indices[i] * sourceStride + channelOffset;
                    int weight = plan.Weights[i];
                    sumR += _rgba[sourceOffset] * weight;
                    sumG += _rgba[sourceOffset + 1] * weight;
                    sumB += _rgba[sourceOffset + 2] * weight;
                    sumA += _rgba[sourceOffset + 3] * weight;
                }

                int destinationOffset = destinationRow + channelOffset;
                destination[destinationOffset]     = (byte)ClampToByte(DivideFixed(sumR));
                destination[destinationOffset + 1] = (byte)ClampToByte(DivideFixed(sumG));
                destination[destinationOffset + 2] = (byte)ClampToByte(DivideFixed(sumB));
                destination[destinationOffset + 3] = (byte)ClampToByte(DivideFixed(sumA));
            }
        }
    }

    private static int[] BuildNearestMap(int sourceSize, int destinationSize)
    {
        int[] map = new int[destinationSize];
        double scale = (double)sourceSize / destinationSize;
        for (int i = 0; i < destinationSize; i++)
        {
            map[i] = Math.Min(sourceSize - 1, (int)((i + 0.5) * scale));
        }

        return map;
    }

    private static bool ShouldParallelize(int width, int height) =>
        Environment.ProcessorCount > 1 && (long)width * height >= 250_000;

    private static byte[] BuildOffsetLut(int offset)
    {
        byte[] lut = new byte[256];
        for (int i = 0; i < lut.Length; i++) lut[i] = (byte)ClampToByte(i + offset);
        return lut;
    }

    private static byte[] BuildContrastLut(double factor)
    {
        byte[] lut = new byte[256];
        for (int i = 0; i < lut.Length; i++) lut[i] = ClampToByte(128 + (i - 128) * factor);
        return lut;
    }

    private static int[] BuildClampedOffsetMap(int size, int radius, int stride)
    {
        int[] map = new int[size + radius * 2];
        for (int i = 0; i < map.Length; i++)
        {
            int coordinate = Math.Clamp(i - radius, 0, size - 1);
            map[i] = coordinate * stride;
        }

        return map;
    }

    private static int[] BuildGaussianKernel(double radius)
    {
        if (radius > 256) throw new ArgumentOutOfRangeException(nameof(radius), "Blur radius is limited to 256 pixels.");

        int kernelRadius = Math.Max(1, (int)Math.Ceiling(radius * 3));
        double sigma = Math.Max(0.01, radius);
        double sigma2 = 2 * sigma * sigma;
        int[] kernel = new int[kernelRadius * 2 + 1];
        double[] raw = new double[kernel.Length];
        double sum = 0;

        for (int i = 0; i < raw.Length; i++)
        {
            int x = i - kernelRadius;
            double weight = Math.Exp(-(x * x) / sigma2);
            raw[i] = weight;
            sum += weight;
        }

        int fixedSum = 0;
        int strongest = 0;
        for (int i = 0; i < kernel.Length; i++)
        {
            kernel[i] = Math.Max(1, (int)Math.Round(raw[i] / sum * ResampleOne));
            fixedSum += kernel[i];
            if (kernel[i] > kernel[strongest]) strongest = i;
        }

        kernel[strongest] += ResampleOne - fixedSum;
        return kernel;
    }

    private static void Fill(byte[] destination, ColorRgba color)
    {
        if (color == default) return;
        uint packed = (uint)(color.R | (color.G << 8) | (color.B << 16) | (color.A << 24));
        Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(destination);
        pixels.Fill(packed);
    }

    private static double NormalizeDegrees(double degrees)
    {
        double normalized = degrees % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private static bool IsNear(double value, double target) => Math.Abs(value - target) < 1e-9;

    private static void ValidateFinite(double value, string paramName)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(paramName, "Value must be finite.");
    }

    private static LinearContribution[] BuildLinearMap(int sourceSize, int destinationSize)
    {
        LinearContribution[] map = new LinearContribution[destinationSize];
        double scale = (double)sourceSize / destinationSize;
        for (int i = 0; i < destinationSize; i++)
        {
            double source = (i + 0.5) * scale - 0.5;
            if (source <= 0) { map[i] = new LinearContribution(0, 0, 0); continue; }
            if (source >= sourceSize - 1) { int last = sourceSize - 1; map[i] = new LinearContribution(last, last, 0); continue; }
            int first = (int)source;
            int weight = (int)Math.Round((source - first) * LinearOne);
            map[i] = new LinearContribution(first, first + 1, weight);
        }

        return map;
    }

    private static ResamplePlan BuildResamplePlan(int sourceSize, int destinationSize)
    {
        int[] offsets = new int[destinationSize];
        int[] counts = new int[destinationSize];
        double scale = (double)sourceSize / destinationSize;
        double filterScale = Math.Max(1, scale);
        double radius = 3 * filterScale;
        int estimatedContributions = Math.Clamp((int)Math.Ceiling(radius * 2 + 1), 1, sourceSize);
        int estimatedCapacity = (int)Math.Min((long)destinationSize * estimatedContributions, 4_000_000);
        List<int> indices = new(estimatedCapacity);
        List<int> weights = new(estimatedCapacity);
        List<int> localIndices = new(estimatedContributions);
        List<double> localWeights = new(estimatedContributions);

        for (int destination = 0; destination < destinationSize; destination++)
        {
            offsets[destination] = indices.Count;
            double center = (destination + 0.5) * scale - 0.5;
            int left = (int)Math.Ceiling(center - radius);
            int right = (int)Math.Floor(center + radius);
            localIndices.Clear();
            localWeights.Clear();
            double weightSum = 0;

            for (int source = left; source <= right; source++)
            {
                double weight = Lanczos((center - source) / filterScale);
                if (Math.Abs(weight) < 1e-12) continue;
                localIndices.Add(Math.Clamp(source, 0, sourceSize - 1));
                localWeights.Add(weight);
                weightSum += weight;
            }

            if (localIndices.Count == 0 || Math.Abs(weightSum) < 1e-12)
            {
                int nearest = Math.Clamp((int)Math.Round(center), 0, sourceSize - 1);
                indices.Add(nearest);
                weights.Add(ResampleOne);
                counts[destination] = 1;
                continue;
            }

            int fixedSum = 0;
            int strongestWeightIndex = -1;
            int strongestWeightMagnitude = 0;
            for (int i = 0; i < localIndices.Count; i++)
            {
                int fixedWeight = (int)Math.Round(localWeights[i] / weightSum * ResampleOne);
                if (fixedWeight == 0) continue;
                indices.Add(localIndices[i]);
                weights.Add(fixedWeight);
                fixedSum += fixedWeight;
                int magnitude = Math.Abs(fixedWeight);
                if (magnitude > strongestWeightMagnitude)
                {
                    strongestWeightMagnitude = magnitude;
                    strongestWeightIndex = weights.Count - 1;
                }
            }

            if (strongestWeightIndex < 0)
            {
                int nearest = Math.Clamp((int)Math.Round(center), 0, sourceSize - 1);
                indices.Add(nearest);
                weights.Add(ResampleOne);
            }
            else
            {
                weights[strongestWeightIndex] += ResampleOne - fixedSum;
            }

            counts[destination] = indices.Count - offsets[destination];
        }

        return new ResamplePlan(destinationSize, offsets, counts, indices.ToArray(), weights.ToArray());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BilinearChannel(byte c00, byte c10, byte c01, byte c11, int inverseWx, int wx, int inverseWy, int wy, byte[] destination, int destinationOffset)
    {
        int top = c00 * inverseWx + c10 * wx;
        int bottom = c01 * inverseWx + c11 * wx;
        long value = (long)top * inverseWy + (long)bottom * wy + (1L << (LinearShift * 2 - 1));
        destination[destinationOffset] = (byte)(value >> (LinearShift * 2));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DivideFixed(long value) =>
        (int)((value + (value >= 0 ? ResampleHalf : -ResampleHalf)) / ResampleOne);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DivideLinearFixed(long value) =>
        (int)((value + (value >= 0 ? LinearHalf : -LinearHalf)) / LinearOne);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte DividePremultipliedFixed(long value)
    {
        const long denominator = 255L * ResampleOne;
        return (byte)ClampToByte((int)((value + denominator / 2) / denominator));
    }

    private static int ToFixedFactor(double factor)
    {
        double scaled = factor * LinearOne;
        if (scaled >= int.MaxValue) return int.MaxValue;
        return (int)Math.Round(scaled);
    }

    private static int ToSignedFixedFactor(double factor)
    {
        double scaled = factor * LinearOne;
        if (scaled >= int.MaxValue) return int.MaxValue;
        if (scaled <= int.MinValue) return int.MinValue;
        return (int)Math.Round(scaled);
    }

    private static double Lanczos(double x)
    {
        x = Math.Abs(x);
        if (x < double.Epsilon) return 1;
        if (x >= 3) return 0;
        return Sinc(x) * Sinc(x / 3);
    }

    private static double Sinc(double x)
    {
        double value = Math.PI * x;
        return Math.Sin(value) / value;
    }

    private void ValidateCoordinates(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            throw new ArgumentOutOfRangeException(nameof(x), "Pixel coordinates are outside the image.");
    }

    private static int CheckedPixelByteCount(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        return checked(width * height * 4);
    }

    private readonly record struct LinearContribution(int First, int Second, int Weight);

    private sealed record ResamplePlan(
        int DestinationSize,
        int[] Offsets,
        int[] Counts,
        int[] Indices,
        int[] Weights);
}
