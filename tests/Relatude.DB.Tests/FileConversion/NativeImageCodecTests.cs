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

    [TestMethod]
    public void Converter_DeclinesFormatsItCannotRead() {
        var converter = new NativeImageConverter();
        Assert.IsTrue(converter.SupportsConversion(FileType.Image, FileFormat.Jpeg, FileType.Image, FileFormat.Png));
        Assert.IsFalse(converter.SupportsConversion(FileType.Image, FileFormat.Gif, FileType.Image, FileFormat.Png));
        Assert.IsFalse(converter.SupportsConversion(FileType.Image, FileFormat.Webp, FileType.Image, FileFormat.Png));
    }
}
