using System.Buffers.Binary;
using System.Text;

namespace Relatude.DB.FileConversion.NativeImageEncoder;

internal sealed class WebpCodec : IImageCodec
{
    private const uint Riff = 0x46464952;
    private const uint Webp = 0x50424557;
    private const uint Vp8l = 0x4c385056;

    private static readonly (int X, int Y)[] DistanceMap =
    [
        (0, 1), (1, 0), (1, 1), (-1, 1), (0, 2), (2, 0), (1, 2), (-1, 2),
        (2, 1), (-2, 1), (2, 2), (-2, 2), (0, 3), (3, 0), (1, 3), (-1, 3),
        (3, 1), (-3, 1), (2, 3), (-2, 3), (3, 2), (-3, 2), (0, 4), (4, 0),
        (1, 4), (-1, 4), (4, 1), (-4, 1), (3, 3), (-3, 3), (2, 4), (-2, 4),
        (4, 2), (-4, 2), (0, 5), (3, 4), (-3, 4), (4, 3), (-4, 3), (5, 0),
        (1, 5), (-1, 5), (5, 1), (-5, 1), (2, 5), (-2, 5), (5, 2), (-5, 2),
        (4, 4), (-4, 4), (3, 5), (-3, 5), (5, 3), (-5, 3), (0, 6), (6, 0),
        (1, 6), (-1, 6), (6, 1), (-6, 1), (2, 6), (-2, 6), (6, 2), (-6, 2),
        (4, 5), (-4, 5), (5, 4), (-5, 4), (3, 6), (-3, 6), (6, 3), (-6, 3),
        (0, 7), (7, 0), (1, 7), (-1, 7), (5, 5), (-5, 5), (7, 1), (-7, 1),
        (4, 6), (-4, 6), (6, 4), (-6, 4), (2, 7), (-2, 7), (7, 2), (-7, 2),
        (3, 7), (-3, 7), (7, 3), (-7, 3), (5, 6), (-5, 6), (6, 5), (-6, 5),
        (8, 0), (4, 7), (-4, 7), (7, 4), (-7, 4), (8, 1), (8, 2), (6, 6),
        (-6, 6), (8, 3), (5, 7), (-5, 7), (7, 5), (-7, 5), (8, 4), (6, 7),
        (-6, 7), (7, 6), (-7, 6), (8, 5), (7, 7), (-7, 7), (8, 6), (8, 7)
    ];

    public ImageFormat Format => ImageFormat.Webp;

    public bool CanDecode(ReadOnlySpan<byte> header)
    {
        return header.Length >= 12
            && BinaryPrimitives.ReadUInt32LittleEndian(header) == Riff
            && BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) == Webp;
    }

    public InternalImage Decode(ReadOnlySpan<byte> data)
    {
        if (!CanDecode(data))
        {
            throw new ImageFormatException("Invalid WEBP RIFF header.");
        }

        int offset = 12;
        while (offset + 8 <= data.Length)
        {
            uint fourCc = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            int length = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 4)..]);
            if (length < 0 || offset + 8 + length > data.Length)
            {
                throw new ImageFormatException("WEBP chunk is truncated.");
            }

            ReadOnlySpan<byte> payload = data.Slice(offset + 8, length);
            if (fourCc == Vp8l)
            {
                return DecodeLossless(payload);
            }

            offset += 8 + length + (length & 1);
        }

        throw new ImageFormatException("Only lossless WEBP VP8L chunks are supported.");
    }

    public void Encode(InternalImage image, Stream stream, ImageSaveOptions options)
    {
        if (image.Width > 16384 || image.Height > 16384)
        {
            throw new ArgumentOutOfRangeException(nameof(image), "WEBP lossless images are limited to 16384 x 16384 pixels.");
        }

        byte[] payload = EncodeLosslessPayload(image);
        int paddedPayloadLength = payload.Length + (payload.Length & 1);
        int riffSize = 4 + 8 + paddedPayloadLength;

        WriteFourCc(stream, "RIFF");
        WriteUInt32(stream, (uint)riffSize);
        WriteFourCc(stream, "WEBP");
        WriteFourCc(stream, "VP8L");
        WriteUInt32(stream, (uint)payload.Length);
        stream.Write(payload);
        if ((payload.Length & 1) != 0)
        {
            stream.WriteByte(0);
        }
    }

    private static InternalImage DecodeLossless(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 5 || payload[0] != 0x2f)
        {
            throw new ImageFormatException("Invalid WEBP lossless payload.");
        }

        Vp8LBitReader reader = new(payload[1..].ToArray());
        int width = reader.ReadBits(14) + 1;
        int height = reader.ReadBits(14) + 1;
        _ = reader.ReadBit();
        int version = reader.ReadBits(3);
        if (version != 0)
        {
            throw new ImageFormatException("Unsupported WEBP lossless version.");
        }

        if (reader.ReadBit() != 0)
        {
            throw new ImageFormatException("WEBP lossless transforms are not supported by this decoder.");
        }

        int colorCacheBits = 0;
        int colorCacheSize = 0;
        if (reader.ReadBit() != 0)
        {
            colorCacheBits = reader.ReadBits(4);
            if (colorCacheBits is < 1 or > 11)
            {
                throw new ImageFormatException("Invalid WEBP color cache size.");
            }

            colorCacheSize = 1 << colorCacheBits;
        }

        if (reader.ReadBit() != 0)
        {
            throw new ImageFormatException("WEBP meta-prefix images are not supported by this decoder.");
        }

        Vp8LHuffman green = ReadPrefixCode(reader, 256 + 24 + colorCacheSize);
        Vp8LHuffman red = ReadPrefixCode(reader, 256);
        Vp8LHuffman blue = ReadPrefixCode(reader, 256);
        Vp8LHuffman alpha = ReadPrefixCode(reader, 256);
        Vp8LHuffman distance = ReadPrefixCode(reader, 40);

        byte[] rgba = new byte[checked(width * height * 4)];
        uint[] colorCache = colorCacheSize == 0 ? [] : new uint[colorCacheSize];
        int pixel = 0;
        while (pixel < width * height)
        {
            int symbol = green.Decode(reader);
            if (symbol < 256)
            {
                byte g = (byte)symbol;
                byte r = (byte)red.Decode(reader);
                byte b = (byte)blue.Decode(reader);
                byte a = (byte)alpha.Decode(reader);
                uint argb = PackArgb(a, r, g, b);
                WritePixel(rgba, pixel++, argb);
                InsertColorCache(colorCache, colorCacheBits, argb);
            }
            else if (symbol < 280)
            {
                int length = ReadPrefixValue(reader, symbol - 256);
                int distanceCode = ReadPrefixValue(reader, distance.Decode(reader));
                int copyDistance = DistanceCodeToPixelDistance(distanceCode, width);
                if (copyDistance <= 0 || copyDistance > pixel)
                {
                    throw new ImageFormatException("Invalid WEBP backward reference.");
                }

                for (int i = 0; i < length && pixel < width * height; i++)
                {
                    uint argb = ReadPixelAsArgb(rgba, pixel - copyDistance);
                    WritePixel(rgba, pixel++, argb);
                    InsertColorCache(colorCache, colorCacheBits, argb);
                }
            }
            else
            {
                int index = symbol - 280;
                if (index >= colorCache.Length)
                {
                    throw new ImageFormatException("Invalid WEBP color cache index.");
                }

                uint argb = colorCache[index];
                WritePixel(rgba, pixel++, argb);
                InsertColorCache(colorCache, colorCacheBits, argb);
            }
        }

        return new InternalImage(width, height, rgba);
    }

    private static byte[] EncodeLosslessPayload(InternalImage image)
    {
        using MemoryStream memory = new();
        memory.WriteByte(0x2f);
        Vp8LBitWriter writer = new(memory);
        writer.WriteBits(image.Width - 1, 14);
        writer.WriteBits(image.Height - 1, 14);
        writer.WriteBit(HasAlpha(image) ? 1 : 0);
        writer.WriteBits(0, 3);

        writer.WriteBit(0);
        writer.WriteBit(0);
        writer.WriteBit(0);

        WriteLiteralPrefixCode(writer, 280);
        WriteLiteralPrefixCode(writer, 256);
        WriteLiteralPrefixCode(writer, 256);
        WriteLiteralPrefixCode(writer, 256);
        WriteSingleSymbolPrefixCode(writer, 0);

        Vp8LHuffmanEncoder literal = Vp8LHuffmanEncoder.EightBitLiterals();
        ReadOnlySpan<byte> pixels = image.Pixels;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            literal.Write(writer, pixels[i + 1]);
            literal.Write(writer, pixels[i]);
            literal.Write(writer, pixels[i + 2]);
            literal.Write(writer, pixels[i + 3]);
        }

        writer.Flush();
        return memory.ToArray();
    }

    private static void WriteLiteralPrefixCode(Vp8LBitWriter writer, int alphabetSize)
    {
        writer.WriteBit(0);
        writer.WriteBits(8, 4);

        Span<int> codeLengthCodeLengths = stackalloc int[19];
        codeLengthCodeLengths[0] = 1;
        codeLengthCodeLengths[8] = 1;
        int[] order = [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8];
        foreach (int symbol in order)
        {
            writer.WriteBits(codeLengthCodeLengths[symbol], 3);
        }

        writer.WriteBit(0);
        for (int i = 0; i < alphabetSize; i++)
        {
            writer.WriteBit(i < 256 ? 1 : 0);
        }
    }

    private static void WriteSingleSymbolPrefixCode(Vp8LBitWriter writer, int symbol)
    {
        writer.WriteBit(1);
        writer.WriteBit(0);
        writer.WriteBit(symbol > 1 ? 1 : 0);
        writer.WriteBits(symbol, symbol > 1 ? 8 : 1);
    }

    private static Vp8LHuffman ReadPrefixCode(Vp8LBitReader reader, int alphabetSize)
    {
        int[] codeLengths = new int[alphabetSize];
        if (reader.ReadBit() != 0)
        {
            int symbols = reader.ReadBit() + 1;
            int isFirstEightBits = reader.ReadBit();
            int symbol0 = reader.ReadBits(1 + 7 * isFirstEightBits);
            if (symbol0 >= alphabetSize)
            {
                throw new ImageFormatException("WEBP simple prefix code is out of range.");
            }

            codeLengths[symbol0] = 1;
            if (symbols == 2)
            {
                int symbol1 = reader.ReadBits(8);
                if (symbol1 >= alphabetSize)
                {
                    throw new ImageFormatException("WEBP simple prefix code is out of range.");
                }

                codeLengths[symbol1] = 1;
            }

            return new Vp8LHuffman(codeLengths);
        }

        int[] order = [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];
        int numCodeLengths = 4 + reader.ReadBits(4);
        int[] codeLengthCodeLengths = new int[19];
        for (int i = 0; i < numCodeLengths; i++)
        {
            codeLengthCodeLengths[order[i]] = reader.ReadBits(3);
        }

        Vp8LHuffman codeLengthCode = new(codeLengthCodeLengths);
        int maxSymbol = alphabetSize;
        if (reader.ReadBit() != 0)
        {
            int lengthBits = 2 + 2 * reader.ReadBits(3);
            maxSymbol = 2 + reader.ReadBits(lengthBits);
            if (maxSymbol > alphabetSize)
            {
                throw new ImageFormatException("WEBP prefix code exceeds alphabet size.");
            }
        }

        int index = 0;
        int previous = 8;
        while (index < maxSymbol)
        {
            int symbol = codeLengthCode.Decode(reader);
            if (symbol <= 15)
            {
                codeLengths[index++] = symbol;
                if (symbol != 0)
                {
                    previous = symbol;
                }
            }
            else if (symbol == 16)
            {
                int repeat = 3 + reader.ReadBits(2);
                for (int i = 0; i < repeat && index < maxSymbol; i++)
                {
                    codeLengths[index++] = previous;
                }
            }
            else if (symbol == 17)
            {
                int repeat = 3 + reader.ReadBits(3);
                index += Math.Min(repeat, maxSymbol - index);
            }
            else if (symbol == 18)
            {
                int repeat = 11 + reader.ReadBits(7);
                index += Math.Min(repeat, maxSymbol - index);
            }
            else
            {
                throw new ImageFormatException("Invalid WEBP code length symbol.");
            }
        }

        return new Vp8LHuffman(codeLengths);
    }

    private static int ReadPrefixValue(Vp8LBitReader reader, int prefixCode)
    {
        if (prefixCode < 4)
        {
            return prefixCode + 1;
        }

        int extraBits = (prefixCode - 2) >> 1;
        int offset = (2 + (prefixCode & 1)) << extraBits;
        return offset + reader.ReadBits(extraBits) + 1;
    }

    private static int DistanceCodeToPixelDistance(int distanceCode, int width)
    {
        if (distanceCode <= 120)
        {
            (int x, int y) = DistanceMap[distanceCode - 1];
            int distance = x + y * width;
            return distance < 1 ? 1 : distance;
        }

        return distanceCode - 120;
    }

    private static bool HasAlpha(InternalImage image)
    {
        ReadOnlySpan<byte> pixels = image.Pixels;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 255)
            {
                return true;
            }
        }

        return false;
    }

    private static uint PackArgb(byte a, byte r, byte g, byte b)
    {
        return (uint)(a << 24 | r << 16 | g << 8 | b);
    }

    private static void WritePixel(byte[] rgba, int pixel, uint argb)
    {
        int offset = pixel * 4;
        rgba[offset] = (byte)(argb >> 16);
        rgba[offset + 1] = (byte)(argb >> 8);
        rgba[offset + 2] = (byte)argb;
        rgba[offset + 3] = (byte)(argb >> 24);
    }

    private static uint ReadPixelAsArgb(byte[] rgba, int pixel)
    {
        int offset = pixel * 4;
        return PackArgb(rgba[offset + 3], rgba[offset], rgba[offset + 1], rgba[offset + 2]);
    }

    private static void InsertColorCache(uint[] cache, int bits, uint argb)
    {
        if (cache.Length == 0)
        {
            return;
        }

        uint index = (0x1e35a7bdu * argb) >> (32 - bits);
        cache[index] = argb;
    }

    private static void WriteFourCc(Stream stream, string value)
    {
        stream.Write(Encoding.ASCII.GetBytes(value));
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private sealed class Vp8LBitReader
    {
        private readonly byte[] _data;
        private int _position;
        private int _bitOffset;

        public Vp8LBitReader(byte[] data)
        {
            _data = data;
        }

        public int ReadBit()
        {
            if (_position >= _data.Length)
            {
                throw new ImageFormatException("WEBP bitstream is truncated.");
            }

            int bit = (_data[_position] >> _bitOffset) & 1;
            _bitOffset++;
            if (_bitOffset == 8)
            {
                _bitOffset = 0;
                _position++;
            }

            return bit;
        }

        public int ReadBits(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                value |= ReadBit() << i;
            }

            return value;
        }
    }

    private sealed class Vp8LBitWriter
    {
        private readonly Stream _stream;
        private int _currentByte;
        private int _bitOffset;

        public Vp8LBitWriter(Stream stream)
        {
            _stream = stream;
        }

        public void WriteBit(int bit)
        {
            _currentByte |= (bit & 1) << _bitOffset;
            _bitOffset++;
            if (_bitOffset == 8)
            {
                FlushByte();
            }
        }

        public void WriteBits(int value, int count)
        {
            if (count == 8)
            {
                WriteByteBits(value);
                return;
            }

            for (int i = 0; i < count; i++)
            {
                WriteBit((value >> i) & 1);
            }
        }

        public void Flush()
        {
            if (_bitOffset > 0)
            {
                FlushByte();
            }
        }

        private void FlushByte()
        {
            _stream.WriteByte((byte)_currentByte);
            _currentByte = 0;
            _bitOffset = 0;
        }

        private void WriteByteBits(int value)
        {
            value &= 0xff;
            if (_bitOffset == 0)
            {
                _stream.WriteByte((byte)value);
                return;
            }

            _currentByte |= (value << _bitOffset) & 0xff;
            _stream.WriteByte((byte)_currentByte);
            _currentByte = value >> (8 - _bitOffset);
        }
    }

    private sealed class Vp8LHuffman
    {
        private readonly Dictionary<int, int> _symbols = [];
        private readonly int _singleSymbol;

        public Vp8LHuffman(int[] codeLengths)
        {
            int nonZero = 0;
            int singleSymbol = -1;
            for (int i = 0; i < codeLengths.Length; i++)
            {
                if (codeLengths[i] != 0)
                {
                    nonZero++;
                    singleSymbol = i;
                }
            }

            if (nonZero == 0)
            {
                _singleSymbol = 0;
                return;
            }

            if (nonZero == 1)
            {
                _singleSymbol = singleSymbol;
                return;
            }

            _singleSymbol = -1;
            int code = 0;
            for (int length = 1; length <= 15; length++)
            {
                for (int symbol = 0; symbol < codeLengths.Length; symbol++)
                {
                    if (codeLengths[symbol] == length)
                    {
                        _symbols[(length << 16) | code] = symbol;
                        code++;
                    }
                }

                code <<= 1;
            }
        }

        public int Decode(Vp8LBitReader reader)
        {
            if (_singleSymbol >= 0)
            {
                return _singleSymbol;
            }

            int code = 0;
            for (int length = 1; length <= 15; length++)
            {
                code = (code << 1) | reader.ReadBit();
                if (_symbols.TryGetValue((length << 16) | code, out int symbol))
                {
                    return symbol;
                }
            }

            throw new ImageFormatException("Invalid WEBP Huffman code.");
        }
    }

    private sealed class Vp8LHuffmanEncoder
    {
        private readonly (int Code, int Length)[] _codes;

        private Vp8LHuffmanEncoder((int Code, int Length)[] codes)
        {
            _codes = codes;
        }

        public static Vp8LHuffmanEncoder EightBitLiterals()
        {
            (int Code, int Length)[] codes = new (int Code, int Length)[256];
            for (int i = 0; i < codes.Length; i++)
            {
                codes[i] = (ReverseBits(i, 8), 8);
            }

            return new Vp8LHuffmanEncoder(codes);
        }

        public void Write(Vp8LBitWriter writer, int symbol)
        {
            (int code, int length) = _codes[symbol];
            writer.WriteBits(code, length);
        }

        private static int ReverseBits(int value, int count)
        {
            int result = 0;
            for (int i = 0; i < count; i++)
            {
                result = (result << 1) | ((value >> i) & 1);
            }

            return result;
        }
    }
}
