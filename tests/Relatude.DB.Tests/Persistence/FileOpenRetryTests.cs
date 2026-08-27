using Relatude.DB.Common;
using System.Diagnostics;

namespace Relatude.Persistence;

/// <summary>
/// A host that starts the new process before the old one has finished stopping - Azure App Service
/// recycles that way by default - hands the new process a database whose files are still held. The
/// lock always clears within seconds, so the only wrong answer is to give up on the first attempt.
/// Everything else must still fail immediately: waiting cannot fix a missing file or a bad header,
/// and a swallowed error is worse than a slow one.
/// </summary>
[TestClass]
public class FileOpenRetryTests {

    string _root = string.Empty;
    string _file = string.Empty;

    [TestInitialize]
    public void CreateRoot() {
        _root = Path.Combine(Path.GetTempPath(), "relatude.lock." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _file = Path.Combine(_root, "db.wal");
        File.WriteAllBytes(_file, new byte[64]);
    }

    [TestCleanup]
    public void DeleteRoot() {
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>The exclusive handle the database itself takes on its log and page files.</summary>
    FileStream lockExclusively() => new(_file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    FileStream openExclusively() => new(_file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    [TestMethod]
    public void IsSharingViolation_RecognisesARealLock() {
        using var holder = lockExclusively();
        var err = Assert.ThrowsException<IOException>(() => openExclusively());
        Assert.IsTrue(FileOpenRetry.IsSharingViolation(err),
            "the exception a genuinely locked file throws must be recognised, or none of the retries fire: " + err.Message);
    }

    [TestMethod]
    public void IsSharingViolation_RejectsWhatWaitingCannotFix() {
        Assert.IsFalse(FileOpenRetry.IsSharingViolation(new FileNotFoundException("gone")));
        Assert.IsFalse(FileOpenRetry.IsSharingViolation(new DirectoryNotFoundException("gone")));
        Assert.IsFalse(FileOpenRetry.IsSharingViolation(new InvalidDataException("bad segment header")));
        Assert.IsFalse(FileOpenRetry.IsSharingViolation(new OutOfMemoryException()));
        Assert.IsFalse(FileOpenRetry.IsSharingViolation(null));
    }

    [TestMethod]
    public void IsSharingViolation_LooksInsideWrappedExceptions() {
        // the index engines wrap their open failures before they reach the data store
        var wrapped = new Exception("Failed loading memory index states. ",
            new InvalidOperationException("engine", new FileLockedException("held", null)));
        Assert.IsTrue(FileOpenRetry.IsSharingViolation(wrapped));
        var unrelated = new Exception("outer", new InvalidDataException("corrupt"));
        Assert.IsFalse(FileOpenRetry.IsSharingViolation(unrelated));
    }

    [TestMethod]
    public void Open_SucceedsOnceTheOtherProcessLetsGo() {
        var holder = lockExclusively();
        var released = false;
        var releasing = Task.Run(() => {
            Thread.Sleep(400); // stand in for the old worker finishing its shutdown
            released = true;
            holder.Dispose();
        });
        var logged = new List<string>();
        var sw = Stopwatch.StartNew();
        using var opened = FileOpenRetry.Open(_file, openExclusively, TimeSpan.FromSeconds(20), logged.Add);
        releasing.Wait();
        Assert.IsTrue(released, "it must not have opened before the holder released the file");
        Assert.IsTrue(sw.Elapsed >= TimeSpan.FromMilliseconds(350), "it should have waited, not spun");
        Assert.AreEqual(2, logged.Count, "one line when the wait starts, one when it ends: " + string.Join(" / ", logged));
        StringAssert.Contains(logged[0], "held by another process");
        StringAssert.Contains(logged[1], "released");
    }

    [TestMethod]
    public void Open_GivesUpWithARecognisableExceptionWhenTheLockNeverClears() {
        using var holder = lockExclusively();
        var sw = Stopwatch.StartNew();
        var err = Assert.ThrowsException<FileLockedException>(
            () => FileOpenRetry.Open(_file, openExclusively, TimeSpan.FromMilliseconds(600)));
        Assert.IsTrue(sw.Elapsed >= TimeSpan.FromMilliseconds(500), "it should have used its budget");
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(10), "and then stopped: " + sw.Elapsed);
        // the server's auto-open retry and the state-file guard both key off this
        Assert.IsTrue(FileOpenRetry.IsSharingViolation(err));
        Assert.IsNotNull(err.InnerException, "the original IO error is the useful half of the report");
        StringAssert.Contains(err.Message, "Azure App Service");
    }

    [TestMethod]
    public void Open_DoesNotWaitForAFailureWaitingCannotFix() {
        var missing = Path.Combine(_root, "not-here.wal");
        var sw = Stopwatch.StartNew();
        Assert.ThrowsException<FileNotFoundException>(() => FileOpenRetry.Open(missing,
            () => new FileStream(missing, FileMode.Open, FileAccess.Read, FileShare.Read),
            TimeSpan.FromSeconds(30)));
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(2),
            "a missing file must fail at once, not sit out the whole budget: " + sw.Elapsed);
    }

    [TestMethod]
    public void Open_PassesAnUnrelatedExceptionStraightOut() {
        var sw = Stopwatch.StartNew();
        Assert.ThrowsException<InvalidDataException>(() => FileOpenRetry.Open(_file,
            () => throw new InvalidDataException("bad header"), TimeSpan.FromSeconds(30)));
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(2), "corrupt data is not a lock: " + sw.Elapsed);
    }

    [TestMethod]
    public void Open_ReportsTheWaitEvenWhenTheCallerHasNoLogger() {
        // most callers are deep in the storage engines with no logger in reach; a silent wait would be
        // indistinguishable from a hang, which is exactly what makes an Azure start-up stall baffling
        var original = FileOpenRetry.DefaultLog;
        var logged = new List<string>();
        try {
            FileOpenRetry.DefaultLog = logged.Add;
            using var holder = lockExclusively();
            Assert.ThrowsException<FileLockedException>(
                () => FileOpenRetry.Open(_file, openExclusively, TimeSpan.FromMilliseconds(300)));
        } finally {
            FileOpenRetry.DefaultLog = original;
        }
        Assert.AreEqual(1, logged.Count, "the start of the wait must be reported: " + string.Join(" / ", logged));
        StringAssert.Contains(logged[0], "held by another process");
    }

    [TestMethod]
    public void Open_DoesNotLogWhenThereIsNothingToWaitFor() {
        var logged = new List<string>();
        using var opened = FileOpenRetry.Open(_file, openExclusively, log: logged.Add);
        Assert.AreEqual(0, logged.Count, "the normal case must stay silent");
    }
}
