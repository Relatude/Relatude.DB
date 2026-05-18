using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Relatude.DB.FileConversion.DefaultImage.NativeImageLib;

internal sealed class PngCodec : IImageCodec
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public ImageFormat Format => ImageFormat.Png;

    public bool CanDecode(ReadOnlySpan<byte> header)
    {
        return header.Length >= Signature.Length && header[..Signature.Length].SequenceEqual(Signature);
    }

    public PureImage Decode(ReadOnlySpan<byte> data)
    {
        if (!CanDecode(data))
        {
            throw new ImageFormatException("Invalid PNG signature.");
        }

        int offset = Signature.Length;
        int width = 0;
        int height = 0;
        int bitDepth = 0;
        int colorType = 0;
        int interlace = 0;
        byte[]? palette = null;
        byte[]? transparency = null;
        List<byte[]> idatChunks = [];

        while (offset + 12 <= data.Length)
        {
            int length = ReadInt32BigEndian(data, offset);
            if (length < 0 || offset + 12 + length > data.Length)
            {
                throw new ImageFormatException("PNG chunk is truncated.");
            }

            ReadOnlySpan<byte> typeBytes = data.Slice(offset + 4, 4);
            ReadOnlySpan<byte> payload = data.Slice(offset + 8, length);
            VerifyCrc(data.Slice(offset + 4, 4 + length), ReadUInt32BigEndian(data, offset + 8 + length));
            string type = Encoding.ASCII.GetString(typeBytes);

            switch (type)
            {
                case "IHDR":
                    if (length != 13)
                    {
                        throw new ImageFormatException("Invalid PNG IHDR chunk.");
                    }

                    width = ReadInt32BigEndian(payload, 0);
                    height = ReadInt32BigEndian(payload, 4);
                    bitDepth = payload[8];
                    colorType = payload[9];
                    interlace = payload[12];
                    ValidateHeader(width, height, bitDepth, colorType, interlace);
                    break;

                case "PLTE":
                    palette = payload.ToArray();
                    break;

                case "tRNS":
                    transparency = payload.ToArray();
                    break;

                case "IDAT":
                    idatChunks.Add(payload.ToArray());
                    break;

                case "IEND":
                    return DecodePixels(width, height, bitDepth, colorType, palette, transparency, idatChunks);
            }

            offset += 12 + length;
        }

        throw new ImageFormatException("PNG image is missing IEND.");
    }

    public void Encode(PureImage image, Stream stream, ImageSaveOptions options)
    {
        stream.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        WriteInt32BigEndian(ihdr, 0, image.Width);
        WriteInt32BigEndian(ihdr, 4, image.Height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(stream, "IHDR", ihdr);

        byte[] raw = BuildFilteredScanlines(image);
        using MemoryStream compressed = new();
        CompressionLevel level = options.PngCompressionLevel <= 0
            ? CompressionLevel.NoCompression
            : options.PngCompressionLevel <= 3
                ? CompressionLevel.Fastest
                : options.PngCompressionLevel <= 6
                    ? CompressionLevel.Optimal
                    : CompressionLevel.SmallestSize;

        using (ZLibStream zlib = new(compressed, level, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        WriteChunk(stream, "IDAT", compressed.ToArray());
        WriteChunk(stream, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static PureImage DecodePixels(
        int width,
        int height,
        int bitDepth,
        int colorType,
        byte[]? palette,
        byte[]? transparency,
        List<byte[]> idatChunks)
    {
        if (idatChunks.Count == 0)
        {
            throw new ImageFormatException("PNG image has no IDAT data.");
        }

        int channels = ChannelsForColorType(colorType);
        int bitsPerPixel = channels * bitDepth;
        int rowBytes = checked((width * bitsPerPixel + 7) / 8);
        int filterBpp = Math.Max(1, (bitsPerPixel + 7) / 8);
        byte[] compressed = Combine(idatChunks);
        byte[] inflated;

        using (MemoryStream source = new(compressed))
        using (ZLibStream zlib = new(source, CompressionMode.Decompress))
        using (MemoryStream output = new())
        {
            zlib.CopyTo(output);
            inflated = output.ToArray();
        }

        int expected = checked((rowBytes + 1) * height);
        if (inflated.Length < expected)
        {
            throw new ImageFormatException("PNG image data is truncated.");
        }

        if (bitDepth == 8 && colorType == 6)
        {
            return DecodeRgba8(width, height, rowBytes, filterBpp, inflated);
        }

        if (bitDepth == 8 && colorType == 2)
        {
            return DecodeRgb8(width, height, rowBytes, filterBpp, inflated, transparency);
        }

        byte[] unfiltered = new byte[rowBytes * height];
        byte[] previous = new byte[rowBytes];
        byte[] current = new byte[rowBytes];
        int sourceOffset = 0;

        for (int y = 0; y < height; y++)
        {
            int filter = inflated[sourceOffset++];
            inflated.AsSpan(sourceOffset, rowBytes).CopyTo(current);
            sourceOffset += rowBytes;
            Unfilter(current, previous, filter, filterBpp);
            current.CopyTo(unfiltered, y * rowBytes);
            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        byte[] rgba = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = unfiltered.AsSpan(y * rowBytes, rowBytes);
            for (int x = 0; x < width; x++)
            {
                ColorRgba color = ReadPixel(row, x, bitDepth, colorType, palette, transparency);
                int destination = (y * width + x) * 4;
                rgba[destination] = color.R;
                rgba[destination + 1] = color.G;
                rgba[destination + 2] = color.B;
                rgba[destination + 3] = color.A;
            }
        }

        return new PureImage(width, height, rgba);
    }

    private static PureImage DecodeRgba8(int width, int height, int rowBytes, int filterBpp, byte[] inflated)
    {
        byte[] rgba = new byte[checked(width * height * 4)];
        byte[] previous = new byte[rowBytes];
        byte[] current = new byte[rowBytes];
        int sourceOffset = 0;

        for (int y = 0; y < height; y++)
        {
            int filter = inflated[sourceOffset++];
            inflated.AsSpan(sourceOffset, rowBytes).CopyTo(current);
            sourceOffset += rowBytes;
            Unfilter(current, previous, filter, filterBpp);
            current.CopyTo(rgba, y * rowBytes);
            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        return new PureImage(width, height, rgba);
    }

    private static PureImage DecodeRgb8(int width, int height, int rowBytes, int filterBpp, byte[] inflated, byte[]? transparency)
    {
        byte[] rgba = new byte[checked(width * height * 4)];
        byte[] previous = new byte[rowBytes];
        byte[] current = new byte[rowBytes];
        int transparentR = -1;
        int transparentG = -1;
        int transparentB = -1;
        if (transparency is { Length: >= 6 })
        {
            transparentR = BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(0, 2));
            transparentG = BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2, 2));
            transparentB = BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4, 2));
        }

        int sourceOffset = 0;
        for (int y = 0; y < height; y++)
        {
            int filter = inflated[sourceOffset++];
            inflated.AsSpan(sourceOffset, rowBytes).CopyTo(current);
            sourceOffset += rowBytes;
            Unfilter(current, previous, filter, filterBpp);

            int destination = y * width * 4;
            int source = 0;
            for (int x = 0; x < width; x++)
            {
                byte r = current[source++];
                byte g = current[source++];
                byte b = current[source++];
                rgba[destination++] = r;
                rgba[destination++] = g;
                rgba[destination++] = b;
                rgba[destination++] = r == transparentR && g == transparentG && b == transparentB ? (byte)0 : (byte)255;
            }

            (previous, current) = (current, previous);
            Array.Clear(current);
        }

        return new PureImage(width, height, rgba);
    }

    private static ColorRgba ReadPixel(
        ReadOnlySpan<byte> row,
        int x,
        int bitDepth,
        int colorType,
        byte[]? palette,
        byte[]? transparency)
    {
        return colorType switch
        {
            0 => ReadGrayscale(row, x, bitDepth, transparency),
            2 => ReadTruecolor(row, x, bitDepth, transparency),
            3 => ReadIndexed(row, x, bitDepth, palette, transparency),
            4 => ReadGrayscaleAlpha(row, x, bitDepth),
            6 => ReadRgba(row, x, bitDepth),
            _ => throw new ImageFormatException($"Unsupported PNG color type: {colorType}.")
        };
    }

    private static ColorRgba ReadGrayscale(ReadOnlySpan<byte> row, int x, int bitDepth, byte[]? transparency)
    {
        int sample = ReadSample(row, x, bitDepth);
        byte gray = ScaleSample(sample, bitDepth);
        byte alpha = 255;
        if (transparency is { Length: >= 2 } && sample == BinaryPrimitives.ReadUInt16BigEndian(transparency))
        {
            alpha = 0;
        }

        return new ColorRgba(gray, gray, gray, alpha);
    }

    private static ColorRgba ReadTruecolor(ReadOnlySpan<byte> row, int x, int bitDepth, byte[]? transparency)
    {
        int bytesPerSample = bitDepth / 8;
        int offset = x * 3 * bytesPerSample;
        int r = ReadComponent(row, offset, bitDepth);
        int g = ReadComponent(row, offset + bytesPerSample, bitDepth);
        int b = ReadComponent(row, offset + bytesPerSample * 2, bitDepth);
        byte alpha = 255;
        if (transparency is { Length: >= 6 }
            && r == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(0, 2))
            && g == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2, 2))
            && b == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4, 2)))
        {
            alpha = 0;
        }

        return new ColorRgba(ScaleSample(r, bitDepth), ScaleSample(g, bitDepth), ScaleSample(b, bitDepth), alpha);
    }

    private static ColorRgba ReadIndexed(ReadOnlySpan<byte> row, int x, int bitDepth, byte[]? palette, byte[]? transparency)
    {
        if (palette is null || palette.Length % 3 != 0)
        {
            throw new ImageFormatException("Indexed PNG palette is missing or invalid.");
        }

        int index = ReadPacked(row, x, bitDepth);
        if (index * 3 + 2 >= palette.Length)
        {
            throw new ImageFormatException("Indexed PNG pixel references a missing palette entry.");
        }

        byte alpha = transparency is not null && index < transparency.Length ? transparency[index] : (byte)255;
        return new ColorRgba(palette[index * 3], palette[index * 3 + 1], palette[index * 3 + 2], alpha);
    }

    private static ColorRgba ReadGrayscaleAlpha(ReadOnlySpan<byte> row, int x, int bitDepth)
    {
        int bytesPerSample = bitDepth / 8;
        int offset = x * 2 * bytesPerSample;
        int graySample = ReadComponent(row, offset, bitDepth);
        int alphaSample = ReadComponent(row, offset + bytesPerSample, bitDepth);
        byte gray = ScaleSample(graySample, bitDepth);
        return new ColorRgba(gray, gray, gray, ScaleSample(alphaSample, bitDepth));
    }

    private static ColorRgba ReadRgba(ReadOnlySpan<byte> row, int x, int bitDepth)
    {
        int bytesPerSample = bitDepth / 8;
        int offset = x * 4 * bytesPerSample;
        return new ColorRgba(
            ScaleSample(ReadComponent(row, offset, bitDepth), bitDepth),
            ScaleSample(ReadComponent(row, offset + bytesPerSample, bitDepth), bitDepth),
            ScaleSample(ReadComponent(row, offset + bytesPerSample * 2, bitDepth), bitDepth),
            ScaleSample(ReadComponent(row, offset + bytesPerSample * 3, bitDepth), bitDepth));
    }

    private static int ReadComponent(ReadOnlySpan<byte> row, int offset, int bitDepth)
    {
        return bitDepth == 16 ? BinaryPrimitives.ReadUInt16BigEndian(row[offset..]) : row[offset];
    }

    private static int ReadSample(ReadOnlySpan<byte> row, int x, int bitDepth)
    {
        return bitDepth >= 8 ? ReadComponent(row, x * (bitDepth / 8), bitDepth) : ReadPacked(row, x, bitDepth);
    }

    private static int ReadPacked(ReadOnlySpan<byte> row, int x, int bitDepth)
    {
        int bit = x * bitDepth;
        int value = row[bit / 8];
        int shift = 8 - bitDepth - bit % 8;
        return (value >> shift) & ((1 << bitDepth) - 1);
    }

    private static byte ScaleSample(int sample, int bitDepth)
    {
        return bitDepth switch
        {
            16 => (byte)(sample >> 8),
            8 => (byte)sample,
            _ => (byte)((sample * 255 + ((1 << bitDepth) - 1) / 2) / ((1 << bitDepth) - 1))
        };
    }

    private static void Unfilter(Span<byte> current, ReadOnlySpan<byte> previous, int filter, int bpp)
    {
        for (int i = 0; i < current.Length; i++)
        {
            int left = i >= bpp ? current[i - bpp] : 0;
            int up = previous[i];
            int upLeft = i >= bpp ? previous[i - bpp] : 0;
            int value = filter switch
            {
                0 => current[i],
                1 => current[i] + left,
                2 => current[i] + up,
                3 => current[i] + ((left + up) >> 1),
                4 => current[i] + Paeth(left, up, upLeft),
                _ => throw new ImageFormatException($"Invalid PNG filter type: {filter}.")
            };
            current[i] = (byte)value;
        }
    }

    private static byte[] BuildFilteredScanlines(PureImage image)
    {
        int stride = image.Width * 4;
        byte[] output = new byte[(stride + 1) * image.Height];
        byte[] previous = new byte[stride];
        byte[] row = new byte[stride];
        byte[] best = new byte[stride];
        byte[] candidate = new byte[stride];

        for (int y = 0; y < image.Height; y++)
        {
            image.Pixels.Slice(y * stride, stride).CopyTo(row);

            int bestFilter = 0;
            int bestScore = int.MaxValue;
            for (int filter = 0; filter <= 4; filter++)
            {
                int score = FilterAndScore(row, previous, candidate, filter, 4);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestFilter = filter;
                    candidate.CopyTo(best, 0);
                }
            }

            int destination = y * (stride + 1);
            output[destination] = (byte)bestFilter;
            best.CopyTo(output, destination + 1);
            row.CopyTo(previous, 0);
        }

        return output;
    }

    private static int FilterAndScore(ReadOnlySpan<byte> row, ReadOnlySpan<byte> previous, Span<byte> output, int filter, int bpp)
    {
        int score = 0;
        switch (filter)
        {
            case 0:
                for (int i = 0; i < row.Length; i++)
                {
                    byte value = row[i];
                    output[i] = value;
                    score += value < 128 ? value : 256 - value;
                }

                break;

            case 1:
                for (int i = 0; i < row.Length; i++)
                {
                    int left = i >= bpp ? row[i - bpp] : 0;
                    byte value = (byte)(row[i] - left);
                    output[i] = value;
                    score += value < 128 ? value : 256 - value;
                }

                break;

            case 2:
                for (int i = 0; i < row.Length; i++)
                {
                    byte value = (byte)(row[i] - previous[i]);
                    output[i] = value;
                    score += value < 128 ? value : 256 - value;
                }

                break;

            case 3:
                for (int i = 0; i < row.Length; i++)
                {
                    int left = i >= bpp ? row[i - bpp] : 0;
                    byte value = (byte)(row[i] - ((left + previous[i]) >> 1));
                    output[i] = value;
                    score += value < 128 ? value : 256 - value;
                }

                break;

            case 4:
                for (int i = 0; i < row.Length; i++)
                {
                    int left = i >= bpp ? row[i - bpp] : 0;
                    int up = previous[i];
                    int upLeft = i >= bpp ? previous[i - bpp] : 0;
                    byte value = (byte)(row[i] - Paeth(left, up, upLeft));
                    output[i] = value;
                    score += value < 128 ? value : 256 - value;
                }

                break;
        }

        return score;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
        {
            return a;
        }

        return pb <= pc ? b : c;
    }

    private static void ValidateHeader(int width, int height, int bitDepth, int colorType, int interlace)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ImageFormatException("Invalid PNG dimensions.");
        }

        if (interlace != 0)
        {
            throw new ImageFormatException("Interlaced PNG files are not supported.");
        }

        bool valid = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            2 => bitDepth is 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            4 => bitDepth is 8 or 16,
            6 => bitDepth is 8 or 16,
            _ => false
        };

        if (!valid)
        {
            throw new ImageFormatException($"Unsupported PNG color type and bit depth: {colorType}/{bitDepth}.");
        }
    }

    private static int ChannelsForColorType(int colorType)
    {
        return colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new ImageFormatException($"Unsupported PNG color type: {colorType}.")
        };
    }

    private static byte[] Combine(List<byte[]> chunks)
    {
        int length = chunks.Sum(static chunk => chunk.Length);
        byte[] output = new byte[length];
        int offset = 0;
        foreach (byte[] chunk in chunks)
        {
            chunk.CopyTo(output, offset);
            offset += chunk.Length;
        }

        return output;
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> payload)
    {
        Span<byte> length = stackalloc byte[4];
        WriteInt32BigEndian(length, 0, payload.Length);
        stream.Write(length);

        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(payload);

        uint crc = Crc32.Compute(typeBytes, payload);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static void VerifyCrc(ReadOnlySpan<byte> typeAndPayload, uint expected)
    {
        uint actual = Crc32.Compute(typeAndPayload);
        if (actual != expected)
        {
            throw new ImageFormatException("PNG CRC check failed.");
        }
    }

    private static int ReadInt32BigEndian(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadInt32BigEndian(data[offset..]);
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
    }

    private static void WriteInt32BigEndian(Span<byte> data, int offset, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(data[offset..], value);
    }

    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            uint crc = 0xffffffffu;
            foreach (byte value in data)
            {
                crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
            }

            return crc ^ 0xffffffffu;
        }

        public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
        {
            uint crc = 0xffffffffu;
            foreach (byte value in first)
            {
                crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
            }

            foreach (byte value in second)
            {
                crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
            }

            return crc ^ 0xffffffffu;
        }

        private static uint[] BuildTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
                }

                table[i] = c;
            }

            return table;
        }
    }
}
