using System.Reflection;
using Relatude.DB.Common;
using Relatude.DB.FileConversion;
using Relatude.DB.FileConversion.ImageEncoders;
using Relatude.DB.IO;

namespace Relatude.FileConversion;

/// <summary>
/// The pictures answered while a conversion is not ready. They are cached by their text and size,
/// and the cache has to stay within its budget: entries used to be added with size 0, which the
/// cache reserves for entries it must never evict, so every status ever rendered stayed in memory.
/// </summary>
[TestClass]
public class FileConversionStatusTests {
    static FileValue png() => FileValue.CreateNew("photo.png", 10, "hash", Guid.Empty, Guid.NewGuid(), [], new PropertyPath(1, Guid.Empty));
    static FileConversionEngine engine() => new(null!, [new NativeImageConverter()], new IOProviderMemory()); // the store is only used to log
    static Cache<Guid, byte[]> statusCacheOf(FileConversionEngine engine)
        => (Cache<Guid, byte[]>)typeof(FileConversionEngine).GetField("_statusCache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;

    [TestMethod]
    public void StatusPicturesCountAgainstTheCacheBudget() {
        using var e = engine();
        var file = png();
        for (var width = 100; width < 120; width++) {
            using var _ = e.GetStatusDataStream(file, new FileAdjustmentImage { RequestedFormat = FileFormat.Png, Width = width, Height = 80 }, new FileConversionProgressInfo());
        }
        var cache = statusCacheOf(e);
        Assert.AreEqual(20, cache.Count);
        Assert.AreEqual(0, cache.CountZeroSize, "a status picture is not an entry the cache must keep");
        Assert.IsTrue(cache.Size > 0, "and counts against the budget, so the cache can evict it");
        cache.ClearAll_NotSize0();
        Assert.AreEqual(0, cache.Count);
    }

    [TestMethod]
    public void AStatusPictureIsNeverRenderedHuge() {
        using var e = engine();
        using var stream = e.GetStatusDataStream(png(), new FileAdjustmentImage { RequestedFormat = FileFormat.Png, Width = 10_000, Height = 5_000 }, new FileConversionProgressInfo());
        Assert.IsFalse(stream.CanWrite, "the bytes are the cached array");
        var (width, height) = NativeImage.ReadSize(stream);
        Assert.AreEqual(1920, width);
        Assert.AreEqual(960, height, "scaled down keeping the aspect");
    }
}
