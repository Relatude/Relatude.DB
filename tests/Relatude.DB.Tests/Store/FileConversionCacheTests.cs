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
        using var output = result.Output; // a large file is a stream on the file, holding it open
        var buffer = new MemoryStream();
        await output.CopyToAsync(buffer);
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
    static byte[] payload(int size, int seed) {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }
    static string tempDir() {
        var dir = Path.Combine(Path.GetTempPath(), "relatude-conv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
    static string[] tempFolderFiles(string root) {
        var temp = Path.Combine(root, _base, FileConversionCache.TempFolderName);
        return Directory.Exists(temp) ? Directory.GetFiles(temp, "*", SearchOption.AllDirectories) : [];
    }

    [TestMethod]
    public async Task WritingTheSameConversionTwiceKeepsOneWholeCopy() {
        // a second run of a conversion (a request missing the cache just before the first one finished,
        // another process sharing the folder) used to append its output to the first copy on disk
        var root = tempDir();
        try {
            var io = new IOProviderDisk(root);
            var cache = new FileConversionCache(io, _base);
            var id = adjustment(FileFormat.Webp);
            var data = payload(300_000, 1); // above the in memory limit, so read back from disk
            await cache.SetFromStreamAsync(id, new MemoryStream(data));
            await cache.SetFromStreamAsync(id, new MemoryStream(payload(300_000, 2)));
            CollectionAssert.AreEqual(data, await read(reopen(io), id), "the first copy should stay, whole and alone");
            Assert.AreEqual(0, tempFolderFiles(root).Length, "nothing should be left in the temp folder");
        } finally {
            Directory.Delete(root, true);
        }
    }
    [TestMethod]
    public async Task AConversionIsWrittenAsideAndMovedIntoPlace() {
        var root = tempDir();
        try {
            var io = new IOProviderDisk(root);
            var cache = new FileConversionCache(io, _base);
            var id = adjustment(FileFormat.Png);
            var input = new SlowStream(payload(50_000, 3));
            var write = Task.Run(() => cache.SetFromStreamAsync(id, input)); // the write blocks halfway on this thread
            await input.HalfRead.Task;
            Assert.IsFalse(io.Exists(pathOf(id, ".png")), "nothing should be on the key before the file is complete");
            Assert.IsFalse(reopen(io).TryGetStatusNoStream(id, out _));
            input.Continue.SetResult();
            await write;
            Assert.IsTrue(reopen(io).TryGetStatusNoStream(id, out var status));
            Assert.AreEqual(FileConversionStatus.Ready, status.Status);
        } finally {
            Directory.Delete(root, true);
        }
    }
    /// <summary>A stream that stops halfway until it is let go on.</summary>
    sealed class SlowStream(byte[] data) : MemoryStream(data) {
        public TaskCompletionSource HalfRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override int Read(byte[] buffer, int offset, int count) {
            if (Position > 0 && !Continue.Task.IsCompleted) {
                HalfRead.TrySetResult();
                Continue.Task.Wait();
            }
            return base.Read(buffer, offset, Math.Min(count, (int)Length / 2));
        }
    }
    [TestMethod]
    public async Task AFileMovedInIsKeptAndASecondOneDiscarded() {
        var root = tempDir();
        try {
            var io = new IOProviderDisk(root);
            var cache = new FileConversionCache(io, _base);
            var id = adjustment(FileFormat.Mp4);
            var first = Path.Combine(root, "first.mp4");
            var second = Path.Combine(root, "second.mp4");
            File.WriteAllBytes(first, payload(300_000, 4));
            File.WriteAllBytes(second, payload(300_000, 5));
            await cache.SetFromFileAsync(id, first);
            await cache.SetFromFileAsync(id, second);
            CollectionAssert.AreEqual(payload(300_000, 4), await read(reopen(io), id));
            Assert.IsFalse(File.Exists(first), "moved onto its key");
            Assert.IsFalse(File.Exists(second), "the converter's output is deleted when a copy is already cached");
        } finally {
            Directory.Delete(root, true);
        }
    }
    [TestMethod]
    public async Task AnEmptyLeftoverIsNotAConversion() {
        // what a write cut short by a crash left on the key, before writes were moved into place whole
        var io = new IOProviderMemory();
        var id = adjustment(FileFormat.Png);
        io.WriteAllBytes(pathOf(id, ".png"), []);
        var cache = new FileConversionCache(io, _base);
        Assert.IsFalse(cache.TryGetStatusNoStream(id, out _));
        Assert.IsFalse(cache.TryGetResultAndStream(id, out _));
        await cache.SetFromStreamAsync(id, new MemoryStream([1, 2, 3]));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await read(reopen(io), id));
    }
    [TestMethod]
    public async Task ATemporaryResultNeverKeepsAPersistentOneOffTheDisk() {
        var io = new IOProviderMemory();
        var cache = new FileConversionCache(io, _base);
        var fileId = Guid.NewGuid();
        var temporary = new FileIdWithAdjustment(fileId, new FileAdjustmentImage { RequestedFormat = FileFormat.Png, Width = 100, Temporary = true }, new PropertyPath(1, Guid.Empty));
        var persistent = new FileIdWithAdjustment(fileId, new FileAdjustmentImage { RequestedFormat = FileFormat.Png, Width = 100 }, new PropertyPath(1, Guid.Empty));
        await cache.SetFromStreamAsync(temporary, new MemoryStream([1]));
        Assert.IsFalse(io.Exists(pathOf(temporary, ".png")), "a small temporary result stays in memory");
        Assert.IsFalse(cache.TryGetStatusNoStream(persistent, out _), "and does not answer for the persistent request");
        await cache.SetFromStreamAsync(persistent, new MemoryStream([1]));
        Assert.IsTrue(io.Exists(pathOf(persistent, ".png")));
    }
    [TestMethod]
    public async Task CachedBytesCannotBeWrittenThrough() {
        var cache = new FileConversionCache(new IOProviderMemory(), _base);
        var id = adjustment(FileFormat.Png);
        await cache.SetFromStreamAsync(id, new MemoryStream([1, 2, 3]));
        Assert.IsTrue(cache.TryGetResultAndStream(id, out var result));
        Assert.IsFalse(result.Output!.CanWrite, "the array is the one in the memory cache, shared by every request");
    }
    [TestMethod]
    public async Task AnErrorReplacesTheResultInMemoryToo() {
        var cache = new FileConversionCache(new IOProviderMemory(), _base);
        var id = adjustment(FileFormat.Png);
        await cache.SetFromStreamAsync(id, new MemoryStream([1]));
        cache.SaveErrorStatus(id, "failed");
        Assert.IsTrue(cache.TryGetResultAndStream(id, out var result));
        Assert.AreEqual(FileConversionStatus.Error, result.ProgressInfo.Status);
    }
    [TestMethod]
    public void AnErrorIsTriedAgainAfterItsTimeUnlessPermanent() {
        var io = new IOProviderMemory();
        var expiresAtOnce = new FileConversionCache(io, _base, errorRetryAfter: TimeSpan.Zero);
        var failed = adjustment(FileFormat.Png);
        var canceled = adjustment(FileFormat.Png);
        expiresAtOnce.SaveErrorStatus(failed, "Source file missing");
        expiresAtOnce.SaveErrorStatus(canceled, "Canceled permanently. ", permanent: true);
        Assert.IsFalse(expiresAtOnce.TryGetStatusNoStream(failed, out _), "an expired error lets the next request convert again");
        Assert.IsTrue(expiresAtOnce.TryGetStatusNoStream(canceled, out var status));
        Assert.AreEqual("Canceled permanently. ", status.Message);
        Assert.IsTrue(reopen(io).TryGetStatusNoStream(failed, out var recent), "within the default time it is still an error");
        Assert.AreEqual("Source file missing", recent.Message);
    }
    [TestMethod]
    public void AnErrorSavedByAnOlderVersionIsTriedAgainUnlessPermanent() {
        // saved as the bare message, without the time it was saved at
        var io = new IOProviderMemory();
        var failed = adjustment(FileFormat.Png);
        var canceled = adjustment(FileFormat.Png);
        io.WriteString(pathOf(failed, ".status"), "Conversion failed");
        io.WriteString(pathOf(canceled, ".status"), "Canceled permanently. ");
        var cache = new FileConversionCache(io, _base);
        Assert.IsFalse(cache.TryGetStatusNoStream(failed, out _));
        Assert.IsTrue(cache.TryGetStatusNoStream(canceled, out var status));
        Assert.AreEqual(FileConversionStatus.Error, status.Status);
    }
    [TestMethod]
    public async Task ASuccessfulRetryRemovesTheError() {
        var io = new IOProviderMemory();
        var cache = new FileConversionCache(io, _base, errorRetryAfter: TimeSpan.Zero);
        var id = adjustment(FileFormat.Png);
        cache.SaveErrorStatus(id, "failed");
        await cache.SetFromStreamAsync(id, new MemoryStream([1]));
        Assert.IsFalse(io.Exists(pathOf(id, ".status")));
    }
    [TestMethod]
    public void TheGenerationOutlivesARestartAndChangesWhenTheCacheIsCleared() {
        var io = new IOProviderMemory();
        var generation = new FileConversionCache(io, _base).Generation;
        Assert.AreNotEqual(Guid.Empty, generation);
        Assert.AreEqual(generation, reopen(io).Generation, "file URLs are versioned by it: a restart must not change them");
        var cache = reopen(io);
        cache.ClearAll();
        Assert.AreNotEqual(generation, cache.Generation, "cleared, the files may be converted differently, under new URLs");
        Assert.AreEqual(cache.Generation, reopen(io).Generation);
    }
    [TestMethod]
    public void OnlyStaleTempFilesAreDeleted() {
        var io = new IOProviderMemory();
        var cache = new FileConversionCache(io, _base);
        var leftover = new[] { _base, FileConversionCache.TempFolderName, "leftover.part" };
        io.WriteAllBytes(leftover, [1]);
        Assert.AreEqual((0, 1), cache.DeleteStaleTempFiles(TimeSpan.FromHours(1)), "a recent file may still be written by a running conversion");
        Assert.IsTrue(io.Exists(leftover));
        Assert.AreEqual((1, 0), cache.DeleteStaleTempFiles(TimeSpan.Zero));
        Assert.IsFalse(io.Exists(leftover));
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
