using Microsoft.Win32.SafeHandles;
using Relatude.DB.IO;

namespace Relatude.Persistence;

/// <summary>
/// Which flushes of the disk append stream reach the file. A flush without deepFlush only hands the bytes
/// to the OS, so it must never count as durable: a deep flush after it still has to reach the disk. That
/// is what the log relies on, as transactions flush with Settings.DeepFlushDisk (off by default) and the
/// scheduler's flush after them is a deep one.
/// </summary>
[TestClass]
public class StoreStreamFlushTests {
    string _folder = null!;

    [TestInitialize]
    public void Init() => _folder = Path.Combine(Path.GetTempPath(), "relatude-streamflush-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup() {
        try { Directory.Delete(_folder, true); } catch { }
    }

    // records the flushToDisk of every flush that reaches the file stream, failed ones included
    sealed class RecordingFileStream(SafeFileHandle handle, FileAccess access) : FileStream(handle, access, 4096 * 10, handle.IsAsync) {
        public readonly List<bool> Flushes = [];
        public bool FailNextFlush;
        public override void Flush(bool flushToDisk) {
            Flushes.Add(flushToDisk);
            if (FailNextFlush) {
                FailNextFlush = false;
                throw new IOException("Simulated failure, as on a full disk.");
            }
            base.Flush(flushToDisk);
        }
    }

    StoreStreamDiscWrite open(out RecordingFileStream file) {
        RecordingFileStream? created = null;
        var stream = new StoreStreamDiscWrite("log.bin", Path.Combine(_folder, "log.bin"), false, () => { }, (h, access) => created = new RecordingFileStream(h, access));
        file = created!;
        return stream;
    }

    static byte[] bytes(int count) => Enumerable.Range(0, count).Select(i => (byte)i).ToArray();

    [TestMethod]
    public void ADeepFlushAfterAPlainOneStillReachesTheDisk() {
        using var stream = open(out var file);
        stream.Append(bytes(100));
        stream.Flush(false);
        stream.Flush(true);
        CollectionAssert.AreEqual(new[] { false, true }, file.Flushes, "the plain flush was taken as durable, so the deep flush never reached the disk");
    }

    [TestMethod]
    public void AFlushWithNothingNewIsSkippedAndADeepOneCoversAPlainOne() {
        using var stream = open(out var file);
        stream.Append(bytes(100));
        stream.Flush(true);
        stream.Flush(true);
        stream.Flush(false); // in the OS already, the deep flush put it there
        CollectionAssert.AreEqual(new[] { true }, file.Flushes);
        stream.Append(bytes(100));
        stream.Flush(false);
        stream.Flush(false);
        CollectionAssert.AreEqual(new[] { true, false }, file.Flushes);
        stream.Flush(true);
        stream.Flush(true);
        CollectionAssert.AreEqual(new[] { true, false, true }, file.Flushes);
    }

    [TestMethod]
    public void AReadThatPushedTheBufferDownDoesNotCountAsDurable() {
        using var stream = open(out var file);
        stream.Append(bytes(100));
        stream.Flush(true);
        stream.Append(bytes(100)); // in the stream's write buffer
        var buffer = new byte[100];
        stream.Get(100, 100, buffer); // pushes the buffer to the OS first, without fsync
        CollectionAssert.AreEqual(bytes(100), buffer);
        stream.Flush(false); // nothing left for a plain flush to do
        stream.Flush(true);
        CollectionAssert.AreEqual(new[] { true, false, true }, file.Flushes);
    }

    [TestMethod]
    public void AFailedFlushIsNotCountedAsFlushed() {
        using var stream = open(out var file);
        stream.Append(bytes(100));
        file.FailNextFlush = true;
        Assert.ThrowsExactly<IOException>(() => stream.Flush(true));
        stream.Flush(true); // tried again, not skipped
        CollectionAssert.AreEqual(new[] { true, true }, file.Flushes);
        stream.Append(bytes(100));
        file.FailNextFlush = true;
        Assert.ThrowsExactly<IOException>(() => stream.Flush(false));
        stream.Flush(false);
        stream.Flush(true);
        CollectionAssert.AreEqual(new[] { true, true, false, false, true }, file.Flushes);
    }

    [TestMethod]
    public async Task BytesAppendedWithoutTheLockAreFlushedToo() {
        var stream = open(out var file);
        stream.Append(bytes(100));
        stream.Flush(true);
        await stream.AppendAsyncNoChecksumOrLock(bytes(50), 50);
        stream.Flush(true);
        CollectionAssert.AreEqual(new[] { true, true }, file.Flushes, "bytes appended without the lock were left out of the deep flush");
        stream.Dispose();
        Assert.AreEqual(150, new FileInfo(Path.Combine(_folder, "log.bin")).Length);
    }
}
