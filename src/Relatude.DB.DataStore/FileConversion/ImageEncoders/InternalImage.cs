using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Relatude.DB.FileConversion.ImageEncoders;

internal sealed unsafe class InternalImage
{
    private const int LinearShift = 14;
    private const int LinearOne = 1 << LinearShift;
    private const int LinearHalf = LinearOne >> 1;
    private const int ResampleShift = 14;
    private const int ResampleOne = 1 << ResampleShift;
    private const int ResampleHalf = ResampleOne >> 1;

    private readonly byte[] _rgba;

    public InternalImage(int width, int height)
        : this(width, height, new byte[CheckedPixelByteCount(width, height)])
    {
    }

    public InternalImage(int width, int height, byte[] rgba)
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

    public static InternalImage Load(string path, ImageLoadOptions? options = null)
    {
        return Load(File.ReadAllBytes(path)).Apply(options);
    }

    public static InternalImage Load(Stream stream, ImageLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Load(ReadAll(stream)).Apply(options);
    }

    /// <summary>Decodes the image, at 1/2, 1/4 or 1/8 of its size when downscale asks for it and the format can.</summary>
    public static InternalImage Load(byte[] data, int downscale = 1) => ImageCodecs.FindDecoder(data).Decode(data, downscale);

    /// <summary>The size the image decodes to, read from its header.</summary>
    public static bool TryReadSize(byte[] data, out int width, out int height) => ImageCodecs.TryReadSize(data, out width, out height);

    public static InternalImage Create(int width, int height, Func<int, int, ColorRgba> fill)
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

        return new InternalImage(width, height, pixels);
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

    public InternalImage Clone()
    {
        return new InternalImage(Width, Height, (byte[])_rgba.Clone());
    }

    public InternalImage Crop(RectangleI rectangle)
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

        return new InternalImage(rectangle.Width, rectangle.Height, pixels);
    }

    public InternalImage Resize(int width, int height, ResizeKernel kernel = ResizeKernel.Lanczos3)
    {
        return Resize(new ResizeOptions(width, height, kernel));
    }

    public InternalImage Resize(ResizeOptions options)
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
            _ => Convolve(options.Width == Width ? null : LanczosPlan(Width, options.Width), options.Height == Height ? null : LanczosPlan(Height, options.Height))
        };
    }

    public InternalImage AdjustBrightness(double amount)
    {
        ValidateFinite(amount, nameof(amount));
        int offset = (int)Math.Round(amount * 255);
        if (offset == 0) return Clone();
        byte[] destination = NewPixels(_rgba.Length);
        ApplyRgbLut(destination, BuildOffsetLut(offset));
        return new InternalImage(Width, Height, destination);
    }

    public InternalImage AdjustSaturation(double amount)
    {
        ValidateFinite(amount, nameof(amount));
        double factor = Math.Max(0, 1 + amount);
        if (factor == 1) return Clone();
        byte[] destination = NewPixels(_rgba.Length);
        ApplySaturation(destination, factor);
        return new InternalImage(Width, Height, destination);
    }

    public InternalImage AdjustContrast(double amount)
    {
        ValidateFinite(amount, nameof(amount));
        double factor = Math.Max(0, 1 + amount);
        if (factor == 1) return Clone();
        byte[] destination = NewPixels(_rgba.Length);
        ApplyRgbLut(destination, BuildContrastLut(factor));
        return new InternalImage(Width, Height, destination);
    }

    public InternalImage FlipHorizontal()
    {
        byte[] destination = NewPixels(_rgba.Length);
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

            return new InternalImage(Width, Height, destination);
        }

        ReadOnlySpan<uint> sourcePixels = MemoryMarshal.Cast<byte, uint>(_rgba);
        Span<uint> destinationPixels = MemoryMarshal.Cast<byte, uint>(destination.AsSpan());

        for (int y = 0; y < Height; y++)
        {
            int row = y * Width;
            for (int x = 0; x < Width; x++)
            {
                destinationPixels[row + x] = sourcePixels[row + Width - 1 - x];
            }
        }

        return new InternalImage(Width, Height, destination);
    }

    public InternalImage FlipVertical()
    {
        byte[] destination = NewPixels(_rgba.Length);
        int stride = Width * 4;
        if (ShouldParallelize(Width, Height))
        {
            Parallel.For(0, Height, y => Buffer.BlockCopy(_rgba, (Height - 1 - y) * stride, destination, y * stride, stride));
            return new InternalImage(Width, Height, destination);
        }

        for (int y = 0; y < Height; y++)
        {
            Buffer.BlockCopy(_rgba, (Height - 1 - y) * stride, destination, y * stride, stride);
        }

        return new InternalImage(Width, Height, destination);
    }

    public InternalImage Invert()
    {
        byte[] destination = NewPixels(_rgba.Length);
        InvertCore(destination);
        return new InternalImage(Width, Height, destination);
    }

    public InternalImage Rotate90Clockwise()
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

            return new InternalImage(Height, Width, destination);
        }

        ReadOnlySpan<uint> sourcePixels = MemoryMarshal.Cast<byte, uint>(_rgba);
        Span<uint> destinationPixels = MemoryMarshal.Cast<byte, uint>(destination.AsSpan());

        for (int y = 0; y < Height; y++)
        {
            int sourceRow = y * Width;
            int destinationX = Height - 1 - y;
            for (int x = 0; x < Width; x++)
            {
                destinationPixels[x * Height + destinationX] = sourcePixels[sourceRow + x];
            }
        }

        return new InternalImage(Height, Width, destination);
    }

    public InternalImage Rotate90CounterClockwise()
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

            return new InternalImage(Height, Width, destination);
        }

        ReadOnlySpan<uint> sourcePixels = MemoryMarshal.Cast<byte, uint>(_rgba);
        Span<uint> destinationPixels = MemoryMarshal.Cast<byte, uint>(destination.AsSpan());

        for (int y = 0; y < Height; y++)
        {
            int sourceRow = y * Width;
            for (int x = 0; x < Width; x++)
            {
                destinationPixels[(Width - 1 - x) * Height + y] = sourcePixels[sourceRow + x];
            }
        }

        return new InternalImage(Height, Width, destination);
    }

    public InternalImage Rotate180()
    {
        byte[] destination = NewPixels(_rgba.Length);
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

            return new InternalImage(Width, Height, destination);
        }

        ReadOnlySpan<uint> sourcePixels = MemoryMarshal.Cast<byte, uint>(_rgba);
        Span<uint> destinationPixels = MemoryMarshal.Cast<byte, uint>(destination.AsSpan());

        for (int i = 0, j = sourcePixels.Length - 1; i < sourcePixels.Length; i++, j--)
        {
            destinationPixels[i] = sourcePixels[j];
        }

        return new InternalImage(Width, Height, destination);
    }

    public InternalImage Rotate(double degrees, ColorRgba background = default, ResizeKernel interpolation = ResizeKernel.Bilinear)
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

        return new InternalImage(destinationWidth, destinationHeight, destination);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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

    public InternalImage Blur(double radius = 1)
    {
        ValidateFinite(radius, nameof(radius));
        if (radius < 0) throw new ArgumentOutOfRangeException(nameof(radius), "Blur radius cannot be negative.");
        if (radius <= 0) return Clone();
        if (radius > 4)
        {
            // a wide blur is a small blur of a smaller picture, so its cost does not grow with the radius
            int factor = (int)(radius / 2);
            return Resize(Math.Max(1, (Width + factor - 1) / factor), Math.Max(1, (Height + factor - 1) / factor))
                .Blur(radius / factor).Resize(Width, Height);
        }

        double twoSigmaSquared = 2 * radius * radius, support = Math.Ceiling(3 * radius);
        double Gauss(double x) => Math.Exp(-x * x / twoSigmaSquared);
        return Convolve(BuildPlan(Width, Width, support, Gauss), BuildPlan(Height, Height, support, Gauss));
    }

    public InternalImage Sharpen(double amount = 1, double radius = 1)
    {
        ValidateFinite(amount, nameof(amount));
        ValidateFinite(radius, nameof(radius));
        if (radius < 0) throw new ArgumentOutOfRangeException(nameof(radius), "Sharpen radius cannot be negative.");
        if (amount == 0 || radius == 0) return Clone();
        byte[] destination = NewPixels(_rgba.Length);
        ApplySharpen(destination, Blur(radius)._rgba, amount);
        return new InternalImage(Width, Height, destination);
    }

    public InternalImage AdjustSharpness(double amount = 1, double radius = 1) => Sharpen(amount, radius);

    public InternalImage AdjustHue(double degrees)
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
        byte[] dst = NewPixels(_rgba.Length);
        int stride = Width * 4;
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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
        return new InternalImage(Width, Height, dst);
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
        InternalImage image = Apply(new ImageLoadOptions { Crop = options.Crop, Resize = options.Resize });
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

    private InternalImage Apply(ImageLoadOptions? options)
    {
        if (options is null) return this;
        InternalImage image = this;
        if (options.Crop is { } crop) image = image.Crop(crop);
        if (options.Resize is { } resize) image = image.Resize(resize);
        return image;
    }

    internal static byte[] ReadAll(Stream stream)
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

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe void InvertRow(byte* source, byte* destination, int width)
    {
        uint* sourcePixels = (uint*)source;
        uint* destinationPixels = (uint*)destination;
        for (int x = 0; x < width; x++)
        {
            destinationPixels[x] = sourcePixels[x] ^ 0x00FF_FFFFu;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe void FlipHorizontalRow(uint* sourcePixels, uint* destinationPixels, int width)
    {
        for (int x = 0; x < width; x++)
        {
            destinationPixels[x] = sourcePixels[width - 1 - x];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe void Rotate90ClockwiseRow(uint* sourcePixels, uint* destinationPixels, int width, int height, int y)
    {
        uint* sourceRow = sourcePixels + y * width;
        int destinationX = height - 1 - y;
        for (int x = 0; x < width; x++)
        {
            destinationPixels[x * height + destinationX] = sourceRow[x];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe void Rotate90CounterClockwiseRow(uint* sourcePixels, uint* destinationPixels, int width, int height, int y)
    {
        uint* sourceRow = sourcePixels + y * width;
        for (int x = 0; x < width; x++)
        {
            destinationPixels[(width - 1 - x) * height + y] = sourceRow[x];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe void Rotate180Row(uint* sourcePixels, uint* destinationPixels, int width, int height, int y)
    {
        uint* sourceRow = sourcePixels + (height - 1 - y) * width;
        uint* destinationRow = destinationPixels + y * width;
        for (int x = 0; x < width; x++)
        {
            destinationRow[x] = sourceRow[width - 1 - x];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe void ResizeNearestRow(uint* sourcePixels, uint* destinationPixels, int sourceWidth, int destinationWidth, int* sourceXs, int sourceY, int destinationY)
    {
        uint* sourceRow = sourcePixels + sourceY * sourceWidth;
        uint* destinationRow = destinationPixels + destinationY * destinationWidth;
        for (int x = 0; x < destinationWidth; x++)
        {
            destinationRow[x] = sourceRow[sourceXs[x]];
        }
    }

    private InternalImage ResizeNearest(int width, int height)
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

            return new InternalImage(width, height, destination);
        }

        ReadOnlySpan<uint> sourcePixels = MemoryMarshal.Cast<byte, uint>(_rgba);
        Span<uint> destinationPixels = MemoryMarshal.Cast<byte, uint>(destination.AsSpan());
        for (int y = 0; y < height; y++)
        {
            int sourceRow = sourceYs[y] * Width;
            int destinationRow = y * width;
            for (int x = 0; x < width; x++)
            {
                destinationPixels[destinationRow + x] = sourcePixels[sourceRow + sourceXs[x]];
            }
        }

        return new InternalImage(width, height, destination);
    }

    private InternalImage ResizeBilinear(int width, int height)
    {
        byte[] destination = new byte[CheckedPixelByteCount(width, height)];
        LinearContribution[] xMap = BuildLinearMap(Width, width);
        LinearContribution[] yMap = BuildLinearMap(Height, height);

        if (ShouldParallelize(width, height)) Parallel.For(0, height, ProcessRow);
        else for (int y = 0; y < height; y++) ProcessRow(y);

        return new InternalImage(width, height, destination);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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

    // resampled with premultiplied alpha, so transparent pixels lend no colour to their neighbours
    private InternalImage Convolve(ResamplePlan? horizontal, ResamplePlan? vertical)
    {
        if (horizontal == null && vertical == null) return Clone();
        bool alpha = HasTranslucency();
        InternalImage source = alpha ? Premultiplied() : this;
        InternalImage result;
        if (horizontal == null) result = source.ResampleVertical(vertical!);
        else if (vertical == null) result = source.ResampleHorizontal(horizontal);
        else
        {
            // the vertical pass is vectorised and the horizontal one is not, so the cheaper order goes
            long horizontalFirst = (long)Height * horizontal.Size * horizontal.Taps * 4 + (long)vertical.Size * horizontal.Size * vertical.Taps;
            long verticalFirst = (long)vertical.Size * Width * vertical.Taps + (long)vertical.Size * horizontal.Size * horizontal.Taps * 4;
            result = verticalFirst < horizontalFirst
                ? source.ResampleVertical(vertical).ResampleHorizontal(horizontal)
                : source.ResampleHorizontal(horizontal).ResampleVertical(vertical);
        }

        if (alpha) result.Unpremultiply();
        return result;
    }

    private InternalImage ResampleHorizontal(ResamplePlan plan)
    {
        int width = plan.Size;
        byte[] destination = NewPixels(CheckedPixelByteCount(width, Height));
        ForRows(Height, width, Row);
        return new InternalImage(width, Height, destination);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Row(int y)
        {
            fixed (byte* source = _rgba, target = destination)
            fixed (int* start = plan.Start, count = plan.Count, offset = plan.Offset, weights = plan.Weights)
            {
                byte* row = source + (long)y * Width * 4;
                uint* output = (uint*)(target + (long)y * width * 4);
                for (int x = 0; x < width; x++)
                {
                    uint* p = (uint*)row + start[x];
                    int* w = weights + offset[x];
                    var sum = Vector128.Create(ResampleHalf);
                    for (int i = 0, n = count[x]; i < n; i++)
                        sum += Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalarUnsafe(p[i]).AsByte())).AsInt32() * w[i];
                    sum = Vector128.Min(Vector128.Max(Vector128.ShiftRightArithmetic(sum, ResampleShift), Vector128<int>.Zero), Vector128.Create(255));
                    output[x] = Vector128.Narrow(Vector128.Narrow(sum.AsUInt32(), sum.AsUInt32()), Vector128<ushort>.Zero).AsUInt32().ToScalar();
                }
            }
        }
    }

    private InternalImage ResampleVertical(ResamplePlan plan)
    {
        int height = plan.Size, n = Width * 4;
        byte[] destination = NewPixels(CheckedPixelByteCount(Width, height));
        ForRows(height, Width, Row);
        return new InternalImage(Width, height, destination);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Row(int y)
        {
            int[] sums = ArrayPool<int>.Shared.Rent(n);
            fixed (byte* source = _rgba, target = destination)
            fixed (int* acc = sums)
            {
                new Span<int>(acc, n).Fill(ResampleHalf);
                for (int t = 0, start = plan.Start[y], offset = plan.Offset[y]; t < plan.Count[y]; t++)
                    AccumulateRow(acc, source + (long)(start + t) * n, plan.Weights[offset + t], n);
                StoreRow(acc, target + (long)y * n, n);
            }

            ArrayPool<int>.Shared.Return(sums);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void AccumulateRow(int* acc, byte* row, int weight, int n)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var w = new Vector<int>(weight);
            int lanes = Vector<int>.Count;
            for (; i <= n - Vector<byte>.Count; i += Vector<byte>.Count)
            {
                Vector.Widen(Unsafe.ReadUnaligned<Vector<byte>>(row + i), out Vector<ushort> low, out Vector<ushort> high);
                Vector.Widen(low, out Vector<uint> a, out Vector<uint> b);
                Vector.Widen(high, out Vector<uint> c, out Vector<uint> d);
                int* s = acc + i;
                Unsafe.WriteUnaligned(s, Unsafe.ReadUnaligned<Vector<int>>(s) + Vector.AsVectorInt32(a) * w);
                Unsafe.WriteUnaligned(s + lanes, Unsafe.ReadUnaligned<Vector<int>>(s + lanes) + Vector.AsVectorInt32(b) * w);
                Unsafe.WriteUnaligned(s + 2 * lanes, Unsafe.ReadUnaligned<Vector<int>>(s + 2 * lanes) + Vector.AsVectorInt32(c) * w);
                Unsafe.WriteUnaligned(s + 3 * lanes, Unsafe.ReadUnaligned<Vector<int>>(s + 3 * lanes) + Vector.AsVectorInt32(d) * w);
            }
        }

        for (; i < n; i++) acc[i] += row[i] * weight;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void StoreRow(int* acc, byte* row, int n)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var max = new Vector<int>(255);
            int lanes = Vector<int>.Count;
            for (; i <= n - Vector<byte>.Count; i += Vector<byte>.Count)
            {
                var a = Vector.AsVectorUInt32(Vector.Min(Vector.Max(Vector.ShiftRightArithmetic(Unsafe.ReadUnaligned<Vector<int>>(acc + i), ResampleShift), Vector<int>.Zero), max));
                var b = Vector.AsVectorUInt32(Vector.Min(Vector.Max(Vector.ShiftRightArithmetic(Unsafe.ReadUnaligned<Vector<int>>(acc + i + lanes), ResampleShift), Vector<int>.Zero), max));
                var c = Vector.AsVectorUInt32(Vector.Min(Vector.Max(Vector.ShiftRightArithmetic(Unsafe.ReadUnaligned<Vector<int>>(acc + i + 2 * lanes), ResampleShift), Vector<int>.Zero), max));
                var d = Vector.AsVectorUInt32(Vector.Min(Vector.Max(Vector.ShiftRightArithmetic(Unsafe.ReadUnaligned<Vector<int>>(acc + i + 3 * lanes), ResampleShift), Vector<int>.Zero), max));
                Unsafe.WriteUnaligned(row + i, Vector.Narrow(Vector.Narrow(a, b), Vector.Narrow(c, d)));
            }
        }

        for (; i < n; i++) row[i] = (byte)Clip(acc[i]);
    }

    private static uint Clip(int value)
    {
        value >>= ResampleShift;
        return (uint)(value < 0 ? 0 : value > 255 ? 255 : value);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool HasTranslucency()
    {
        ReadOnlySpan<uint> pixels = MemoryMarshal.Cast<byte, uint>(_rgba);
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var opaque = new Vector<uint>(0xFF000000u);
            for (; i <= pixels.Length - Vector<uint>.Count; i += Vector<uint>.Count)
                if (!Vector.EqualsAll(new Vector<uint>(pixels[i..]) & opaque, opaque)) return true;
        }

        for (; i < pixels.Length; i++)
            if (pixels[i] < 0xFF000000u) return true;
        return false;
    }

    private InternalImage Premultiplied()
    {
        byte[] destination = NewPixels(_rgba.Length);
        ForRows(Height, Width, Row);
        return new InternalImage(Width, Height, destination);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Row(int y)
        {
            for (int i = y * Width * 4, end = i + Width * 4; i < end; i += 4)
            {
                int a = _rgba[i + 3];
                destination[i] = (byte)((_rgba[i] * a + 127) / 255);
                destination[i + 1] = (byte)((_rgba[i + 1] * a + 127) / 255);
                destination[i + 2] = (byte)((_rgba[i + 2] * a + 127) / 255);
                destination[i + 3] = (byte)a;
            }
        }
    }

    private void Unpremultiply()
    {
        ForRows(Height, Width, Row);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Row(int y)
        {
            for (int i = y * Width * 4, end = i + Width * 4; i < end; i += 4)
            {
                int a = _rgba[i + 3];
                if (a == 255) continue;
                if (a == 0)
                {
                    _rgba[i] = _rgba[i + 1] = _rgba[i + 2] = 0;
                    continue;
                }

                _rgba[i] = (byte)Math.Min(255, (_rgba[i] * 255 + a / 2) / a);
                _rgba[i + 1] = (byte)Math.Min(255, (_rgba[i + 1] * 255 + a / 2) / a);
                _rgba[i + 2] = (byte)Math.Min(255, (_rgba[i + 2] * 255 + a / 2) / a);
            }
        }
    }

    /// <summary>This image on a larger canvas of the given colour, its top left corner at x, y.</summary>
    public InternalImage Pad(int width, int height, int x, int y, ColorRgba background)
    {
        byte[] pixels = new byte[CheckedPixelByteCount(width, height)];
        Fill(pixels, background);
        for (int row = 0; row < Height; row++)
            Buffer.BlockCopy(_rgba, row * Width * 4, pixels, ((y + row) * width + x) * 4, Width * 4);
        return new InternalImage(width, height, pixels);
    }

    private static void ForRows(int rows, int width, Action<int> row)
    {
        if (ShouldParallelize(width, rows)) Parallel.For(0, rows, row);
        else for (int y = 0; y < rows; y++) row(y);
    }

    private static byte[] NewPixels(int length) => GC.AllocateUninitializedArray<byte>(length);

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

    internal static bool ShouldParallelize(int width, int height) =>
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

    private static void Fill(byte[] destination, ColorRgba color)
    {
        if (color == default) return;
        uint packed = (uint)(color.R | (color.G << 8) | (color.B << 16) | (color.A << 24));
        Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(destination.AsSpan());
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

    private static ResamplePlan LanczosPlan(int sourceSize, int destinationSize)
    {
        double filterScale = Math.Max(1, (double)sourceSize / destinationSize);
        return BuildPlan(sourceSize, destinationSize, 3 * filterScale, x => Lanczos(x / filterScale));
    }

    // for each destination pixel, the run of source pixels it is made from and their weights; edges are clamped
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ResamplePlan BuildPlan(int sourceSize, int destinationSize, double support, Func<double, double> kernel)
    {
        double scale = (double)sourceSize / destinationSize;
        int[] start = new int[destinationSize], count = new int[destinationSize], offset = new int[destinationSize];
        List<int> weights = new(destinationSize * ((int)Math.Ceiling(support) * 2 + 1));
        double[] local = new double[Math.Min(sourceSize, (int)Math.Ceiling(support) * 2 + 2)];
        int[] fixedLocal = new int[local.Length];
        for (int d = 0; d < destinationSize; d++)
        {
            double center = (d + 0.5) * scale - 0.5;
            int left = (int)Math.Ceiling(center - support), right = (int)Math.Floor(center + support);
            int lo = Math.Clamp(left, 0, sourceSize - 1), n = Math.Clamp(right, 0, sourceSize - 1) - lo + 1;
            Array.Clear(local, 0, n);
            double sum = 0;
            for (int s = left; s <= right; s++)
            {
                double w = kernel(center - s);
                local[Math.Clamp(s, 0, sourceSize - 1) - lo] += w;
                sum += w;
            }

            if (Math.Abs(sum) < 1e-12)
            {
                Array.Clear(local, 0, n);
                local[Math.Clamp((int)Math.Round(center), lo, lo + n - 1) - lo] = sum = 1;
            }

            int total = 0, strongest = 0;
            for (int i = 0; i < n; i++)
            {
                fixedLocal[i] = (int)Math.Round(local[i] / sum * ResampleOne);
                total += fixedLocal[i];
                if (Math.Abs(fixedLocal[i]) > Math.Abs(fixedLocal[strongest])) strongest = i;
            }

            fixedLocal[strongest] += ResampleOne - total;
            int first = 0, last = n - 1;
            while (first < last && fixedLocal[first] == 0) first++;
            while (last > first && fixedLocal[last] == 0) last--;
            start[d] = lo + first;
            count[d] = last - first + 1;
            offset[d] = weights.Count;
            for (int i = first; i <= last; i++) weights.Add(fixedLocal[i]);
        }

        return new ResamplePlan(destinationSize, start, count, offset, weights.ToArray());
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
    private static int DivideLinearFixed(long value) =>
        (int)((value + (value >= 0 ? LinearHalf : -LinearHalf)) / LinearOne);

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

    private sealed record ResamplePlan(int Size, int[] Start, int[] Count, int[] Offset, int[] Weights)
    {
        public int Taps => Math.Max(1, Weights.Length / Size);
    }

    // ── Drawing ─────────────────────────────────────────────────────────────

    public InternalImage DrawLine(int x1, int y1, int x2, int y2, int lineWidth, ColorRgba color) {
        var dst = Clone();
        int half = Math.Max(0, lineWidth / 2);
        int dx = Math.Abs(x2 - x1), dy = Math.Abs(y2 - y1);
        int sx = x1 < x2 ? 1 : -1, sy = y1 < y2 ? 1 : -1;
        int err = dx - dy, x = x1, y = y1;
        while (true) {
            for (int oy = -half; oy <= half; oy++)
                for (int ox = -half; ox <= half; ox++) {
                    int px = x + ox, py = y + oy;
                    if ((uint)px < (uint)dst.Width && (uint)py < (uint)dst.Height)
                        dst[px, py] = color;
                }
            if (x == x2 && y == y2) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x += sx; }
            if (e2 < dx)  { err += dx; y += sy; }
        }
        return dst;
    }

    public InternalImage DrawBox(int x1, int y1, int x2, int y2, int borderWidth, ColorRgba borderColor, bool filled, ColorRgba fillColor) {
        var dst = Clone();
        int left = Math.Min(x1, x2), right = Math.Max(x1, x2);
        int top = Math.Min(y1, y2), bottom = Math.Max(y1, y2);
        if (filled) {
            int fx1 = Math.Max(0, left + borderWidth), fx2 = Math.Min(Width - 1, right - borderWidth);
            int fy1 = Math.Max(0, top + borderWidth), fy2 = Math.Min(Height - 1, bottom - borderWidth);
            for (int py = fy1; py <= fy2; py++)
                for (int px = fx1; px <= fx2; px++)
                    dst[px, py] = fillColor;
        }
        for (int w = 0; w < borderWidth; w++) {
            int lx = left + w, rx = right - w, ty = top + w, by = bottom - w;
            for (int px = Math.Max(0, lx); px <= Math.Min(Width - 1, rx); px++) {
                if ((uint)ty < (uint)Height) dst[px, ty] = borderColor;
                if ((uint)by < (uint)Height) dst[px, by] = borderColor;
            }
            for (int py = Math.Max(0, ty); py <= Math.Min(Height - 1, by); py++) {
                if ((uint)lx < (uint)Width) dst[lx, py] = borderColor;
                if ((uint)rx < (uint)Width) dst[rx, py] = borderColor;
            }
        }
        return dst;
    }

    public InternalImage DrawText(int x, int y, string text, int fontSizeInPixels, ColorRgba color, bool sansSerif) {
        var dst = Clone();
        int scale = Math.Max(1, fontSizeInPixels / BitmapFont.GlyphHeight);
        int cx = x;
        foreach (char ch in text) {
            byte[] glyph = BitmapFont.GetGlyph(ch, sansSerif);
            int gw = BitmapFont.GlyphWidth, gh = BitmapFont.GlyphHeight;
            for (int gy = 0; gy < gh; gy++)
                for (int gx = 0; gx < gw; gx++)
                    if ((glyph[gy] & (1 << (gw - 1 - gx))) != 0)
                        for (int sy = 0; sy < scale; sy++)
                            for (int sx = 0; sx < scale; sx++) {
                                int px = cx + gx * scale + sx, py = y + gy * scale + sy;
                                if ((uint)px < (uint)dst.Width && (uint)py < (uint)dst.Height)
                                    dst[px, py] = color;
                            }
            cx += (gw + 1) * scale;
        }
        return dst;
    }

    internal static InternalImage Create(int width, int height) {
        return new InternalImage(width, height, new byte[CheckedPixelByteCount(width, height)]);
    }

    // ── Bitmap font (5×7) ────────────────────────────────────────────────────

    internal static class BitmapFont {
        public const int GlyphWidth = 5;
        public const int GlyphHeight = 7;

        // Each glyph: 7 bytes, each byte is a 5-bit row (bit4=leftmost pixel)
        private static readonly Dictionary<char, byte[]> Serif = BuildSerif();
        private static readonly Dictionary<char, byte[]> SansSerif = BuildSans();

        public static byte[] GetGlyph(char ch, bool sansSerif) {
            var dict = sansSerif ? SansSerif : Serif;
            return dict.TryGetValue(ch, out var g) ? g : dict.TryGetValue('?', out var q) ? q : _unknown;
        }

        private static readonly byte[] _unknown = [0x0E, 0x11, 0x01, 0x06, 0x04, 0x00, 0x04];

        // Compact 5×7 sans-serif glyphs for printable ASCII 32-126
        private static Dictionary<char, byte[]> BuildSans() => new() {
            [' '] = [0x00,0x00,0x00,0x00,0x00,0x00,0x00],
            ['!'] = [0x04,0x04,0x04,0x04,0x00,0x00,0x04],
            ['"'] = [0x0A,0x0A,0x00,0x00,0x00,0x00,0x00],
            ['#'] = [0x0A,0x1F,0x0A,0x0A,0x1F,0x0A,0x00],
            ['$'] = [0x04,0x0F,0x14,0x0E,0x05,0x1E,0x04],
            ['%'] = [0x18,0x19,0x02,0x04,0x08,0x13,0x03],
            ['&'] = [0x0C,0x12,0x14,0x08,0x15,0x12,0x0D],
            ['\'']= [0x04,0x04,0x00,0x00,0x00,0x00,0x00],
            ['('] = [0x02,0x04,0x08,0x08,0x08,0x04,0x02],
            [')'] = [0x08,0x04,0x02,0x02,0x02,0x04,0x08],
            ['*'] = [0x00,0x04,0x15,0x0E,0x15,0x04,0x00],
            ['+'] = [0x00,0x04,0x04,0x1F,0x04,0x04,0x00],
            [','] = [0x00,0x00,0x00,0x00,0x06,0x04,0x08],
            ['-'] = [0x00,0x00,0x00,0x1F,0x00,0x00,0x00],
            ['.'] = [0x00,0x00,0x00,0x00,0x00,0x06,0x06],
            ['/'] = [0x01,0x02,0x02,0x04,0x08,0x08,0x10],
            ['0'] = [0x0E,0x11,0x13,0x15,0x19,0x11,0x0E],
            ['1'] = [0x04,0x0C,0x04,0x04,0x04,0x04,0x0E],
            ['2'] = [0x0E,0x11,0x01,0x06,0x08,0x10,0x1F],
            ['3'] = [0x1F,0x02,0x04,0x06,0x01,0x11,0x0E],
            ['4'] = [0x02,0x06,0x0A,0x12,0x1F,0x02,0x02],
            ['5'] = [0x1F,0x10,0x1E,0x01,0x01,0x11,0x0E],
            ['6'] = [0x06,0x08,0x10,0x1E,0x11,0x11,0x0E],
            ['7'] = [0x1F,0x01,0x02,0x04,0x08,0x08,0x08],
            ['8'] = [0x0E,0x11,0x11,0x0E,0x11,0x11,0x0E],
            ['9'] = [0x0E,0x11,0x11,0x0F,0x01,0x02,0x0C],
            [':'] = [0x00,0x06,0x06,0x00,0x06,0x06,0x00],
            [';'] = [0x00,0x06,0x06,0x00,0x06,0x04,0x08],
            ['<'] = [0x02,0x04,0x08,0x10,0x08,0x04,0x02],
            ['='] = [0x00,0x00,0x1F,0x00,0x1F,0x00,0x00],
            ['>'] = [0x08,0x04,0x02,0x01,0x02,0x04,0x08],
            ['?'] = [0x0E,0x11,0x01,0x06,0x04,0x00,0x04],
            ['@'] = [0x0E,0x11,0x01,0x0D,0x15,0x15,0x0E],
            ['A'] = [0x04,0x0A,0x11,0x11,0x1F,0x11,0x11],
            ['B'] = [0x1E,0x11,0x11,0x1E,0x11,0x11,0x1E],
            ['C'] = [0x0E,0x11,0x10,0x10,0x10,0x11,0x0E],
            ['D'] = [0x1C,0x12,0x11,0x11,0x11,0x12,0x1C],
            ['E'] = [0x1F,0x10,0x10,0x1E,0x10,0x10,0x1F],
            ['F'] = [0x1F,0x10,0x10,0x1E,0x10,0x10,0x10],
            ['G'] = [0x0E,0x11,0x10,0x17,0x11,0x11,0x0F],
            ['H'] = [0x11,0x11,0x11,0x1F,0x11,0x11,0x11],
            ['I'] = [0x0E,0x04,0x04,0x04,0x04,0x04,0x0E],
            ['J'] = [0x07,0x02,0x02,0x02,0x02,0x12,0x0C],
            ['K'] = [0x11,0x12,0x14,0x18,0x14,0x12,0x11],
            ['L'] = [0x10,0x10,0x10,0x10,0x10,0x10,0x1F],
            ['M'] = [0x11,0x1B,0x15,0x15,0x11,0x11,0x11],
            ['N'] = [0x11,0x11,0x19,0x15,0x13,0x11,0x11],
            ['O'] = [0x0E,0x11,0x11,0x11,0x11,0x11,0x0E],
            ['P'] = [0x1E,0x11,0x11,0x1E,0x10,0x10,0x10],
            ['Q'] = [0x0E,0x11,0x11,0x11,0x15,0x12,0x0D],
            ['R'] = [0x1E,0x11,0x11,0x1E,0x14,0x12,0x11],
            ['S'] = [0x0F,0x10,0x10,0x0E,0x01,0x01,0x1E],
            ['T'] = [0x1F,0x04,0x04,0x04,0x04,0x04,0x04],
            ['U'] = [0x11,0x11,0x11,0x11,0x11,0x11,0x0E],
            ['V'] = [0x11,0x11,0x11,0x11,0x0A,0x0A,0x04],
            ['W'] = [0x11,0x11,0x15,0x15,0x15,0x0A,0x0A],
            ['X'] = [0x11,0x11,0x0A,0x04,0x0A,0x11,0x11],
            ['Y'] = [0x11,0x11,0x0A,0x04,0x04,0x04,0x04],
            ['Z'] = [0x1F,0x01,0x02,0x04,0x08,0x10,0x1F],
            ['['] = [0x0E,0x08,0x08,0x08,0x08,0x08,0x0E],
            ['\\']= [0x10,0x08,0x08,0x04,0x02,0x02,0x01],
            [']'] = [0x0E,0x02,0x02,0x02,0x02,0x02,0x0E],
            ['^'] = [0x04,0x0A,0x11,0x00,0x00,0x00,0x00],
            ['_'] = [0x00,0x00,0x00,0x00,0x00,0x00,0x1F],
            ['`'] = [0x08,0x04,0x00,0x00,0x00,0x00,0x00],
            ['a'] = [0x00,0x00,0x0E,0x01,0x0F,0x11,0x0F],
            ['b'] = [0x10,0x10,0x16,0x19,0x11,0x11,0x1E],
            ['c'] = [0x00,0x00,0x0E,0x10,0x10,0x11,0x0E],
            ['d'] = [0x01,0x01,0x0D,0x13,0x11,0x11,0x0F],
            ['e'] = [0x00,0x00,0x0E,0x11,0x1F,0x10,0x0E],
            ['f'] = [0x06,0x09,0x08,0x1C,0x08,0x08,0x08],
            ['g'] = [0x00,0x0F,0x11,0x11,0x0F,0x01,0x0E],
            ['h'] = [0x10,0x10,0x16,0x19,0x11,0x11,0x11],
            ['i'] = [0x04,0x00,0x0C,0x04,0x04,0x04,0x0E],
            ['j'] = [0x02,0x00,0x06,0x02,0x02,0x12,0x0C],
            ['k'] = [0x10,0x10,0x12,0x14,0x18,0x14,0x12],
            ['l'] = [0x0C,0x04,0x04,0x04,0x04,0x04,0x0E],
            ['m'] = [0x00,0x00,0x1A,0x15,0x15,0x11,0x11],
            ['n'] = [0x00,0x00,0x16,0x19,0x11,0x11,0x11],
            ['o'] = [0x00,0x00,0x0E,0x11,0x11,0x11,0x0E],
            ['p'] = [0x00,0x1E,0x11,0x11,0x1E,0x10,0x10],
            ['q'] = [0x00,0x0F,0x11,0x11,0x0F,0x01,0x01],
            ['r'] = [0x00,0x00,0x16,0x19,0x10,0x10,0x10],
            ['s'] = [0x00,0x00,0x0E,0x10,0x0E,0x01,0x1E],
            ['t'] = [0x08,0x08,0x1C,0x08,0x08,0x09,0x06],
            ['u'] = [0x00,0x00,0x11,0x11,0x11,0x13,0x0D],
            ['v'] = [0x00,0x00,0x11,0x11,0x11,0x0A,0x04],
            ['w'] = [0x00,0x00,0x11,0x15,0x15,0x15,0x0A],
            ['x'] = [0x00,0x00,0x11,0x0A,0x04,0x0A,0x11],
            ['y'] = [0x00,0x11,0x11,0x0F,0x01,0x11,0x0E],
            ['z'] = [0x00,0x00,0x1F,0x02,0x04,0x08,0x1F],
            ['{'] = [0x03,0x04,0x04,0x08,0x04,0x04,0x03],
            ['|'] = [0x04,0x04,0x04,0x04,0x04,0x04,0x04],
            ['}'] = [0x18,0x04,0x04,0x02,0x04,0x04,0x18],
            ['~'] = [0x00,0x08,0x15,0x02,0x00,0x00,0x00],
        };

        // Serif variant: same shapes but with small serifs on I, 1, etc.
        private static Dictionary<char, byte[]> BuildSerif() {
            var d = BuildSans();
            d['I'] = [0x1F,0x04,0x04,0x04,0x04,0x04,0x1F];
            d['1'] = [0x06,0x0E,0x06,0x06,0x06,0x06,0x1F];
            d['i'] = [0x04,0x00,0x0E,0x04,0x04,0x04,0x1F];
            d['l'] = [0x0C,0x04,0x04,0x04,0x04,0x04,0x1F];
            return d;
        }
    }
}
