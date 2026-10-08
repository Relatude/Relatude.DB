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
}

