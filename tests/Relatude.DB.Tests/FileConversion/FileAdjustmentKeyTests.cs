using Relatude.DB.Common;
using Relatude.DB.FileConversion;
using Relatude.DB.Web;

namespace Relatude.FileConversion;

/// <summary>
/// The keys conversions are cached under. A key that moves makes every conversion under it run again
/// and leaves the old files orphaned in the cache, so the keys of persistent adjustments are pinned
/// here to the values they have had since the cache was introduced: a change to how keys are made
/// has to fail one of these tests rather than quietly reconvert a site's pictures after a deploy.
/// </summary>
[TestClass]
public class FileAdjustmentKeyTests {
    static readonly Guid _fileId = new("6f1c2a4e-8b3d-4c5e-9f70-112233445566");
    static Guid conversionKey(FileAdjustmentBase adj) => new FileIdWithAdjustment(_fileId, adj, new PropertyPath(1, Guid.Empty)).GetKey();

    [TestMethod]
    public void PersistentKeysAreUnchanged() {
        (FileAdjustmentBase Adjustment, string Key, string ConversionKey)[] pinned = [
            (new FileAdjustmentImage { Width = 200 },
                "16a440cd-2cfa-61bb-2214-65804aa63055", "bfe8ddc8-2f59-2d8d-4a69-0378db1d13f5"),
            (new FileAdjustmentImage { RequestedFormat = FileFormat.Webp, Width = 300, Height = 200, CropMode = ImageCropMode.Fill, Quality = 80 },
                "a30544ed-e983-91dd-b9b1-e540919ffa72", "a847eeaa-d87d-43ea-55f9-872dbcca03a3"),
            (new FileAdjustmentImage {
                RequestedFormat = FileFormat.Jpeg, Width = 100, Height = 200, Quality = 80, CropMode = ImageCropMode.Fill, Saturation = -50, Zoom = 150,
                FocusX = 10, FocusY = 20, Rotation = 90, InvertLuminance = true, AutoBackgroundColor = false, BackgroundColor = "#aabbcc", TimeOffsetMs = 4000,
            }, "ad62d363-8489-2e3c-1039-34379186ba32", "fe4e7162-a140-ba49-15c6-a6c783ad60b7"),
            (new FileAdjustmentImage { RequestedFormat = FileFormat.Png, Width = 512, Height = 512, SourceX = 10, SourceY = 20, SourceWidth = 300, SourceHeight = 300, AutoLightDarkMode = AutoLightDarkSwitch.AdaptToLightModeIfNeeded },
                "fdbb9444-1507-222e-fc3f-8131cf1fd13c", "ce123b22-0968-2668-7209-4e8cb2b42541"),
            (new FileAdjustmentVideo { RequestedFormat = FileFormat.Mp4, Width = 640, Height = 360, TargetBitRateInMbps = 2.5 },
                "4514ea1d-5f2b-161a-e24d-1c96befbf8d0", "5ff63bc3-6f89-f314-da7a-24446a581e00"),
            (new FileAdjustmentVideo { RequestedFormat = FileFormat.Mp4, Width = 320, TargetBitRateInMbps = 1, CropNotZoom = true },
                "e4aa2d20-c319-b78d-bd55-5598f8f87e1c", "a6d5d1ee-d1b1-382e-cd7f-2c54a4ddfb14"),
        ];
        foreach (var (adjustment, key, conversion) in pinned) {
            Assert.AreEqual(Guid.Parse(key), adjustment.GetKey(), "adjustment key moved");
            Assert.AreEqual(Guid.Parse(conversion), conversionKey(adjustment), "conversion key moved");
            Assert.AreEqual(Guid.Parse(key), adjustment.Normalized().GetKey(), "an adjustment that is already in range keeps its key when normalized");
        }
    }

    [TestMethod]
    public void EnumNumbersAreFixed() {
        // written into keys and encoded URLs: inserting a member mid list once renumbered every video format
        FileFormat[] formats = [
            FileFormat.Jpeg, FileFormat.Png, FileFormat.Gif, FileFormat.Bmp, FileFormat.Svg, FileFormat.Webp, FileFormat.Avif, FileFormat.Image,
            FileFormat.Mp4, FileFormat.Avi, FileFormat.Mov, FileFormat.Wmv, FileFormat.Mkv, FileFormat.Flv,
            FileFormat.Mp3, FileFormat.Wav, FileFormat.Aac, FileFormat.Flac,
            FileFormat.Pdf, FileFormat.Doc, FileFormat.Docx, FileFormat.Xls, FileFormat.Xlsx, FileFormat.Ppt, FileFormat.Pptx, FileFormat.Txt,
            FileFormat.FileMetaJson, FileFormat.Unknown,
        ];
        for (var i = 0; i < formats.Length; i++) Assert.AreEqual(i, (int)formats[i], formats[i].ToString());
        Assert.AreEqual(formats.Length, Enum.GetValues<FileFormat>().Length, "a new format needs the next free number, and a line above");
        Assert.AreEqual(0, (int)ImageCropMode.Fill);
        Assert.AreEqual(1, (int)ImageCropMode.Fit);
        Assert.AreEqual(2, (int)ImageCropMode.Stretch);
        Assert.AreEqual(3, (int)ImageCropMode.Auto);
        Assert.AreEqual(0, (int)AutoLightDarkSwitch.None);
        Assert.AreEqual(1, (int)AutoLightDarkSwitch.AdaptToLightModeIfNeeded);
        Assert.AreEqual(2, (int)AutoLightDarkSwitch.AdaptToDarkModeIfNeeded);
        Assert.AreEqual(0, (int)FileAdjustmentType.Image);
        Assert.AreEqual(1, (int)FileAdjustmentType.Video);
        Assert.AreEqual(2, (int)FileAdjustmentType.Meta);
    }

    [TestMethod]
    public void TheKeyFollowsChangesToTheAdjustment() {
        // an adjustment reused for a list of sizes: the key, and the URL made from it, follow each change
        var adj = new FileAdjustmentImage { Width = 100 };
        var first = adj.GetKey();
        adj.Width = 200;
        Assert.AreNotEqual(first, adj.GetKey());
        Assert.AreEqual(new FileAdjustmentImage { Width = 200 }.GetKey(), adj.GetKey());

        var encoder = new BinaryUrlFileAdjustmentEncoder(Guid.Empty);
        adj.Width = 100;
        var url100 = encoder.GetEncodedString(adj);
        adj.Width = 200;
        var url200 = encoder.GetEncodedString(adj);
        Assert.AreNotEqual(url100, url200);
        Assert.AreEqual(200, ((FileAdjustmentImage)encoder.GetAdjustmentFromEncodedString(url200)).Width);
    }

    [TestMethod]
    public void TemporaryIsPartOfTheKey() {
        // a temporary result is kept in memory only: sharing a key let it stand in for a persistent one,
        // which then never reached the disk
        Assert.AreNotEqual(new FileAdjustmentImage { Width = 200 }.GetKey(), new FileAdjustmentImage { Width = 200, Temporary = true }.GetKey());
        Assert.AreNotEqual(new FileAdjustmentVideo { Width = 200 }.GetKey(), new FileAdjustmentVideo { Width = 200, Temporary = true }.GetKey());
        Assert.AreNotEqual(new FileAdjustmentMeta().GetKey(), new FileAdjustmentMeta { Temporary = false }.GetKey());
    }

    [TestMethod]
    public void TheMetaKeyCarriesItsFormat() {
        Assert.AreNotEqual(new FileAdjustmentMeta().GetKey(), new FileAdjustmentMeta { RequestedFormat = FileFormat.Txt }.GetKey());
        Assert.AreEqual(new FileAdjustmentMeta().GetKey(), new FileAdjustmentMeta().GetKey());
    }

    [TestMethod]
    public void EquivalentRequestsNormalizeToOneKey() {
        // a readable URL is not sanitized when it is parsed, an encoded one is
        var readable = FileAdjustmentUrlCodec.TryParseShortString("w200zm100fwebp")!;
        var encoder = new BinaryUrlFileAdjustmentEncoder(Guid.Empty);
        var encoded = encoder.GetAdjustmentFromEncodedString(encoder.GetEncodedString(readable));
        Assert.AreNotEqual(readable.GetKey(), encoded.GetKey(), "the two paths disagree before normalizing");
        Assert.AreEqual(encoded.GetKey(), readable.Normalized().GetKey());
        Assert.AreEqual(encoded.GetKey(), encoded.Normalized().GetKey(), "normalizing twice changes nothing");
        Assert.AreEqual(100, ((FileAdjustmentImage)readable).Zoom, "the instance normalized is left as it was");

        var canonical = new FileAdjustmentImage { Width = 200, BackgroundColor = "#aabbcc" };
        foreach (var color in new[] { "#AABBCC", "aabbcc", " #AaBbCc " }) {
            Assert.AreEqual(canonical.GetKey(), new FileAdjustmentImage { Width = 200, BackgroundColor = color }.Normalized().GetKey(), color);
        }
        Assert.AreEqual(new FileAdjustmentImage { Width = 200 }.GetKey(), new FileAdjustmentImage { Width = 200, BackgroundColor = "#" }.Normalized().GetKey());

        var huge = (FileAdjustmentImage)FileAdjustmentUrlCodec.TryParseQuery("/x?w=50000&h=0&q=500")!.Normalized();
        Assert.AreEqual(10_000, huge.Width);
        Assert.IsNull(huge.Height);
        Assert.AreEqual(100, huge.Quality);
    }

    [TestMethod]
    public void AVideoWithoutABitRateKeepsTheConverterDefault() {
        // 0 is "not set": sanitizing used to clamp it to 0.01 Mbps
        Assert.AreEqual(0, ((FileAdjustmentVideo)new FileAdjustmentVideo { RequestedFormat = FileFormat.Mp4 }.Normalized()).TargetBitRateInMbps);
        Assert.AreEqual(0.01, ((FileAdjustmentVideo)new FileAdjustmentVideo { TargetBitRateInMbps = 0.001 }.Normalized()).TargetBitRateInMbps);
        Assert.AreEqual(100, ((FileAdjustmentVideo)new FileAdjustmentVideo { TargetBitRateInMbps = 500 }.Normalized()).TargetBitRateInMbps);
    }
}
