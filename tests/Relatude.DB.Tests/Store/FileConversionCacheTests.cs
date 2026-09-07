using Relatude.DB.Common;
using Relatude.DB.FileConversion;
using Relatude.DB.IO;

namespace Relatude.Store;

/// <summary>
/// The naming and the clearing of the converted file cache. Names carry the extension of the
/// format they were converted to, and the cache is sharded over two folder levels named by the
/// leading hex chars of the conversion key.
/// </summary>
[TestClass]
public class FileConversionCacheTests {
    const string _base = FileKeyUtility.ConvertedFolderName;
    static FileIdWithAdjustment adjustment(FileFormat requestedFormat, int width = 100)
        => new(Guid.NewGuid(), new FileAdjustmentImage { RequestedFormat = requestedFormat, Width = width }, new PropertyPath(1, Guid.Empty));
    static string[] pathOf(FileIdWithAdjustment id, string extension) {
        var key = id.GetKey().ToString();
        return [_base, key[..2], key[2..4], key + extension];
    }
    static async Task<byte[]> read(FileConversionCache cache, FileIdWithAdjustment id) {
        Assert.IsTrue(cache.TryGetResultAndStream(id, out var result), "nothing cached for the conversion");
        Assert.AreEqual(FileConversionStatus.Ready, result.ProgressInfo.Status);
        Assert.IsNotNull(result.Output);
        var buffer = new MemoryStream();
        await result.Output.CopyToAsync(buffer);
        return buffer.ToArray();
    }
    /// <summary>A cache reading what another one wrote, so lookups go to the io provider rather
    /// than to the in memory copy of the small file the writing instance keeps.</summary>
    static FileConversionCache reopen(IIOProvider io) => new(io, _base);

    [TestMethod]
    public async Task CachedFileCarriesTheRequestedFormatExtension() {
        var io = new IOProviderMemory();
        var cache = new FileConversionCache(io, _base);
        var id = adjustment(FileFormat.Webp);
        await cache.SetFromStreamAsync(id, new MemoryStream([1, 2, 3]));
        Assert.IsTrue(io.Exists(pathOf(id, ".webp")), "the cached file should carry the extension of the format it was converted to");
        Assert.IsFalse(io.Exists(pathOf(id, string.Empty)), "no extensionless file should be written anymore");
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await read(reopen(io), id));
        Assert.IsTrue(reopen(io).TryGetStatusNoStream(id, out var status));
        Assert.AreEqual(FileConversionStatus.Ready, status.Status);
    }
    [TestMethod]
    public async Task AFormatWithoutAnExtensionOfItsOwnFallsBackToBin() {
        // FileFormat.Image is the adaptive format: it is resolved to a concrete one before a
        // conversion is cached, so this is the fallback rather than something the engine writes
        var io = new IOProviderMemory();
        var cache = new FileConversionCache(io, _base);
        var id = adjustment(FileFormat.Image);
        await cache.SetFromStreamAsync(id, new MemoryStream([1]));
        Assert.IsTrue(io.Exists(pathOf(id, ".bin")));
    }
    [TestMethod]
    public async Task ClearRemovesTheFileAndTheErrorOfTheKey() {
        var io = new IOProviderMemory();
        var cache = new FileConversionCache(io, _base);
        var id = adjustment(FileFormat.Png);
        await cache.SetFromStreamAsync(id, new MemoryStream([1]));
        var other = adjustment(FileFormat.Png);
        cache.SaveErrorStatus(other, "failed");

        cache.Clear(id);
        Assert.IsFalse(io.Exists(pathOf(id, ".png")));
        Assert.IsFalse(cache.TryGetResultAndStream(id, out _));
        Assert.IsTrue(io.Exists(pathOf(other, ".status")), "another key should be untouched");
    }
    [TestMethod]
    public async Task ClearAllErrorsDeletesTheErrorsAndKeepsTheConvertedFiles() {
        var io = new IOProviderMemory();
        var cache = new FileConversionCache(io, _base);
        var failed = adjustment(FileFormat.Png);
        cache.SaveErrorStatus(failed, "conversion failed");
        var ready = adjustment(FileFormat.Png);
        await cache.SetFromStreamAsync(ready, new MemoryStream([1]));
        Assert.IsTrue(io.Exists(pathOf(failed, ".status")));
        Assert.IsTrue(reopen(io).TryGetStatusNoStream(failed, out var error));
        Assert.AreEqual(FileConversionStatus.Error, error.Status);
        Assert.AreEqual("conversion failed", error.Message);

        cache.ClearAllErrors();
        Assert.IsFalse(io.Exists(pathOf(failed, ".status")), "the error status should be gone");
        Assert.IsFalse(reopen(io).TryGetStatusNoStream(failed, out _));
        Assert.IsTrue(io.Exists(pathOf(ready, ".png")), "a converted file should survive clearing errors");
    }
    [TestMethod]
    public async Task ClearAllKeepsTheTempFolder() {
        // conversions in progress write their output in the temp folder: clearing the cache must
        // not pull the file out from under a running conversion
        var io = new IOProviderMemory();
        var cache = new FileConversionCache(io, _base);
        var id = adjustment(FileFormat.Png);
        await cache.SetFromStreamAsync(id, new MemoryStream([1]));
        var inProgress = new[] { _base, FileConversionCache.TempFolderName, "conversion.tmp" };
        io.WriteAllBytes(inProgress, [9]);

        cache.ClearAll();
        Assert.IsFalse(io.Exists(pathOf(id, ".png")), "the cached file should be gone");
        Assert.IsTrue(io.Exists(inProgress), "the temp folder should be left alone");
    }
}
