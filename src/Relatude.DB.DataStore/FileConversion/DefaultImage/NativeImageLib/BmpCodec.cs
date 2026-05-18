using System.Buffers.Binary;
using System.Numerics;

namespace Relatude.DB.FileConversion.DefaultImage.NativeImageLib;

internal sealed class BmpCodec : IImageCodec
{
    private const int BiRgb = 0;
    private const int BiBitFields = 3;

    public ImageFormat Format => ImageFormat.Bmp;

    public bool CanDecode(ReadOnlySpan<byte> header)
    {
        return header.Length >= 2 && header[0] == (byte)'B' && header[1] == (byte)'M';
    }

    public PureImage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 54 || !CanDecode(data))
        {
            throw new ImageFormatException("Invalid BMP header.");
        }

        int pixelOffset = ReadInt32(data, 10);
        int dibSize = ReadInt32(data, 14);
        if (dibSize < 40 || data.Length < 14 + dibSize || pixelOffset < 14 + dibSize)
        {
            throw new ImageFormatException("Unsupported BMP DIB header.");
        }

        int width = ReadInt32(data, 18);
        int rawHeight = ReadInt32(data, 22);
        if (width <= 0 || rawHeight == 0)
        {
            throw new ImageFormatException("Invalid BMP dimensions.");
        }

        bool topDown = rawHeight < 0;
        int height = Math.Abs(rawHeight);
        int planes = ReadInt16(data, 26);
        int bitsPerPixel = ReadInt16(data, 28);
        int compression = ReadInt32(data, 30);
        if (planes != 1)
        {
            throw new ImageFormatException("Invalid BMP plane count.");
        }

        if (compression != BiRgb && compression != BiBitFields)
        {
            throw new ImageFormatException("Only uncompressed BMP files are supported.");
        }

        uint redMask = 0x7c00;
        uint greenMask = 0x03e0;
        uint blueMask = 0x001f;
        uint alphaMask = 0;
        int maskOffset = 14 + dibSize;
        if (compression == BiBitFields)
        {
            if (data.Length < maskOffset + 12)
            {
                throw new ImageFormatException("BMP bitfield masks are missing.");
            }

            redMask = ReadUInt32(data, maskOffset);
            greenMask = ReadUInt32(data, maskOffset + 4);
            blueMask = ReadUInt32(data, maskOffset + 8);
            if (data.Length >= maskOffset + 16)
            {
                alphaMask = ReadUInt32(data, maskOffset + 12);
            }
        }
        else if (bitsPerPixel == 32)
        {
            redMask = 0x00ff0000;
            greenMask = 0x0000ff00;
            blueMask = 0x000000ff;
            alphaMask = 0xff000000;
        }
        else if (bitsPerPixel == 16)
        {
            redMask = 0x7c00;
            greenMask = 0x03e0;
            blueMask = 0x001f;
        }

        int rowStride = ((width * bitsPerPixel + 31) / 32) * 4;
        if (pixelOffset < 0 || pixelOffset + rowStride * height > data.Length)
        {
            throw new ImageFormatException("BMP pixel data is truncated.");
        }

        ColorRgba[] palette = ReadPalette(data, dibSize, pixelOffset, bitsPerPixel);
        byte[] rgba = new byte[checked(width * height * 4)];
        bool sawNonZeroAlpha = false;

        for (int y = 0; y < height; y++)
        {
            int sourceY = topDown ? y : height - 1 - y;
            int row = pixelOffset + sourceY * rowStride;
            for (int x = 0; x < width; x++)
            {
                int destination = (y * width + x) * 4;
                ColorRgba color = bitsPerPixel switch
                {
                    1 => palette[ReadPacked(data[row..(row + rowStride)], x, 1)],
                    4 => palette[ReadPacked(data[row..(row + rowStride)], x, 4)],
                    8 => palette[data[row + x]],
                    16 => DecodeBitFields(ReadUInt16(data, row + x * 2), redMask, greenMask, blueMask, alphaMask),
                    24 => new ColorRgba(data[row + x * 3 + 2], data[row + x * 3 + 1], data[row + x * 3], 255),
                    32 => DecodeBitFields(ReadUInt32(data, row + x * 4), redMask, greenMask, blueMask, alphaMask),
                    _ => throw new ImageFormatException($"Unsupported BMP bit depth: {bitsPerPixel}.")
                };

                rgba[destination] = color.R;
                rgba[destination + 1] = color.G;
                rgba[destination + 2] = color.B;
                rgba[destination + 3] = color.A;
                sawNonZeroAlpha |= color.A != 0;
            }
        }

        if (bitsPerPixel == 32 && !sawNonZeroAlpha)
        {
            for (int i = 3; i < rgba.Length; i += 4)
            {
                rgba[i] = 255;
            }
        }

        return new PureImage(width, height, rgba);
    }

    public void Encode(PureImage image, Stream stream, ImageSaveOptions options)
    {
        const int fileHeaderSize = 14;
        const int dibHeaderSize = 40;
        const int maskSize = 16;
        const int bitsPerPixel = 32;

        int rowStride = image.Width * 4;
        int imageSize = rowStride * image.Height;
        int pixelOffset = fileHeaderSize + dibHeaderSize + maskSize;
        int fileSize = pixelOffset + imageSize;

        Span<byte> header = stackalloc byte[pixelOffset];
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        WriteInt32(header, 2, fileSize);
        WriteInt32(header, 10, pixelOffset);
        WriteInt32(header, 14, dibHeaderSize);
        WriteInt32(header, 18, image.Width);
        WriteInt32(header, 22, -image.Height);
        WriteInt16(header, 26, 1);
        WriteInt16(header, 28, bitsPerPixel);
        WriteInt32(header, 30, BiBitFields);
        WriteInt32(header, 34, imageSize);
        WriteUInt32(header, 54, 0x00ff0000);
        WriteUInt32(header, 58, 0x0000ff00);
        WriteUInt32(header, 62, 0x000000ff);
        WriteUInt32(header, 66, 0xff000000);
        stream.Write(header);

        ReadOnlySpan<byte> pixels = image.Pixels;
        byte[] row = new byte[rowStride];
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                int source = (y * image.Width + x) * 4;
                int destination = x * 4;
                row[destination] = pixels[source + 2];
                row[destination + 1] = pixels[source + 1];
                row[destination + 2] = pixels[source];
                row[destination + 3] = pixels[source + 3];
            }

            stream.Write(row);
        }
    }

    private static ColorRgba[] ReadPalette(ReadOnlySpan<byte> data, int dibSize, int pixelOffset, int bitsPerPixel)
    {
        if (bitsPerPixel > 8)
        {
            return [];
        }

        int paletteOffset = 14 + dibSize;
        int paletteEntries = Math.Min(1 << bitsPerPixel, Math.Max(0, (pixelOffset - paletteOffset) / 4));
        if (paletteEntries == 0)
        {
            throw new ImageFormatException("Indexed BMP palette is missing.");
        }

        ColorRgba[] palette = new ColorRgba[paletteEntries];
        for (int i = 0; i < palette.Length; i++)
        {
            int offset = paletteOffset + i * 4;
            palette[i] = new ColorRgba(data[offset + 2], data[offset + 1], data[offset], 255);
        }

        return palette;
    }

    private static int ReadPacked(ReadOnlySpan<byte> row, int x, int bitDepth)
    {
        int bit = x * bitDepth;
        int value = row[bit / 8];
        int shift = 8 - bitDepth - bit % 8;
        return (value >> shift) & ((1 << bitDepth) - 1);
    }

    private static ColorRgba DecodeBitFields(uint value, uint redMask, uint greenMask, uint blueMask, uint alphaMask)
    {
        byte alpha = alphaMask == 0 ? (byte)255 : ExtractMasked(value, alphaMask);
        return new ColorRgba(
            ExtractMasked(value, redMask),
            ExtractMasked(value, greenMask),
            ExtractMasked(value, blueMask),
            alpha);
    }

    private static byte ExtractMasked(uint value, uint mask)
    {
        if (mask == 0)
        {
            return 0;
        }

        int shift = BitOperations.TrailingZeroCount(mask);
        uint normalized = (value & mask) >> shift;
        uint max = mask >> shift;
        return (byte)((normalized * 255 + max / 2) / max);
    }

    private static short ReadInt16(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadInt16LittleEndian(data[offset..]);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    }

    private static void WriteInt16(Span<byte> data, int offset, short value)
    {
        BinaryPrimitives.WriteInt16LittleEndian(data[offset..], value);
    }

    private static void WriteInt32(Span<byte> data, int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(data[offset..], value);
    }

    private static void WriteUInt32(Span<byte> data, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data[offset..], value);
    }
}
