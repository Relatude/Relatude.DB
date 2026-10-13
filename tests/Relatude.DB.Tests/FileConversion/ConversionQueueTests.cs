using Relatude.DB.Common;
using Relatude.DB.FileConversion;

namespace Relatude.FileConversion;

/// <summary>
/// The order queued conversions start in: the order they were queued, except that one queued
/// firstInQueue - a web request waiting for it - starts before every conversion waiting.
/// </summary>
[TestClass]
public class ConversionQueueTests {
    static ProgressEntry entry(string name, FileConversionProgressInfo? progress = null) {
        var id = new FileIdWithAdjustment(Guid.NewGuid(), new FileAdjustmentImage { Width = 100 }, new PropertyPath(1, Guid.Empty));
        var info = new FileConversionInfo(id, name, "hash", FileFormat.Png);
        return new ProgressEntry(DateTime.UtcNow, progress ?? new FileConversionProgressInfo(), info, new InputFileSource(() => Task.FromResult<Stream>(new MemoryStream()), null), null, null);
    }
    static Guid key(ProgressEntry e) => e.FileInfo.IdWithAdjustment.GetKey();
    static void queue(RunningConversions conversions, ProgressEntry e, bool firstInQueue = false) => conversions.AddIfMissing(key(e), () => e, firstInQueue);
    static string[] startAll(RunningConversions conversions, Func<ProgressEntry, bool>? tryReserveWork = null) {
        var started = new List<string>();
        while (conversions.TryGetWorkIfNotAlreadyWorkingOnEntryOrConverterTooBusy(out var e, tryReserveWork ?? (_ => true))) started.Add(e.FileInfo.FileName);
        return [.. started];
    }

    [TestMethod]
    public void ConversionsStartInTheOrderTheyWereQueued() {
        var conversions = new RunningConversions();
        foreach (var name in new[] { "a", "b", "c" }) queue(conversions, entry(name));
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, startAll(conversions));
    }

    [TestMethod]
    public void FirstInQueueStartsBeforeEveryConversionWaiting() {
        var conversions = new RunningConversions();
        foreach (var name in new[] { "a", "b", "c" }) queue(conversions, entry(name));
        queue(conversions, entry("web"), firstInQueue: true);
        queue(conversions, entry("d"));
        CollectionAssert.AreEqual(new[] { "web", "a", "b", "c", "d" }, startAll(conversions));
    }

    [TestMethod]
    public void AWaitingConversionAskedForFirstInQueueMovesThere() {
        var conversions = new RunningConversions();
        var c = entry("c");
        foreach (var e in new[] { entry("a"), entry("b"), c }) queue(conversions, e);
        conversions.AddIfMissing(key(c), () => throw new AssertFailedException("queued twice"), firstInQueue: true); // asked for again
        Assert.AreEqual(3, conversions.Count);
        CollectionAssert.AreEqual(new[] { "c", "a", "b" }, startAll(conversions));
        conversions.MoveFirst(Guid.NewGuid()); // one that is not queued is no error
    }

    [TestMethod]
    public void AskedForAgainWithoutFirstInQueueItKeepsItsPlace() {
        var conversions = new RunningConversions();
        var c = entry("c");
        foreach (var e in new[] { entry("a"), entry("b"), c }) queue(conversions, e);
        conversions.AddIfMissing(key(c), () => throw new AssertFailedException("queued twice"));
        queue(conversions, entry("d"));
        CollectionAssert.AreEqual(new[] { "a", "b", "c", "d" }, startAll(conversions));
    }

    [TestMethod]
    public void QueueOrderSurvivesRemovals() {
        // the dictionary behind the queue reuses the slot of a removed key, so walking its keys - as
        // the search for the next conversion used to - started the last queued first
        var conversions = new RunningConversions();
        var a = entry("a");
        foreach (var e in new[] { a, entry("b"), entry("c") }) queue(conversions, e);
        conversions.Remove(a, ConversionStatus.Completed, null);
        queue(conversions, entry("d"));
        CollectionAssert.AreEqual(new[] { "b", "c", "d" }, startAll(conversions));
    }

    [TestMethod]
    public void AnUpdateIsStoredAndKeepsItsPlace() {
        var conversions = new RunningConversions();
        var b = entry("b");
        foreach (var e in new[] { entry("a"), b, entry("c") }) queue(conversions, e);
        var progress = new FileConversionProgressInfo(FileConversionStatus.InProgress, 0, -1, "Downloading FFmpeg...");
        conversions.UpdateIfExists(new ProgressEntry(b.Created, progress, b.FileInfo, b.InputSource, null, null));
        Assert.IsTrue(conversions.TryGet(key(b), out var stored));
        Assert.AreSame(progress, stored.ProgressInfo, "the update used to be dropped");
        Assert.AreEqual(3, conversions.Count);
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, startAll(conversions));
    }

    [TestMethod]
    public void AnUpdateOfAConversionNoLongerQueuedIsNotAdded() {
        var conversions = new RunningConversions();
        conversions.UpdateIfExists(entry("gone"));
        Assert.AreEqual(0, conversions.Count);
    }

    [TestMethod]
    public void ABusyConverterIsPassedOver() {
        var conversions = new RunningConversions();
        var a = entry("a");
        foreach (var e in new[] { a, entry("b"), entry("c") }) queue(conversions, e);
        CollectionAssert.AreEqual(new[] { "b", "c" }, startAll(conversions, e => e != a));
        CollectionAssert.AreEqual(new[] { "a" }, startAll(conversions));
    }

    [TestMethod]
    public void AConversionRemovedWhileReservingDoesNotEndTheSearch() {
        // the engine removes a conversion no converter takes while it is being asked about it
        var conversions = new RunningConversions();
        var a = entry("a");
        foreach (var e in new[] { a, entry("b") }) queue(conversions, e);
        bool reserve(ProgressEntry e) {
            if (e != a) return true;
            conversions.Remove(e, ConversionStatus.Failed, "no converter");
            return false;
        }
        CollectionAssert.AreEqual(new[] { "b" }, startAll(conversions, reserve));
        Assert.AreEqual(1, conversions.Count);
    }
}
