using System.Buffers.Binary;
using System.Numerics;

namespace Relatude.DB.FileConversion.DefaultImage.NativeImageLib;

internal sealed class JpegCodec : IImageCodec
{
    private static readonly int[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10,
        17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63
    ];

    private static readonly byte[] LuminanceQuant =
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99
    ];

    private static readonly byte[] ChrominanceQuant =
    [
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99
    ];

    private static readonly byte[] DcLuminanceCounts = [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] DcChrominanceCounts = [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    private static readonly byte[] DcValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    private static readonly byte[] AcLuminanceCounts = [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d];
    private static readonly byte[] AcChrominanceCounts = [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];

    private static readonly byte[] AcLuminanceValues =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12,
        0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08,
        0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16,
        0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
        0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
        0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79,
        0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98,
        0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7,
        0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6,
        0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5,
        0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4,
        0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea,
        0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa
    ];

    private static readonly byte[] AcChrominanceValues =
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21,
        0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91,
        0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0,
        0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34,
        0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38,
        0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
        0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78,
        0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96,
        0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5,
        0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4,
        0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3,
        0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2,
        0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9,
        0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8,
        0xf9, 0xfa
    ];

    private static readonly double[] DctMatrix = BuildDctMatrix();
    private static readonly HuffmanEncoder DcLuminanceEncoder = new(DcLuminanceCounts, DcValues);
    private static readonly HuffmanEncoder AcLuminanceEncoder = new(AcLuminanceCounts, AcLuminanceValues);
    private static readonly HuffmanEncoder DcChrominanceEncoder = new(DcChrominanceCounts, DcValues);
    private static readonly HuffmanEncoder AcChrominanceEncoder = new(AcChrominanceCounts, AcChrominanceValues);

    public ImageFormat Format => ImageFormat.Jpeg;

    public bool CanDecode(ReadOnlySpan<byte> header)
    {
        return header.Length >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff;
    }

    public PureImage Decode(ReadOnlySpan<byte> data)
    {
        byte[] bytes = data.ToArray();
        if (!CanDecode(bytes))
        {
            throw new ImageFormatException("Invalid JPEG signature.");
        }

        JpegDecoder decoder = new(bytes);
        return decoder.Decode();
    }

    public void Encode(PureImage image, Stream stream, ImageSaveOptions options)
    {
        int[] qY = ScaleQuantTable(LuminanceQuant, options.Quality);
        int[] qC = ScaleQuantTable(ChrominanceQuant, options.Quality);

        WriteMarker(stream, 0xd8);
        WriteSegment(stream, 0xe0, [0x4a, 0x46, 0x49, 0x46, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        WriteDqt(stream, 0, qY);
        WriteDqt(stream, 1, qC);
        WriteSof0(stream, image.Width, image.Height);
        WriteDht(stream, 0, 0, DcLuminanceCounts, DcValues);
        WriteDht(stream, 1, 0, AcLuminanceCounts, AcLuminanceValues);
        WriteDht(stream, 0, 1, DcChrominanceCounts, DcValues);
        WriteDht(stream, 1, 1, AcChrominanceCounts, AcChrominanceValues);
        WriteSos(stream);

        JpegBitWriter writer = new(stream);
        int blocksX = (image.Width + 7) / 8;
        int blocksY = (image.Height + 7) / 8;
        int previousY = 0;
        int previousCb = 0;
        int previousCr = 0;
        double[] ySamples = new double[64];
        double[] cbSamples = new double[64];
        double[] crSamples = new double[64];
        double[] workspace = new double[64];
        int[] yBlock = new int[64];
        int[] cbBlock = new int[64];
        int[] crBlock = new int[64];

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                BuildEncodedBlocks(image, bx, by, qY, qC, ySamples, cbSamples, crSamples, workspace, yBlock, cbBlock, crBlock);

                previousY = EncodeBlock(writer, yBlock, previousY, DcLuminanceEncoder, AcLuminanceEncoder);
                previousCb = EncodeBlock(writer, cbBlock, previousCb, DcChrominanceEncoder, AcChrominanceEncoder);
                previousCr = EncodeBlock(writer, crBlock, previousCr, DcChrominanceEncoder, AcChrominanceEncoder);
            }
        }

        writer.Flush();
        WriteMarker(stream, 0xd9);
    }

    private static int EncodeBlock(JpegBitWriter writer, int[] block, int previousDc, HuffmanEncoder dc, HuffmanEncoder ac)
    {
        int diff = block[0] - previousDc;
        int category = Category(diff);
        dc.Write(writer, category);
        if (category > 0)
        {
            writer.WriteBits(MagnitudeBits(diff, category), category);
        }

        int zeroRun = 0;
        for (int i = 1; i < 64; i++)
        {
            int coefficient = block[ZigZag[i]];
            if (coefficient == 0)
            {
                zeroRun++;
                if (zeroRun == 16)
                {
                    ac.Write(writer, 0xf0);
                    zeroRun = 0;
                }

                continue;
            }

            int acCategory = Category(coefficient);
            ac.Write(writer, (zeroRun << 4) | acCategory);
            writer.WriteBits(MagnitudeBits(coefficient, acCategory), acCategory);
            zeroRun = 0;
        }

        if (zeroRun > 0)
        {
            ac.Write(writer, 0);
        }

        return block[0];
    }

    private static void BuildEncodedBlocks(
        PureImage image,
        int blockX,
        int blockY,
        int[] luminanceQuant,
        int[] chrominanceQuant,
        double[] ySamples,
        double[] cbSamples,
        double[] crSamples,
        double[] workspace,
        int[] yOutput,
        int[] cbOutput,
        int[] crOutput)
    {
        ReadOnlySpan<byte> pixels = image.Pixels;

        for (int y = 0; y < 8; y++)
        {
            int sourceY = Math.Min(image.Height - 1, blockY * 8 + y);
            for (int x = 0; x < 8; x++)
            {
                int sourceX = Math.Min(image.Width - 1, blockX * 8 + x);
                int offset = (sourceY * image.Width + sourceX) * 4;
                int a = pixels[offset + 3];
                double r;
                double g;
                double b;
                if (a == 255)
                {
                    r = pixels[offset];
                    g = pixels[offset + 1];
                    b = pixels[offset + 2];
                }
                else
                {
                    int inverseA = 255 - a;
                    r = (pixels[offset] * a + 255 * inverseA + 127) / 255;
                    g = (pixels[offset + 1] * a + 255 * inverseA + 127) / 255;
                    b = (pixels[offset + 2] * a + 255 * inverseA + 127) / 255;
                }

                int sample = y * 8 + x;
                ySamples[sample] = 0.299 * r + 0.587 * g + 0.114 * b - 128;
                cbSamples[sample] = -0.168736 * r - 0.331264 * g + 0.5 * b;
                crSamples[sample] = 0.5 * r - 0.418688 * g - 0.081312 * b;
            }
        }

        ForwardDct(ySamples, workspace, yOutput, luminanceQuant);
        ForwardDct(cbSamples, workspace, cbOutput, chrominanceQuant);
        ForwardDct(crSamples, workspace, crOutput, chrominanceQuant);
    }

    private static void ForwardDct(double[] samples, double[] workspace, int[] output, int[] quant)
    {
        for (int y = 0; y < 8; y++)
        {
            for (int u = 0; u < 8; u++)
            {
                double sum = 0;
                int sampleBase = y * 8;
                int matrixBase = u * 8;
                for (int x = 0; x < 8; x++)
                {
                    sum += samples[sampleBase + x] * DctMatrix[matrixBase + x];
                }

                workspace[y * 8 + u] = sum;
            }
        }

        for (int v = 0; v < 8; v++)
        {
            int matrixBase = v * 8;
            int outputBase = v * 8;
            for (int u = 0; u < 8; u++)
            {
                double sum = 0;
                for (int y = 0; y < 8; y++)
                {
                    sum += workspace[y * 8 + u] * DctMatrix[matrixBase + y];
                }

                int index = outputBase + u;
                output[index] = (int)Math.Round(sum / quant[index]);
            }
        }
    }

    private static int Category(int value)
    {
        int absolute = Math.Abs(value);
        return absolute == 0 ? 0 : BitOperations.Log2((uint)absolute) + 1;
    }

    private static int MagnitudeBits(int value, int category)
    {
        return value >= 0 ? value : value + ((1 << category) - 1);
    }

    private static int ExtendSign(int value, int bits)
    {
        if (bits == 0)
        {
            return 0;
        }

        int vt = 1 << (bits - 1);
        return value < vt ? value + ((-1) << bits) + 1 : value;
    }

    private static int[] ScaleQuantTable(byte[] source, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
        int[] table = new int[64];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = Math.Clamp((source[i] * scale + 50) / 100, 1, 255);
        }

        return table;
    }

    private static void WriteSof0(Stream stream, int width, int height)
    {
        byte[] payload =
        [
            8,
            (byte)(height >> 8), (byte)height,
            (byte)(width >> 8), (byte)width,
            3,
            1, 0x11, 0,
            2, 0x11, 1,
            3, 0x11, 1
        ];
        WriteSegment(stream, 0xc0, payload);
    }

    private static void WriteSos(Stream stream)
    {
        byte[] payload =
        [
            3,
            1, 0x00,
            2, 0x11,
            3, 0x11,
            0, 63, 0
        ];
        WriteSegment(stream, 0xda, payload);
    }

    private static void WriteDqt(Stream stream, int tableId, int[] table)
    {
        byte[] payload = new byte[65];
        payload[0] = (byte)tableId;
        for (int i = 0; i < 64; i++)
        {
            payload[i + 1] = (byte)table[ZigZag[i]];
        }

        WriteSegment(stream, 0xdb, payload);
    }

    private static void WriteDht(Stream stream, int tableClass, int tableId, byte[] counts, byte[] values)
    {
        byte[] payload = new byte[1 + 16 + values.Length];
        payload[0] = (byte)((tableClass << 4) | tableId);
        counts.CopyTo(payload, 1);
        values.CopyTo(payload, 17);
        WriteSegment(stream, 0xc4, payload);
    }

    private static void WriteSegment(Stream stream, int marker, ReadOnlySpan<byte> payload)
    {
        WriteMarker(stream, marker);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(payload.Length + 2));
        stream.Write(length);
        stream.Write(payload);
    }

    private static void WriteMarker(Stream stream, int marker)
    {
        stream.WriteByte(0xff);
        stream.WriteByte((byte)marker);
    }

    private static double[] BuildDctMatrix()
    {
        double[] matrix = new double[64];
        for (int u = 0; u < 8; u++)
        {
            double cu = u == 0 ? 1 / Math.Sqrt(2) : 1;
            for (int x = 0; x < 8; x++)
            {
                matrix[u * 8 + x] = 0.5 * cu * Math.Cos(((2 * x + 1) * u * Math.PI) / 16);
            }
        }

        return matrix;
    }

    private sealed class JpegDecoder
    {
        private readonly byte[] _data;
        private readonly int[][] _quantTables = new int[4][];
        private readonly HuffmanDecoder?[,] _huffmanTables = new HuffmanDecoder[2, 4];
        private readonly List<Component> _components = [];
        private int _offset = 2;
        private int _width;
        private int _height;
        private int _restartInterval;

        public JpegDecoder(byte[] data)
        {
            _data = data;
        }

        public PureImage Decode()
        {
            while (_offset < _data.Length)
            {
                int marker = ReadMarker();
                if (marker == 0xd9)
                {
                    break;
                }

                if (marker is >= 0xd0 and <= 0xd7)
                {
                    continue;
                }

                if (marker == 0xda)
                {
                    ParseScanHeader(ReadSegment());
                    return DecodeScan();
                }

                ReadOnlySpan<byte> segment = ReadSegment();
                switch (marker)
                {
                    case 0xc0:
                        ParseStartOfFrame(segment);
                        break;
                    case 0xc2:
                        throw new ImageFormatException("Progressive JPEG files are not supported.");
                    case 0xdb:
                        ParseQuantizationTables(segment);
                        break;
                    case 0xc4:
                        ParseHuffmanTables(segment);
                        break;
                    case 0xdd:
                        _restartInterval = BinaryPrimitives.ReadUInt16BigEndian(segment);
                        break;
                }
            }

            throw new ImageFormatException("JPEG image is missing a baseline scan.");
        }

        private PureImage DecodeScan()
        {
            if (_width <= 0 || _height <= 0 || _components.Count is not (1 or 3))
            {
                throw new ImageFormatException("Unsupported JPEG frame.");
            }

            int maxH = _components.Max(static c => c.H);
            int maxV = _components.Max(static c => c.V);
            int mcuWidth = maxH * 8;
            int mcuHeight = maxV * 8;
            int mcuCols = (_width + mcuWidth - 1) / mcuWidth;
            int mcuRows = (_height + mcuHeight - 1) / mcuHeight;

            foreach (Component component in _components)
            {
                component.Stride = mcuCols * component.H * 8;
                component.SampleHeight = mcuRows * component.V * 8;
                component.Samples = new byte[component.Stride * component.SampleHeight];
            }

            JpegBitReader reader = new(_data, _offset);
            int mcuSinceRestart = 0;
            int[] coefficients = new int[64];
            byte[] block = new byte[64];
            double[] idctWorkspace = new double[64];

            for (int my = 0; my < mcuRows; my++)
            {
                for (int mx = 0; mx < mcuCols; mx++)
                {
                    if (_restartInterval > 0 && mcuSinceRestart == _restartInterval)
                    {
                        reader.AlignToByte();
                        reader.SkipRestartMarker();
                        foreach (Component component in _components)
                        {
                            component.PreviousDc = 0;
                        }

                        mcuSinceRestart = 0;
                    }

                    foreach (Component component in _components)
                    {
                        for (int vy = 0; vy < component.V; vy++)
                        {
                            for (int hx = 0; hx < component.H; hx++)
                            {
                                DecodeBlock(reader, component, coefficients, block, idctWorkspace);
                                int blockX = mx * component.H + hx;
                                int blockY = my * component.V + vy;
                                CopyBlock(component, block, blockX, blockY);
                            }
                        }
                    }

                    mcuSinceRestart++;
                }
            }

            return ConvertToRgba(maxH, maxV);
        }

        private void DecodeBlock(JpegBitReader reader, Component component, int[] coefficients, byte[] block, double[] idctWorkspace)
        {
            Array.Clear(coefficients);
            HuffmanDecoder dc = RequiredHuffman(0, component.DcTable);
            HuffmanDecoder ac = RequiredHuffman(1, component.AcTable);
            int[] quant = RequiredQuant(component.QuantTable);

            int dcBits = dc.Decode(reader);
            int diff = ExtendSign(reader.ReadBits(dcBits), dcBits);
            component.PreviousDc += diff;
            coefficients[0] = component.PreviousDc * quant[0];

            int index = 1;
            while (index < 64)
            {
                int symbol = ac.Decode(reader);
                if (symbol == 0)
                {
                    break;
                }

                if (symbol == 0xf0)
                {
                    index += 16;
                    continue;
                }

                int run = symbol >> 4;
                int bits = symbol & 0x0f;
                index += run;
                if (index >= 64)
                {
                    throw new ImageFormatException("Invalid JPEG AC coefficient run.");
                }

                coefficients[ZigZag[index]] = ExtendSign(reader.ReadBits(bits), bits) * quant[ZigZag[index]];
                index++;
            }

            InverseDct(coefficients, block, idctWorkspace);
        }

        private static void CopyBlock(Component component, byte[] block, int blockX, int blockY)
        {
            int destinationX = blockX * 8;
            int destinationY = blockY * 8;
            for (int y = 0; y < 8; y++)
            {
                Buffer.BlockCopy(block, y * 8, component.Samples, (destinationY + y) * component.Stride + destinationX, 8);
            }
        }

        private PureImage ConvertToRgba(int maxH, int maxV)
        {
            byte[] rgba = new byte[_width * _height * 4];

            if (_components.Count == 1)
            {
                Component yComponent = _components[0];
                for (int y = 0; y < _height; y++)
                {
                    for (int x = 0; x < _width; x++)
                    {
                        byte gray = yComponent.Samples[y * yComponent.Stride + x];
                        int destination = (y * _width + x) * 4;
                        rgba[destination] = gray;
                        rgba[destination + 1] = gray;
                        rgba[destination + 2] = gray;
                        rgba[destination + 3] = 255;
                    }
                }

                return new PureImage(_width, _height, rgba);
            }

            Component yComp = _components[0];
            Component cbComp = _components[1];
            Component crComp = _components[2];

            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width; x++)
                {
                    int yy = yComp.Samples[(y * yComp.V / maxV) * yComp.Stride + (x * yComp.H / maxH)];
                    int cb = cbComp.Samples[(y * cbComp.V / maxV) * cbComp.Stride + (x * cbComp.H / maxH)] - 128;
                    int cr = crComp.Samples[(y * crComp.V / maxV) * crComp.Stride + (x * crComp.H / maxH)] - 128;

                    int r = PureImage.ClampToByte(yy + ((91881 * cr + 32768) >> 16));
                    int g = PureImage.ClampToByte(yy - ((22554 * cb + 46802 * cr + 32768) >> 16));
                    int b = PureImage.ClampToByte(yy + ((116130 * cb + 32768) >> 16));

                    int destination = (y * _width + x) * 4;
                    rgba[destination] = (byte)r;
                    rgba[destination + 1] = (byte)g;
                    rgba[destination + 2] = (byte)b;
                    rgba[destination + 3] = 255;
                }
            }

            return new PureImage(_width, _height, rgba);
        }

        private void ParseStartOfFrame(ReadOnlySpan<byte> segment)
        {
            if (segment.Length < 8 || segment[0] != 8)
            {
                throw new ImageFormatException("Only 8-bit baseline JPEG files are supported.");
            }

            _height = BinaryPrimitives.ReadUInt16BigEndian(segment[1..]);
            _width = BinaryPrimitives.ReadUInt16BigEndian(segment[3..]);
            int componentCount = segment[5];
            if (componentCount is not (1 or 3) || segment.Length < 6 + componentCount * 3)
            {
                throw new ImageFormatException("Unsupported JPEG component count.");
            }

            _components.Clear();
            for (int i = 0; i < componentCount; i++)
            {
                int offset = 6 + i * 3;
                int sampling = segment[offset + 1];
                _components.Add(new Component
                {
                    Id = segment[offset],
                    H = sampling >> 4,
                    V = sampling & 0x0f,
                    QuantTable = segment[offset + 2]
                });
            }
        }

        private void ParseScanHeader(ReadOnlySpan<byte> segment)
        {
            int count = segment[0];
            if (count != _components.Count || segment.Length < 1 + count * 2 + 3)
            {
                throw new ImageFormatException("Unsupported JPEG scan layout.");
            }

            for (int i = 0; i < count; i++)
            {
                int id = segment[1 + i * 2];
                Component component = _components.FirstOrDefault(c => c.Id == id)
                    ?? throw new ImageFormatException("JPEG scan references an unknown component.");
                int table = segment[2 + i * 2];
                component.DcTable = table >> 4;
                component.AcTable = table & 0x0f;
            }

            int tail = 1 + count * 2;
            if (segment[tail] != 0 || segment[tail + 1] != 63 || segment[tail + 2] != 0)
            {
                throw new ImageFormatException("Only baseline sequential JPEG scans are supported.");
            }
        }

        private void ParseQuantizationTables(ReadOnlySpan<byte> segment)
        {
            int offset = 0;
            while (offset < segment.Length)
            {
                int info = segment[offset++];
                int precision = info >> 4;
                int id = info & 0x0f;
                if (id >= _quantTables.Length)
                {
                    throw new ImageFormatException("JPEG quantization table id is out of range.");
                }

                int entryBytes = precision == 0 ? 1 : 2;
                if (precision > 1 || offset + 64 * entryBytes > segment.Length)
                {
                    throw new ImageFormatException("Invalid JPEG quantization table.");
                }

                int[] table = new int[64];
                for (int i = 0; i < 64; i++)
                {
                    table[ZigZag[i]] = precision == 0
                        ? segment[offset++]
                        : BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(offset, 2));
                    if (precision == 1)
                    {
                        offset += 2;
                    }
                }

                _quantTables[id] = table;
            }
        }

        private void ParseHuffmanTables(ReadOnlySpan<byte> segment)
        {
            int offset = 0;
            while (offset < segment.Length)
            {
                int info = segment[offset++];
                int tableClass = info >> 4;
                int id = info & 0x0f;
                if (tableClass > 1 || id >= 4 || offset + 16 > segment.Length)
                {
                    throw new ImageFormatException($"Invalid JPEG Huffman table header 0x{info:X2} at byte {offset - 1} in a {segment.Length}-byte segment.");
                }

                byte[] counts = segment.Slice(offset, 16).ToArray();
                offset += 16;
                int valueCount = counts.Sum(static value => value);
                if (offset + valueCount > segment.Length)
                {
                    throw new ImageFormatException("JPEG Huffman table is truncated.");
                }

                byte[] values = segment.Slice(offset, valueCount).ToArray();
                offset += valueCount;
                _huffmanTables[tableClass, id] = new HuffmanDecoder(counts, values);
            }
        }

        private int[] RequiredQuant(int id)
        {
            return id >= 0 && id < _quantTables.Length && _quantTables[id] is { } table
                ? table
                : throw new ImageFormatException($"JPEG quantization table {id} is missing.");
        }

        private HuffmanDecoder RequiredHuffman(int tableClass, int id)
        {
            return id >= 0 && id < 4 && _huffmanTables[tableClass, id] is { } table
                ? table
                : throw new ImageFormatException($"JPEG Huffman table {tableClass}:{id} is missing.");
        }

        private ReadOnlySpan<byte> ReadSegment()
        {
            if (_offset + 2 > _data.Length)
            {
                throw new ImageFormatException("JPEG segment is truncated.");
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_offset, 2));
            if (length < 2 || _offset + length > _data.Length)
            {
                throw new ImageFormatException("JPEG segment length is invalid.");
            }

            ReadOnlySpan<byte> segment = _data.AsSpan(_offset + 2, length - 2);
            _offset += length;
            return segment;
        }

        private int ReadMarker()
        {
            while (_offset < _data.Length && _data[_offset] != 0xff)
            {
                _offset++;
            }

            while (_offset < _data.Length && _data[_offset] == 0xff)
            {
                _offset++;
            }

            if (_offset >= _data.Length)
            {
                throw new ImageFormatException("JPEG marker is missing.");
            }

            return _data[_offset++];
        }
    }

    private sealed class Component
    {
        public int Id { get; init; }
        public int H { get; init; }
        public int V { get; init; }
        public int QuantTable { get; init; }
        public int DcTable { get; set; }
        public int AcTable { get; set; }
        public int PreviousDc { get; set; }
        public int Stride { get; set; }
        public int SampleHeight { get; set; }
        public byte[] Samples { get; set; } = [];
    }

    private sealed class HuffmanDecoder
    {
        private readonly Dictionary<int, int> _symbols = [];

        public HuffmanDecoder(byte[] counts, byte[] values)
        {
            int code = 0;
            int valueIndex = 0;
            for (int length = 1; length <= 16; length++)
            {
                int count = counts[length - 1];
                for (int i = 0; i < count; i++)
                {
                    _symbols[(length << 16) | code] = values[valueIndex++];
                    code++;
                }

                code <<= 1;
            }
        }

        public int Decode(JpegBitReader reader)
        {
            int code = 0;
            for (int length = 1; length <= 16; length++)
            {
                code = (code << 1) | reader.ReadBit();
                if (_symbols.TryGetValue((length << 16) | code, out int symbol))
                {
                    return symbol;
                }
            }

            throw new ImageFormatException("Invalid JPEG Huffman code.");
        }
    }

    private sealed class HuffmanEncoder
    {
        private readonly Dictionary<int, (int Code, int Length)> _codes = [];

        public HuffmanEncoder(byte[] counts, byte[] values)
        {
            int code = 0;
            int valueIndex = 0;
            for (int length = 1; length <= 16; length++)
            {
                int count = counts[length - 1];
                for (int i = 0; i < count; i++)
                {
                    _codes[values[valueIndex++]] = (code, length);
                    code++;
                }

                code <<= 1;
            }
        }

        public void Write(JpegBitWriter writer, int symbol)
        {
            if (!_codes.TryGetValue(symbol, out (int Code, int Length) code))
            {
                throw new ImageFormatException("JPEG encoder is missing a Huffman code.");
            }

            writer.WriteBits(code.Code, code.Length);
        }
    }

    private sealed class JpegBitReader
    {
        private readonly byte[] _data;
        private int _position;
        private int _bitBuffer;
        private int _bitsAvailable;

        public JpegBitReader(byte[] data, int position)
        {
            _data = data;
            _position = position;
        }

        public int ReadBit()
        {
            if (_bitsAvailable == 0)
            {
                FillByte();
            }

            _bitsAvailable--;
            return (_bitBuffer >> _bitsAvailable) & 1;
        }

        public int ReadBits(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                value = (value << 1) | ReadBit();
            }

            return value;
        }

        public void AlignToByte()
        {
            _bitsAvailable = 0;
        }

        public void SkipRestartMarker()
        {
            while (_position < _data.Length && _data[_position] == 0xff)
            {
                _position++;
            }

            if (_position >= _data.Length || _data[_position] is < 0xd0 or > 0xd7)
            {
                throw new ImageFormatException("JPEG restart marker is missing.");
            }

            _position++;
            _bitBuffer = 0;
            _bitsAvailable = 0;
        }

        private void FillByte()
        {
            if (_position >= _data.Length)
            {
                throw new ImageFormatException("JPEG entropy data is truncated.");
            }

            int value = _data[_position++];
            if (value == 0xff)
            {
                if (_position >= _data.Length)
                {
                    throw new ImageFormatException("JPEG entropy marker is truncated.");
                }

                int marker = _data[_position++];
                if (marker != 0x00)
                {
                    throw new ImageFormatException($"Unexpected JPEG marker 0xFF{marker:X2} inside entropy data.");
                }
            }

            _bitBuffer = value;
            _bitsAvailable = 8;
        }
    }

    private sealed class JpegBitWriter
    {
        private readonly Stream _stream;
        private int _bitBuffer;
        private int _bitsInBuffer;

        public JpegBitWriter(Stream stream)
        {
            _stream = stream;
        }

        public void WriteBits(int bits, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                _bitBuffer = (_bitBuffer << 1) | ((bits >> i) & 1);
                _bitsInBuffer++;
                if (_bitsInBuffer == 8)
                {
                    WriteByte(_bitBuffer);
                    _bitBuffer = 0;
                    _bitsInBuffer = 0;
                }
            }
        }

        public void Flush()
        {
            if (_bitsInBuffer > 0)
            {
                int value = (_bitBuffer << (8 - _bitsInBuffer)) | ((1 << (8 - _bitsInBuffer)) - 1);
                WriteByte(value);
                _bitBuffer = 0;
                _bitsInBuffer = 0;
            }
        }

        private void WriteByte(int value)
        {
            _stream.WriteByte((byte)value);
            if (value == 0xff)
            {
                _stream.WriteByte(0);
            }
        }
    }

    private static void InverseDct(int[] coefficients, byte[] output, double[] workspace)
    {
        for (int v = 0; v < 8; v++)
        {
            for (int x = 0; x < 8; x++)
            {
                double sum = 0;
                int coefficientBase = v * 8;
                for (int u = 0; u < 8; u++)
                {
                    sum += coefficients[coefficientBase + u] * DctMatrix[u * 8 + x];
                }

                workspace[v * 8 + x] = sum;
            }
        }

        for (int y = 0; y < 8; y++)
        {
            int outputBase = y * 8;
            for (int x = 0; x < 8; x++)
            {
                double sum = 0;
                for (int v = 0; v < 8; v++)
                {
                    sum += workspace[v * 8 + x] * DctMatrix[v * 8 + y];
                }

                output[outputBase + x] = PureImage.ClampToByte(sum + 128);
            }
        }
    }
}
