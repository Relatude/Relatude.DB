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

