using Relatude.DB.Common;
using Relatude.DB.FileConversion;
using Relatude.DB.FileConversion.ImageEncoders;

namespace Relatude.FileConversion;

[TestClass]
public class NativeImageCodecTests {

    // smooth colour with some detail, so it compresses roughly like a photo
    static InternalImage Picture(int w, int h, bool alpha = false) => InternalImage.Create(w, h, (x, y) => new ColorRgba(
        (byte)(128 + 100 * Math.Sin(x * 0.05)), (byte)(128 + 100 * Math.Cos(y * 0.07)), (byte)(128 + 80 * Math.Sin((x + y) * 0.03)), alpha ? (byte)(x * 255 / w) : (byte)255));

    static byte[] Save(InternalImage image, ImageFormat format, int quality = 90) {
        var ms = new MemoryStream();
        image.Save(ms, format, new ImageSaveOptions { Quality = quality });
        return ms.ToArray();
    }

    static double Psnr(InternalImage a, InternalImage b) {
        ReadOnlySpan<byte> pa = a.Pixels, pb = b.Pixels;
        double sum = 0;
        for (int i = 0; i < pa.Length; i++) if ((i & 3) != 3) sum += (pa[i] - pb[i]) * (pa[i] - pb[i]);
        double mse = sum / (pa.Length / 4 * 3);
        return mse == 0 ? 99 : 10 * Math.Log10(255 * 255 / mse);
    }

    [TestMethod]
    public void Jpeg_RoundTripKeepsThePicture() {
        var source = Picture(333, 251);
        foreach (var quality in new[] { 50, 85, 95 }) {
            var back = InternalImage.Load(Save(source, ImageFormat.Jpeg, quality));
            Assert.AreEqual(333, back.Width);
            Assert.AreEqual(251, back.Height);
            Assert.IsTrue(Psnr(source, back) > 35, $"q{quality}: {Psnr(source, back):F1} dB");
        }
    }

    [TestMethod]
    public void Jpeg_SubsamplesChromaBelowQuality90() {
        static int lumaSampling(byte[] jpeg) {
            for (int i = 2; i < jpeg.Length - 1; i++) if (jpeg[i] == 0xFF && jpeg[i + 1] == 0xC0) return jpeg[i + 11];
            return -1;
        }
        var source = Picture(64, 64);
        Assert.AreEqual(0x22, lumaSampling(Save(source, ImageFormat.Jpeg, 85)));
        Assert.AreEqual(0x11, lumaSampling(Save(source, ImageFormat.Jpeg, 90)));
    }

    [TestMethod]
    public void Jpeg_DecodesAtAFractionOfItsSize() {
        var data = Save(Picture(400, 300), ImageFormat.Jpeg);
        var full = InternalImage.Load(data);
        foreach (var scale in new[] { 2, 4, 8 }) {
            var small = InternalImage.Load(data, scale);
            Assert.AreEqual((400 + scale - 1) / scale, small.Width);
            Assert.AreEqual((300 + scale - 1) / scale, small.Height);
            Assert.IsTrue(Psnr(full.Resize(small.Width, small.Height), small) > 28, $"1/{scale}");
        }
    }

    [TestMethod]
    public void Jpeg_ExifOrientationIsApplied() {
        var source = Picture(64, 32);
        var jpeg = Save(source, ImageFormat.Jpeg, 95);
        // APP1 Exif, little endian, one IFD entry: orientation (0x0112) = 6, turn 90 degrees clockwise
        byte[] app1 = [0xFF, 0xE1, 0, 34, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0, (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0,
            1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0, 0, 0, 0, 0];
        var turnedJpeg = jpeg[..2].Concat(app1).Concat(jpeg[2..]).ToArray();
        Assert.IsTrue(InternalImage.TryReadSize(turnedJpeg, out var width, out var height));
        Assert.AreEqual((32, 64), (width, height), "the header reports the size as shown");
        var turned = InternalImage.Load(turnedJpeg);
        Assert.AreEqual((32, 64), (turned.Width, turned.Height));
        Assert.IsTrue(Psnr(source.Rotate90Clockwise(), turned) > 30);
    }

    [TestMethod]
    public void Png_RoundTripIsExact_InTheSmallestColourType() {
        ColorRgba[] three = [new(255, 0, 0), new(0, 0, 255, 128), new(0, 0, 0, 0)];
        var cases = new (string Name, InternalImage Image, int Type, int Depth)[] {
            ("rgba", Picture(50, 40, alpha: true), 6, 8),
            ("rgb", Picture(50, 40), 2, 8),
            ("grey", InternalImage.Create(50, 40, (x, y) => new ColorRgba((byte)(x * 5), (byte)(x * 5), (byte)(x * 5))), 0, 8),
            ("grey with alpha", InternalImage.Create(50, 40, (x, y) => new ColorRgba((byte)y, (byte)y, (byte)y, (byte)(x * 5))), 4, 8),
            ("three colours", InternalImage.Create(50, 40, (x, y) => three[(x / 5 + y / 5) % 3]), 3, 2),
        };
        foreach (var (name, image, type, depth) in cases) {
            var png = Save(image, ImageFormat.Png);
            Assert.AreEqual(depth, png[24], name);
            Assert.AreEqual(type, png[25], name);
            CollectionAssert.AreEqual(image.Pixels.ToArray(), InternalImage.Load(png).Pixels.ToArray(), name);
        }
    }

    [TestMethod]
    public void Resize_TransparentEdgesKeepTheirColour() {
        var disc = InternalImage.Create(400, 400, (x, y) => (x - 200) * (x - 200) + (y - 200) * (y - 200) < 150 * 150 ? new ColorRgba(255, 0, 0) : new ColorRgba(0, 0, 0, 0));
        var small = disc.Resize(40, 40);
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 40; x++) {
                var c = small[x, y];
                if (c.A > 20) Assert.IsTrue(c.R > 250 && c.G < 5 && c.B < 5, $"({x},{y}) is {c}");
            }
    }

    [TestMethod]
    public void LoadFor_DecodesNoLargerThanTheResultNeeds() {
        var jpeg = Save(Picture(2000, 1000), ImageFormat.Jpeg, 85);
        var adj = new FileAdjustmentImage { Width = 200, CropMode = ImageCropMode.Fill, FocusX = 1000 };
        var asked = adj;
        using var image = NativeImage.LoadFor(new MemoryStream(jpeg), ref adj, out var width, out var height);
        Assert.AreEqual((2000, 1000), (width, height), "the original's size");
        Assert.AreEqual(250, image.Width, "decoded at 1/8, which still covers the 200 asked for");
        Assert.AreEqual(125, adj.FocusX, "the focus moves with the decoded size");
        Assert.AreEqual(1000, asked.FocusX, "the caller's adjustment is left alone");
        using var result = image.Adjust(adj);
        Assert.AreEqual((200, 100), (result.Width, result.Height));
    }

    // written by libwebp (through Pillow): the hash is of the pixels libwebp decodes them to, which ours must equal
    static readonly (string Name, string Base64, string Sha256)[] LibwebpFiles = [
        ("lossy with alpha", "UklGRs4AAABXRUJQVlA4WAoAAAAQAAAAFwAADwAAQUxQSCMAAAABFyAQSNKefo2ICAcFbSQpZ5L94evkVxDR/wmAizj3Meyv6wBWUDgghAAAALAEAJ0BKhgAEAA+bTCTRiQjIaEwCACADYlsAJ0yhHA3kD4AAUBwEE5JZUaTVgAA/v5yeX2t3X7w4nzT9SGXfjNfz2aaquRtomNFn0Niwrb+cYTU1QwzIff/zi/B7zGTzikyXvMTAGKA2lLov8n6/YwDt2ug/SP8YFv+DGiguOVK6vtsAA==", "7bb915e03f6c9156d1370953a324e7019048dbbe330e1a4af86d5cc563407053"),
        ("lossless", "UklGRnQAAABXRUJQVlA4TGgAAAAvF8ADEA2ISRP22z+xISL6PwE2VF0YKEjbgKl/27sSBmLA+z8BCMoHKIQkyWmDJ3l/wAsfAdS2bcNiTikSCyb78rcMg0AQIrMtMNsEIPDL6Su7BwBAUVQbABjEHfkb8UOGHgYx1Fn+Fg==", "accfb8ef4bd645b1853156cca3102a8335b8bad73f9271298492576d09a57f13"),
        ("animated, first frame", "UklGRuIBAABXRUJQVlA4WAoAAAASAAAAFwAADwAAQU5JTQYAAAAAAAAAAABBTk1G1gAAAAAAAAEAABcAAA0AAGQAAAJBTFBIKAAAAAEXIBZM8bAzKCJCg1EbSY6a5EZ+0TjvdQgi+j8OSZIAZinrXwAA3z5WUDggjgAAADAEAJ0BKhgADgA+bTSTRqQjIaEwCACADYlsAJ0yhHA3oAE92NUxBALGAAD+/lpWrT2evDBj6BbilreEJPwjlRcc01UF92xzotB0MZ/4xbfpk/Gj+PHRln9O7a4/aNt5L7WKxXfZ9EK38hFlmrnmEQ08EbxD0YL+f+fv0+gGY62P+6aZ/ZfEkXHKleXgAABBTk1G2AAAAAAAAAEAABcAAA0AAGQAAAJBTFBIKAAAAAEXIBZM8bAzKCJCg1EbSY6a5EZ+0TjvdQgi+j8OSZIAZinrXwAA3z5WUDggkAAAADAEAJ0BKhgADgA+bSyTRaQioZgEAEAGxLYdwvBGYwQ5Vh34cQAL7kSi7gD+493+ff/xyAX9H3P/82N9pJspKkyeMXlxjePIOcFXCcTC2SY1E7jbKn2hLbl/axv6f8//Z+Cio4f/y3zxUtz819rZ5kFTvSGyw7l1cvv1oXdVp+RnnbzHHP+pQa3Hxz6FuQAAAA==", "61c7957e89131aaf8937026c0201b66eaabe1da64a599819a3a7d9d7da3e792f"),
    ];

    [TestMethod]
    public void Webp_ReadsWhatLibwebpWrites() {
        foreach (var (name, base64, sha256) in LibwebpFiles) {
            var data = Convert.FromBase64String(base64);
            Assert.IsTrue(InternalImage.TryReadSize(data, out var width, out var height), name);
            var image = InternalImage.Load(data);
            Assert.AreEqual((24, 16), (image.Width, image.Height), name);
            Assert.AreEqual((width, height), (image.Width, image.Height), name);
            Assert.AreEqual(sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(image.Pixels)), name);
        }
    }

    [TestMethod]
    public void Webp_RoundTripKeepsThePictureAndExactAlpha() {
        var source = Picture(333, 251, alpha: true);
        foreach (var quality in new[] { 50, 85 }) {
            var back = InternalImage.Load(Save(source, ImageFormat.Webp, quality));
            Assert.AreEqual((333, 251), (back.Width, back.Height));
            Assert.IsTrue(Psnr(source, back) > 32, $"q{quality}: {Psnr(source, back):F1} dB");
            for (int i = 3; i < source.Pixels.Length; i += 4) Assert.AreEqual(source.Pixels[i], back.Pixels[i], "alpha is kept exactly");
        }
        var opaque = Picture(64, 48);
        var small = Save(opaque, ImageFormat.Webp, 85);
        Assert.AreEqual("VP8 ", System.Text.Encoding.ASCII.GetString(small, 12, 4), "an opaque picture needs no extended header");
        Assert.IsTrue(Save(opaque, ImageFormat.Webp, 50).Length < Save(opaque, ImageFormat.Webp, 95).Length);
    }

    [TestMethod]
    public void Converter_DeclinesFormatsItCannotRead() {
        var converter = new NativeImageConverter();
        Assert.IsTrue(converter.SupportsConversion(FileType.Image, FileFormat.Jpeg, FileType.Image, FileFormat.Png));
        Assert.IsTrue(converter.SupportsConversion(FileType.Image, FileFormat.Webp, FileType.Image, FileFormat.Webp));
        Assert.IsTrue(converter.SupportsConversion(FileType.Image, FileFormat.Gif, FileType.Image, FileFormat.Gif));
        Assert.IsFalse(converter.SupportsConversion(FileType.Image, FileFormat.Avif, FileType.Image, FileFormat.Png));
    }

    static string Hash(InternalImage image) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(image.Pixels));

    // made by Pillow: three interlaced frames over transparency, disposed to the background, then to the frame before, then kept
    const string PillowGif = "R0lGODlhGAAQAIEAAAAAANwoHhRa5gAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQJBQAAACwAAAAAGAAQAAAIPAABCBxIsKDBgwgTKlyIMIDDhw4ZEoQIUeJAig8tCsQYUSPHABoBfAw50iPHkChTqly5UoDLlzBjynwZEAAh+QQNDAAAACwAAAMAGAAMAIEAAADcZB4UWuYAAAAIOQABCBwYoKDBggMTKkx48ODChwQbIoT4UOJEigotBsC4UCPHjBY/MgwpsqTJkygXCljJsqXLlywDAgAh+QQFCAAAACwAAAMAGAAMAIEAAADcoB4UWuYAAAAIOgABCBxIMIDBgwYJKlwIACFChhAbOkwYceFEihULXsyo8GIAjhonghzocaTJkxEFqFzJsqXLlSgZBgQAOw==";
    // its frames as Pillow composes them, transparent pixels as zero
    static readonly string[] PillowGifFrames = [
        "1cd4c6dd942be78f065a3bf528198203fde37a1ec4dfff0efeee146f220dfe0e",
        "49bc7a81e5b5bc76916beb8b2582978cc37931e50ad9927e369cc778253c7215",
        "5e4052adfb33b8ac7c5abf4e4bca6dc94e0c692a78939db327f0ac8a8792e0e3",
    ];

    [TestMethod]
    public void Gif_ReadsEveryFrameAsItIsShown() {
        var data = Convert.FromBase64String(PillowGif);
        Assert.IsTrue(InternalImage.TryReadSize(data, out var width, out var height));
        Assert.AreEqual((24, 16), (width, height));
        var animation = ImageCodecs.ReadAnimation(data)!;
        CollectionAssert.AreEqual(PillowGifFrames, animation.Frames.Select(Hash).ToArray());
        CollectionAssert.AreEqual(new[] { 50, 120, 80 }, animation.Durations);
        Assert.AreEqual(0, animation.Loops);
        Assert.AreEqual(PillowGifFrames[0], Hash(InternalImage.Load(data)), "a still is the first frame");
        Assert.IsTrue(NativeImage.IsAnimated(data));
    }

    // a ball crossing a background of few colours, the fourth frame a repeat of the third
    static Animation Bouncing(bool transparent) {
        var frames = new InternalImage[5];
        for (int i = 0; i < 5; i++) {
            int k = i == 3 ? 2 : i;
            frames[i] = InternalImage.Create(40, 30, (x, y) =>
                Math.Abs(x - 8 - 6 * k) < 5 && Math.Abs(y - 15) < 5 ? new ColorRgba(230, 60, (byte)(40 * k), 255)
                : transparent && x < 10 ? new ColorRgba(0, 0, 0, 0)
                : new ColorRgba(20, 90, (byte)(5 * y), 255));
        }
        return new Animation(frames, [40, 60, 80, 100, 120], 3);
    }

    static readonly int[] Shown = [0, 1, 2, 4]; // the frames left once the repeat is merged

    [TestMethod]
    public void Gif_AnimationRoundTripIsExact() {
        foreach (bool transparent in new[] { false, true }) {
            var source = Bouncing(transparent);
            var ms = new MemoryStream();
            GifCodec.Write(source, ms);
            var back = GifCodec.Read(ms.ToArray(), int.MaxValue);
            CollectionAssert.AreEqual(new[] { 40, 60, 180, 120 }, back.Durations, "a repeated frame is merged into the one before");
            Assert.AreEqual(3, back.Loops);
            for (int i = 0; i < Shown.Length; i++) Assert.AreEqual(Hash(source.Frames[Shown[i]]), Hash(back.Frames[i]), $"frame {Shown[i]}");
        }
    }

    [TestMethod]
    public void Gif_ManyColoursBecomeAPaletteCloseToThem() {
        var source = Picture(160, 120);
        var back = InternalImage.Load(Save(source, ImageFormat.Gif));
        Assert.IsTrue(Psnr(source, back) > 30, $"{Psnr(source, back):F1} dB");

        // a frame differing from the one before by less than the palette tells apart is merged into it
        var nudged = source.Clone();
        nudged.MutablePixels[2] ^= 1;
        var ms = new MemoryStream();
        GifCodec.Write(new Animation([source, nudged], [100, 50], 0), ms);
        Assert.AreEqual(1, GifCodec.Read(ms.ToArray(), int.MaxValue).Frames.Length);
    }

    [TestMethod]
    public void Webp_AnimationIsReadAsLibwebpShowsItAndWritten() {
        var animated = Convert.FromBase64String(LibwebpFiles[2].Base64);
        var frames = WebpCodec.ReadAnimation(animated)!;
        CollectionAssert.AreEqual(new[] { LibwebpFiles[2].Sha256, "161a63602d198c3cb409467ef81a6b13c424e8e8bb53010dde829328a366fad4" }, frames.Frames.Select(Hash).ToArray());
        CollectionAssert.AreEqual(new[] { 100, 100 }, frames.Durations);
        Assert.IsNull(WebpCodec.ReadAnimation(Convert.FromBase64String(LibwebpFiles[0].Base64)), "a still is no animation");

        foreach (bool transparent in new[] { false, true }) {
            var source = Bouncing(transparent);
            var ms = new MemoryStream();
            WebpCodec.WriteAnimation(source, ms, new ImageSaveOptions { Quality = 90 });
            var back = WebpCodec.ReadAnimation(ms.ToArray())!;
            CollectionAssert.AreEqual(new[] { 40, 60, 180, 120 }, back.Durations);
            Assert.AreEqual(3, back.Loops);
            for (int i = 0; i < Shown.Length; i++) {
                var a = source.Frames[Shown[i]];
                var b = back.Frames[i];
                for (int p = 3; p < a.Pixels.Length; p += 4) Assert.AreEqual(a.Pixels[p], b.Pixels[p], "alpha is kept exactly");
                if (!transparent) Assert.IsTrue(Psnr(a, b) > 26, $"frame {Shown[i]}: {Psnr(a, b):F1} dB"); // hard edges at 40x30: libwebp gets 28-30 too
            }
        }
    }

    [TestMethod]
    public void NativeImage_KeepsAnimationsThroughAdjustments() {
        var ms = new MemoryStream();
        GifCodec.Write(Bouncing(false), ms);
        using var image = NativeImage.Load(new MemoryStream(ms.ToArray()));
        Assert.AreEqual(4, image.FrameCount);
        using var adjusted = image.Adjust(new FileAdjustmentImage { Width = 20, Saturation = -50 });
        var gif = GifCodec.Read(adjusted.Encode(FileFormat.Gif), int.MaxValue);
        Assert.AreEqual((4, 20, 15), (gif.Frames.Length, gif.Frames[0].Width, gif.Frames[0].Height));
        var webp = WebpCodec.ReadAnimation(adjusted.Encode(FileFormat.Webp))!;
        Assert.AreEqual((4, 20, 15), (webp.Frames.Length, webp.Frames[0].Width, webp.Frames[0].Height));
        var png = adjusted.Encode(FileFormat.Png);
        Assert.IsFalse(NativeImage.IsAnimated(png));
        Assert.AreEqual(20, InternalImage.Load(png).Width, "a format without animation gets the first frame");
    }

    // ── Crafted files ────────────────────────────────────────────────────────

    static byte[] Riff(params (string Kind, byte[] Payload)[] chunks) {
        var ms = new MemoryStream();
        ms.Write("RIFF\0\0\0\0WEBP"u8);
        foreach (var (kind, payload) in chunks) {
            ms.Write(System.Text.Encoding.ASCII.GetBytes(kind));
            ms.Write(BitConverter.GetBytes(payload.Length));
            ms.Write(payload);
            if ((payload.Length & 1) != 0) ms.WriteByte(0);
        }
        var d = ms.ToArray();
        BitConverter.TryWriteBytes(d.AsSpan(4), d.Length - 8);
        return d;
    }

    static byte[] PngChunk(string kind, byte[] payload) {
        var body = System.Text.Encoding.ASCII.GetBytes(kind).Concat(payload).ToArray();
        uint crc = 0xFFFFFFFF;
        foreach (byte b in body) {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        var d = new byte[body.Length + 8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(d, payload.Length);
        body.CopyTo(d, 4);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(d.Length - 4), ~crc);
        return d;
    }

    // a jpeg whose frame header is made to claim another size
    static byte[] JpegClaiming(int width, int height) {
        var d = Save(Picture(16, 16), ImageFormat.Jpeg, 95);
        int at = d.AsSpan().IndexOf([(byte)0xFF, (byte)0xC0]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(at + 5), (ushort)height);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(at + 7), (ushort)width);
        return d;
    }

    // files of a few bytes that declare pictures of gigabytes: refused before the pixels are allocated
    [TestMethod]
    public void CraftedFiles_AreRefusedBeforeTheirPixelsAreAllocated() {
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, 12000);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), 12000);
        ihdr[8] = 8;
        var bmp = new byte[58];
        "BM"u8.CopyTo(bmp);
        BitConverter.TryWriteBytes(bmp.AsSpan(10), 54);
        BitConverter.TryWriteBytes(bmp.AsSpan(14), 40);
        BitConverter.TryWriteBytes(bmp.AsSpan(18), 10000);
        BitConverter.TryWriteBytes(bmp.AsSpan(22), 10000);
        BitConverter.TryWriteBytes(bmp.AsSpan(26), (short)1);
        BitConverter.TryWriteBytes(bmp.AsSpan(28), (short)0x7fff);
        var crafted = new (string Name, byte[] Data)[] {
            ("webp lossy 16383 x 16383", Riff(("VP8 ", [0x10, 0x02, 0x00, 0x9d, 0x01, 0x2a, 0xff, 0x3f, 0xff, 0x3f, .. new byte[32]]))),
            ("webp lossy 12000 x 12000, under the limit, from 32 bytes", Riff(("VP8 ", [0x10, 0x02, 0x00, 0x9d, 0x01, 0x2a, 0xe0, 0x2e, 0xe0, 0x2e, .. new byte[32]]))),
            ("webp lossless 16384 x 16384", Riff(("VP8L", [0x2f, 0xff, 0xff, 0xff, 0x0f, .. new byte[8]]))),
            ("webp canvas 16777216 x 16", Riff(("VP8X", [0, 0, 0, 0, 0xff, 0xff, 0xff, 15, 0, 0]), ("ANMF", new byte[16]))),
            ("jpeg 20000 x 20000", JpegClaiming(20000, 20000)),
            ("png 12000 x 12000 from 30 bytes of data", [137, 80, 78, 71, 13, 10, 26, 10, .. PngChunk("IHDR", ihdr), .. PngChunk("IDAT", new byte[30]), .. PngChunk("IEND", [])]),
            ("bmp of bit depth 32767", bmp),
        };
        foreach (var (name, data) in crafted) {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.ThrowsExactly<ImageFormatException>(() => InternalImage.Load(data), name);
            Assert.IsTrue(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20, $"{name}: {GC.GetAllocatedBytesForCurrentThread() - before:N0} bytes");
        }

        // a gif frame claiming 40000 x 40000 gets only the room its two bytes of data can fill
        byte[] gif = [.. "GIF89a"u8, 1, 0, 1, 0, 0x80, 0, 0, 0, 0, 0, 255, 255, 255,
            0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x44, 0x01, 0,
            0x2C, 0, 0, 0, 0, 0x40, 0x9c, 0x40, 0x9c, 0, 2, 2, 0x44, 0x01, 0, 0x3B];
        long start = GC.GetAllocatedBytesForCurrentThread();
        Assert.AreEqual(2, ImageCodecs.ReadAnimation(gif)!.Frames.Length);
        Assert.IsTrue(GC.GetAllocatedBytesForCurrentThread() - start < 1 << 20);
    }

    [TestMethod]
    public void Jpeg_ASecondFrameHeaderIsRefused() {
        var d = Save(Picture(16, 16), ImageFormat.Jpeg, 95);
        byte[] crafted = [.. d.AsSpan(0, d.Length - 2),
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x08, 0xFF, 0xFF, 0x03, 1, 0x31, 0, 2, 0x11, 0, 3, 0x11, 0,
            0xFF, 0xDA, 0x00, 0x0C, 0x03, 1, 0x00, 2, 0x00, 3, 0x00, 0x00, 0x3F, 0x00, .. new byte[64], 0xFF, 0xD9];
        Assert.ThrowsExactly<ImageFormatException>(() => InternalImage.Load(crafted));
    }

    [TestMethod]
    public void Jpeg_WhatTheDataDoesNotReachIsGrey() {
        var d = Save(Picture(64, 64), ImageFormat.Jpeg, 95);
        var cut = InternalImage.Load(d[..(d.Length * 6 / 10)]);
        Assert.AreEqual((64, 64), (cut.Width, cut.Height));
        for (int x = 0; x < 64; x++) Assert.AreEqual(new ColorRgba(128, 128, 128), cut[x, 63], $"({x},63)");
    }

    // one 37 x 29 4:2:0 picture written by libjpeg (through Pillow) as baseline, then as progressive, then the baseline's
    // coefficients rewritten with another scan script: a DC scan per component, DC and AC refinement, a restart every
    // 2 blocks. All three hold the same coefficients, and libjpeg decodes them to the same pixels.
    static readonly string[] ProgressiveTwins = [
        "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" +
        "/9GXk76z00LMTwz/2Q==",
    ];

    [TestMethod]
    public void Jpeg_ProgressiveDecodesToThePixelsOfItsBaselineTwin() {
        var files = ProgressiveTwins.Select(Convert.FromBase64String).ToArray();
        for (int i = 1; i < files.Length; i++) {
            Assert.IsTrue(files[i].AsSpan().IndexOf([(byte)0xFF, (byte)0xC2]) > 0, $"file {i} is progressive");
            Assert.IsTrue(InternalImage.TryReadSize(files[i], out var width, out var height));
            Assert.AreEqual((37, 29), (width, height));
        }
        foreach (var scale in new[] { 1, 2, 4, 8 }) {
            var baseline = InternalImage.Load(files[0], scale);
            for (int i = 1; i < files.Length; i++) {
                var progressive = InternalImage.Load(files[i], scale);
                Assert.AreEqual((baseline.Width, baseline.Height), (progressive.Width, progressive.Height));
                Assert.IsTrue(baseline.Pixels.SequenceEqual(progressive.Pixels), $"file {i} at 1/{scale}");
            }
        }
    }

    [TestMethod]
    public void Jpeg_ProgressiveCutShortShowsWhatItsScansGive() {
        var d = Convert.FromBase64String(ProgressiveTwins[1]);
        var whole = InternalImage.Load(d);
        var cut = InternalImage.Load(d[..(d.Length * 6 / 10)]);
        Assert.AreEqual((37, 29), (cut.Width, cut.Height));
        Assert.IsTrue(Psnr(whole, cut) > 20, $"{Psnr(whole, cut):F1} dB: the whole picture, less detailed");
    }

    // a refinement scan over blocks of zeros reads next to no data but visits every coefficient: repeated, scans of a
    // few bytes each could make a small file take minutes. A coefficient has a first scan and at most 13 refinements.
    [TestMethod]
    public void Jpeg_ScansRepeatedOverTheSameCoefficientsAreRefused() {
        static byte[] crafted(int scans) {
            // 64 x 64 greyscale, progressive; an AC table of one code: an end-of-band run of 64 blocks
            List<byte> d = [0xFF, 0xD8,
                0xFF, 0xDB, 0x00, 0x43, 0x00, .. Enumerable.Repeat((byte)1, 64),
                0xFF, 0xC2, 0x00, 0x0B, 0x08, 0x00, 0x40, 0x00, 0x40, 0x01, 0x01, 0x11, 0x00,
                0xFF, 0xC4, 0x00, 0x14, 0x10, 0x01, .. new byte[15], 0x60];
            // AC 1-63 refining bit 0: the run's code, its 6 extra bits, padding
            for (int i = 0; i < scans; i++) d.AddRange([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x01, 0x3F, 0x10, 0x01]);
            d.AddRange([0xFF, 0xD9]);
            return [.. d];
        }
        var allowed = InternalImage.Load(crafted(16));
        Assert.AreEqual(new ColorRgba(128, 128, 128), allowed[63, 63]);
        Assert.ThrowsExactly<ImageFormatException>(() => InternalImage.Load(crafted(17)));
    }

    [TestMethod]
    public void Jpeg_AnOversubscribedHuffmanTableIsRefused() {
        var d = Save(Picture(16, 16), ImageFormat.Jpeg, 95);
        // three codes of length 1, where there is room for two
        byte[] dht = [0xFF, 0xC4, 0x00, 0x16, 0x00, 3, .. new byte[15], 0, 1, 2];
        Assert.ThrowsExactly<ImageFormatException>(() => InternalImage.Load([.. d.AsSpan(0, 2), .. dht, .. d.AsSpan(2)]));
    }

    [TestMethod]
    public void Webp_TruncatedDataIsRefused() {
        // the second is large enough to be decoded by the three-thread pipeline
        foreach (var (w, h) in new[] { (256, 256), (700, 500) }) {
            var d = Save(Picture(w, h), ImageFormat.Webp, 85);
            Assert.AreEqual("VP8 ", System.Text.Encoding.ASCII.GetString(d, 12, 4));
            int length = BitConverter.ToInt32(d, 16) * 3 / 4;
            var cut = Riff(("VP8 ", d.AsSpan(20, length).ToArray()));
            Assert.ThrowsExactly<ImageFormatException>(() => InternalImage.Load(cut), $"{w} x {h}");
            Assert.AreEqual((w, h), (InternalImage.Load(d).Width, InternalImage.Load(d).Height), "the whole file still decodes");
        }
    }

    [TestMethod]
    public void Jpeg_ProgressiveAtAnEighthKeepsOnlyTheDc() {
        // 16000 x 16000 greyscale, one DC scan: more than ImageLimits.MaxPixels in full and 512 MB of coefficients,
        // at 1/8 a 2000 x 2000 picture and a coefficient a block
        byte[] d = [0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x43, 0x00, .. Enumerable.Repeat((byte)1, 64),
            0xFF, 0xC2, 0x00, 0x0B, 0x08, 0x3E, 0x80, 0x3E, 0x80, 0x01, 0x01, 0x11, 0x00,
            0xFF, 0xC4, 0x00, 0x14, 0x00, 0x01, .. new byte[15], 0x00,
            0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xD9];
        Assert.ThrowsExactly<ImageFormatException>(() => InternalImage.Load(d));
        long before = GC.GetAllocatedBytesForCurrentThread();
        var small = InternalImage.Load(d, 8);
        Assert.AreEqual((2000, 2000), (small.Width, small.Height));
        Assert.IsTrue(GC.GetAllocatedBytesForCurrentThread() - before < 40 << 20, $"{GC.GetAllocatedBytesForCurrentThread() - before:N0} bytes");
    }

    // 6 x 4 patches of 8 x 8 written by libjpeg (through Pillow) at quality 100, so each decodes to exactly its values:
    // the 16 corners of CMYK (ink 0 or 255, C varying slowest), then 8 in between. As RGB, the same numbers.
    static readonly int[][] Inks = [[0, 0, 0, 0], [0, 0, 0, 255], [0, 0, 255, 0], [0, 0, 255, 255], [0, 255, 0, 0], [0, 255, 0, 255],
        [0, 255, 255, 0], [0, 255, 255, 255], [255, 0, 0, 0], [255, 0, 0, 255], [255, 0, 255, 0], [255, 0, 255, 255], [255, 255, 0, 0],
        [255, 255, 0, 255], [255, 255, 255, 0], [255, 255, 255, 255], [30, 60, 90, 20], [200, 10, 128, 64], [128, 128, 128, 128],
        [5, 250, 100, 1], [77, 180, 104, 241], [255, 128, 0, 30], [10, 20, 30, 40], [240, 200, 160, 120]];
    const string CmykPatches =
        "/9j/7gAOQWRvYmUAZAAAAAAA/9sAQwABAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEB" +
        "/8AAFAgAIAAwBEMRAE0RAFkRAEsRAP/EAB8AAAEFAQEBAQEBAAAAAAAAAAABAgMEBQYHCAkKC//EALUQAAIBAwMCBAMFBQQEAAABfQECAwAEEQUSITFB" +
        "BhNRYQcicRQygZGhCCNCscEVUtHwJDNicoIJChYXGBkaJSYnKCkqNDU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6g4SFhoeIiYqS" +
        "k5SVlpeYmZqio6Slpqeoqaqys7S1tre4ubrCw8TFxsfIycrS09TV1tfY2drh4uPk5ebn6Onq8fLz9PX29/j5+v/aAA4EQwBNAFkASwAAPwD+/iv7+K/v" +
        "4r+/iiiiv8Aeiiv8Aev9/iiiiv8AAHor/AHr/f4r/f4ooor/AAB6KK/wB6/3+KKKK/wB6/wB6/3+K/3+K/3+KKKK/wAAeiiv8Aev9/iiiiv8Aeiv8Aev" +
        "9/iv9/iiiiv8Aeiiv8Aev9/iiiiv8Aev9wiv9hiv9Siv91iv8q+v2Qr8z6/J+v6QK/hPor+f+v72K/gvr7gr+/iv5v6/owrn6/wf6/yb6/aCv7QK/wBp" +
        "iv8Aeor+2Cvg+vL6/wAM+v8AJfr/AD76/l/r/9k=";
    const string YcckPatches =
        "/9j/7gAOQWRvYmUAZAAAAAAC/9sAQwABAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEB" +
        "/8AAFAgAIAAwBEMRAE0RAFkRAEsRAP/EAB8AAAEFAQEBAQEBAAAAAAAAAAABAgMEBQYHCAkKC//EALUQAAIBAwMCBAMFBQQEAAABfQECAwAEEQUSITFB" +
        "BhNRYQcicRQygZGhCCNCscEVUtHwJDNicoIJChYXGBkaJSYnKCkqNDU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6g4SFhoeIiYqS" +
        "k5SVlpeYmZqio6Slpqeoqaqys7S1tre4ubrCw8TFxsfIycrS09TV1tfY2drh4uPk5ebn6Onq8fLz9PX29/j5+v/aAA4EQwBNAFkASwAAPwD/AD/6KK/v" +
        "4ooor/AHr7or+/ivlev9/iiiiv8AAHr+8Cv8V+v5T6/3+KKKK/wB6+6K/wBACvlev9/iiiiv8Aev4z6/lHr/AH+K/wB/iiiiv8Aevuiv9ACvlev9/iii" +
        "iv8AAHr+8Cv8V+v5T6/3+KKKK/wB6+8K/wBACvl+v9/iiiiv8Aev8bevqCvm+v8AdYr7QrsK/s4r8n6/YCvi+v5V6/n/AK++K/O+v4h6/v4r5nr2iv6C" +
        "K/wf69Yr+fev7uK/2mK/z16/rgr+YevL6/16K/Pevuiv5f6//9k=";
    const string RgbPatches =
        "/9j/7gAOQWRvYmUAZAAAAAAA/9sAQwABAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEB" +
        "/8AAEQgAIAAwA1IRAEcRAEIRAP/EAB8AAAEFAQEBAQEBAAAAAAAAAAABAgMEBQYHCAkKC//EALUQAAIBAwMCBAMFBQQEAAABfQECAwAEEQUSITFBBhNR" +
        "YQcicRQygZGhCCNCscEVUtHwJDNicoIJChYXGBkaJSYnKCkqNDU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6g4SFhoeIiYqSk5SV" +
        "lpeYmZqio6Slpqeoqaqys7S1tre4ubrCw8TFxsfIycrS09TV1tfY2drh4uPk5ebn6Onq8fLz9PX29/j5+v/aAAwDUgBHAEIAAD8A/wA/+v8AP/r/AD/6" +
        "KKKKK/3+KKKKK/3+K/wB6KKKKK/3+KKKK/3+K/wB6/wB6KKKKK/3+KKKKK/3+K/wB6KKKKK/3+KKKK/w96/x56/y16/1UK/G+v0wr+b+v7sKK/gnr+9C" +
        "vh+v6QK/nProK/1kK/F+v4v6/wAFev4n6+8K/wBzCv8AWgr/AEEK/9k=";

    static ColorRgba Patch(InternalImage image, int i) => image[(i % 6) * 8 + 4, (i / 6) * 8 + 4];

    static bool Near(ColorRgba a, ColorRgba b, int tolerance) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    // without a profile, as Skia, Pillow and browsers: values are Adobe's (inverted), a channel (255 - C) * (255 - K) / 255
    static ColorRgba InkTimesBlack(int[] ink) {
        byte channel(int c) => (byte)Math.Round((255 - ink[c]) * (255 - ink[3]) / 255.0);
        return new ColorRgba(channel(0), channel(1), channel(2));
    }

    [TestMethod]
    public void Jpeg_CmykAndYcckAreInkTimesBlack() {
        var cmyk = InternalImage.Load(Convert.FromBase64String(CmykPatches));
        var ycck = InternalImage.Load(Convert.FromBase64String(YcckPatches));
        for (int i = 0; i < Inks.Length; i++) {
            var expected = InkTimesBlack(Inks[i]);
            Assert.AreEqual(expected, Patch(cmyk, i), $"CMYK patch {i}");
            Assert.IsTrue(Near(expected, Patch(ycck, i), 2), $"YCCK patch {i}: {Patch(ycck, i)} for {expected}"); // YCbCr rounds
        }
    }

    [TestMethod]
    public void Jpeg_RgbWithoutColourTransformIsRead() {
        var d = Convert.FromBase64String(RgbPatches);
        byte[] withoutAdobeMarker = [.. d.AsSpan(0, 2), .. d.AsSpan(18)]; // then only the component ids, R G B, say RGB
        foreach (var data in new[] { d, withoutAdobeMarker }) {
            var image = InternalImage.Load(data);
            for (int i = 0; i < Inks.Length; i++) Assert.AreEqual(new ColorRgba((byte)Inks[i][0], (byte)Inks[i][1], (byte)Inks[i][2]), Patch(image, i));
        }
    }

    // a CMYK profile made here, its A2B0 a lut16, lut8 or lutAtoB table taking each corner of CMYK to a colour (as Lab)
    [TestMethod]
    public void Jpeg_CmykIsConvertedThroughItsIccProfile() {
        ColorRgba[] corners = [new(255, 255, 255), new(40, 40, 40), new(250, 230, 20), new(60, 50, 0), new(230, 0, 140), new(70, 0, 40),
            new(220, 30, 40), new(50, 10, 10), new(0, 170, 230), new(0, 50, 70), new(0, 160, 80), new(0, 40, 20), new(50, 50, 150),
            new(10, 10, 40), new(30, 30, 30), new(0, 0, 0)];
        var file = Convert.FromBase64String(CmykPatches);
        foreach (var type in new[] { "mft2", "mft1", "mAB " }) {
            var icc = CmykProfileOf(corners, type, out var expected);
            var image = InternalImage.Load(WithIcc(file, icc));
            for (int i = 0; i < 16; i++) Assert.IsTrue(Near(expected[i], Patch(image, i), 1), $"{type} corner {i}: {Patch(image, i)} for {expected[i]}");
            // in two parts, out of order, as the standard allows
            var split = WithIcc(WithIcc(file, icc[..100], 1, 2), icc[100..], 2, 2);
            Assert.IsTrue(InternalImage.Load(split).Pixels.SequenceEqual(image.Pixels), type);
        }
        // a profile that cannot be read, or one with a part missing, leaves the plain conversion
        var mft2 = CmykProfileOf(corners, "mft2", out _);
        foreach (var data in new[] { WithIcc(file, mft2[..200]), WithIcc(file, mft2[..100], 1, 2) }) {
            var image = InternalImage.Load(data);
            for (int i = 0; i < Inks.Length; i++) Assert.AreEqual(InkTimesBlack(Inks[i]), Patch(image, i));
        }
    }

    static byte[] WithIcc(byte[] jpeg, byte[] icc, int part = 1, int parts = 1) {
        int length = 2 + 14 + icc.Length;
        return [.. jpeg.AsSpan(0, 2), 0xFF, 0xE2, (byte)(length >> 8), (byte)length, .. "ICC_PROFILE "u8, (byte)part, (byte)parts, .. icc, .. jpeg.AsSpan(2)];
    }

    // sRGB's colorants adapted to D50, the connection space's white, as ICC profiles state them
    static readonly double[,] SrgbToXyz = { { 0.4360747, 0.3850649, 0.1430804 }, { 0.2225045, 0.7168786, 0.0606169 }, { 0.0139322, 0.0971045, 0.7141733 } };
    static readonly double[] D50 = [0.9642, 1, 0.8249];

    static double[] Lab(ColorRgba c) {
        static double linear(byte v) => v / 255.0 <= 0.04045 ? v / 255.0 / 12.92 : Math.Pow((v / 255.0 + 0.055) / 1.055, 2.4);
        static double f(double t) => t > Math.Pow(6 / 29.0, 3) ? Math.Cbrt(t) : t / (3 * (6 / 29.0) * (6 / 29.0)) + 4 / 29.0;
        double[] rgb = [linear(c.R), linear(c.G), linear(c.B)], xyz = new double[3];
        for (int i = 0; i < 3; i++) xyz[i] = f((SrgbToXyz[i, 0] * rgb[0] + SrgbToXyz[i, 1] * rgb[1] + SrgbToXyz[i, 2] * rgb[2]) / D50[i]);
        return [116 * xyz[1] - 16, 500 * (xyz[0] - xyz[1]), 200 * (xyz[1] - xyz[2])];
    }

    static ColorRgba Srgb(double[] lab) {
        static double f(double t) => t > 6 / 29.0 ? t * t * t : 3 * (6 / 29.0) * (6 / 29.0) * (t - 4 / 29.0);
        double fy = (lab[0] + 16) / 116;
        double[] xyz = [D50[0] * f(fy + lab[1] / 500), f(fy), D50[2] * f(fy - lab[2] / 200)];
        // solved by Cramer's rule, then sRGB's curve
        var m = SrgbToXyz;
        double det(double[,] a) => a[0, 0] * (a[1, 1] * a[2, 2] - a[1, 2] * a[2, 1]) - a[0, 1] * (a[1, 0] * a[2, 2] - a[1, 2] * a[2, 0]) + a[0, 2] * (a[1, 0] * a[2, 1] - a[1, 1] * a[2, 0]);
        byte channel(int col) {
            var a = (double[,])m.Clone();
            for (int r = 0; r < 3; r++) a[r, col] = xyz[r];
            double v = Math.Clamp(det(a) / det(m), 0, 1);
            return (byte)Math.Round(255 * (v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055));
        }
        return new ColorRgba(channel(0), channel(1), channel(2));
    }

    // expected: the corners' colours as the table holds them, its Lab rounded to its encoding
    static byte[] CmykProfileOf(ColorRgba[] corners, string type, out ColorRgba[] expected) {
        static void be16(List<byte> o, int v) { o.Add((byte)(v >> 8)); o.Add((byte)v); }
        static void be32(List<byte> o, uint v) => o.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        // Lab's encodings: lut16's legacy one (L 100 = 0xFF00), lut8's, and v4's of lutAtoB
        var (kl, kab) = type switch { "mft2" => (652.8, 256.0), "mft1" => (2.55, 1.0), _ => (655.35, 65535 / 255.0) };
        var encoded = corners.Select(c => Lab(c)).Select(l => new[] { (int)Math.Round(l[0] * kl), (int)Math.Round((l[1] + 128) * kab), (int)Math.Round((l[2] + 128) * kab) }).ToArray();
        expected = [.. encoded.Select(v => Srgb([v[0] / kl, v[1] / kab - 128, v[2] / kab - 128]))];
        var clut = new List<byte>();
        foreach (var v in encoded) foreach (var x in v) if (type == "mft1") clut.Add((byte)x); else be16(clut, x);
        var t = new List<byte>();
        t.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
        be32(t, 0);
        if (type == "mAB ") {
            // B curves at 32: three identities; CLUT at 68; A curves at 184: two identities and two gamma 1 functions
            t.AddRange([4, 3, 0, 0]);
            foreach (var offset in new uint[] { 32, 0, 0, 68, 184 }) be32(t, offset);
            for (int i = 0; i < 3; i++) { t.AddRange("curv"u8); be32(t, 0); be32(t, 0); }
            t.AddRange([2, 2, 2, 2, .. new byte[12], 2, 0, 0, 0, .. clut]);
            for (int i = 0; i < 2; i++) { t.AddRange("curv"u8); be32(t, 0); be32(t, 0); }
            for (int i = 0; i < 2; i++) { t.AddRange("para"u8); be32(t, 0); be32(t, 0); be32(t, 1 << 16); }
        } else {
            t.AddRange([4, 3, 2, 0]);
            for (int i = 0; i < 9; i++) be32(t, i % 4 == 0 ? 1u << 16 : 0);
            if (type == "mft2") {
                t.AddRange([0, 2, 0, 2]); // two entries in each input and output table
                for (int i = 0; i < 4; i++) { be16(t, 0); be16(t, 65535); }
                t.AddRange(clut);
                for (int i = 0; i < 3; i++) { be16(t, 0); be16(t, 65535); }
            } else {
                for (int i = 0; i < 4; i++) for (int v = 0; v < 256; v++) t.Add((byte)v);
                t.AddRange(clut);
                for (int i = 0; i < 3; i++) for (int v = 0; v < 256; v++) t.Add((byte)v);
            }
        }
        var p = new List<byte>();
        be32(p, (uint)(144 + t.Count));
        p.AddRange([0, 0, 0, 0, type == "mAB " ? (byte)4 : (byte)2, 0x20, 0, 0, .. "prtrCMYKLab "u8, .. new byte[12], .. "acsp"u8]);
        p.AddRange(new byte[128 - p.Count]);
        be32(p, 1);
        p.AddRange("A2B0"u8);
        be32(p, 144);
        be32(p, (uint)t.Count);
        p.AddRange(t);
        return [.. p];
    }

    [TestMethod]
    public void Adjust_RefusesAResultLargerThanTheLimit() {
        using var image = NativeImage.Create(100, 100);
        Assert.ThrowsExactly<ImageFormatException>(() => image.Adjust(new FileAdjustmentImage { Width = 30000, Height = 30000 }));
        using var tall = NativeImage.Create(1, 100);
        Assert.ThrowsExactly<ImageFormatException>(() => tall.Adjust(new FileAdjustmentImage { Width = 20000 }), "the height follows the width");
        using var fits = image.Adjust(new FileAdjustmentImage { Width = 3000 });
        Assert.AreEqual((3000, 3000), (fits.Width, fits.Height));
        Assert.ThrowsExactly<ImageFormatException>(() => image.Adjust(new FileAdjustmentImage { Width = 5000 }), "enlarged beyond MaxEnlargedPixels");
        using var large = NativeImage.Create(5000, 4000);
        using var smaller = large.Adjust(new FileAdjustmentImage { Width = 4800 });
        Assert.AreEqual((4800, 3840), (smaller.Width, smaller.Height), "past MaxEnlargedPixels but no larger than its source: allowed");
    }
}

