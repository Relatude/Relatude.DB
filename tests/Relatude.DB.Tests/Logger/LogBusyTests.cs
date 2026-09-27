using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.Logging.Statistics;

namespace Relatude.Logger;
/// <summary>
/// A log that is busy - a page or a search being read, statistics being saved - does not hold up
/// those recording to it: what they record is left in the log's backlog, and whoever holds the log
/// next records it before doing anything else.
/// </summary>
[TestClass]
public class LogBusyTests {
    static readonly TimeSpan _long = TimeSpan.FromSeconds(20); // for what has to happen
    static readonly TimeSpan _short = TimeSpan.FromMilliseconds(300); // for what must not happen

    // A log with three entries on disk and a page of it being read, held up at the gate: the page
    // holds the log until the gate opens.
    static (GatedIOProvider Io, LogStore Store, Task<List<LogEntry>> Page) busy(IIOProvider? inner = null) {
        var io = new GatedIOProvider(inner ?? new IOProviderMemory(), FileKeyUtility.Logger_NamePrefix("test", FileInterval.Day));
        var store = H.Store(io, H.RichSettings());
        for (var i = 0; i < 3; i++) store.Record("test", H.Entry(H.T0.AddMinutes(i), ("pInt", 1)));
        store.GetLogFileSize("test"); // writes the buffer out, so the page has a file to read
        io.Close();
        var page = Task.Run(() => store.ExtractLog("test", H.T0, H.T0.AddDays(1), 0, 100, true, out _).ToList());
        Assert.IsTrue(io.ReadWaiting.Wait(_long), "the page never came to its file");
        return (io, store, page);
    }
    static int total(LogStore store) {
        store.ExtractLog("test", H.T0, H.T0.AddDays(1), 0, 1, true, out var total);
        return total;
    }
    static DateTime day => H.T0.Date;

    [TestMethod]
    public void RecordDoesNotWaitForAPageBeingRead() {
        var (io, store, page) = busy();
        try {
            var recording = Task.Run(() => {
                for (var i = 0; i < 100; i++) store.Record("test", H.Entry(H.T0.AddHours(1).AddSeconds(i), ("pInt", 2)));
            });
            Assert.IsTrue(recording.Wait(_long), "recording waited for the page being read");
            Assert.IsFalse(page.IsCompleted, "the page was being read all the while");
        } finally {
            io.Open();
        }
        Assert.IsTrue(page.Wait(_long));
        Assert.AreEqual(3, page.Result.Count, "the page has what the log held when it was asked for");
        store.Dispose();
    }

    [TestMethod]
    public void WhatWasRecordedWhileBusyIsInTheNextPageAndInTheStatistics() {
        var (io, store, page) = busy();
        try {
            var recording = Task.Run(() => {
                for (var i = 0; i < 100; i++) store.Record("test", H.Entry(H.T0.AddHours(1).AddSeconds(i), ("pInt", 2), ("pGroup", "later")));
            });
            Assert.IsTrue(recording.Wait(_long));
        } finally {
            io.Open();
        }
        Assert.IsTrue(page.Wait(_long));
        Assert.AreEqual(103, total(store));
        Assert.AreEqual(103, store.AnalyseCombinedRows("test", IntervalType.Day, day, day.AddDays(1)).Value);
        Assert.AreEqual(3 + 200, store.AnalyseCombinedIntegerSums("test", "pInt", IntervalType.Day, day, day.AddDays(1)).Value);
        Assert.AreEqual(100, store.AnalyseCombinedGroupCounts("test", "pGroup", IntervalType.Day, day, day.AddDays(1)).Value["later"]);
        store.Dispose();
    }

    [TestMethod]
    public void AnEntryThatMustBeOnDiskWaitsForTheLog() {
        var (io, store, page) = busy();
        Task durable;
        try {
            durable = Task.Run(() => store.Record("test", H.Entry(H.T0.AddHours(2), ("pInt", 5)), flushToDisk: true));
            Assert.IsFalse(durable.Wait(_short), "an entry that has to be on disk when Record returns cannot be left for later");
        } finally {
            io.Open();
        }
        Assert.IsTrue(durable.Wait(_long));
        Assert.IsTrue(page.Wait(_long));
        Assert.AreEqual(4, total(store));
        store.Dispose();
    }

    [TestMethod]
    public void AFullBacklogMakesTheNextCallerWait() {
        var (io, store, page) = busy();
        Task overflow;
        try {
            var filling = Task.Run(() => {
                for (var i = 0; i < Log.MaxBacklog; i++) store.Record("test", H.Entry(H.T0.AddHours(3), ("pInt", 1)));
            });
            Assert.IsTrue(filling.Wait(_long), "the backlog takes entries up to its size without anyone waiting");
            overflow = Task.Run(() => store.Record("test", H.Entry(H.T0.AddHours(3), ("pInt", 1))));
            Assert.IsFalse(overflow.Wait(_short), "one more than the backlog holds waits for the log");
        } finally {
            io.Open();
        }
        Assert.IsTrue(overflow.Wait(_long));
        Assert.IsTrue(page.Wait(_long));
        Assert.AreEqual(3 + Log.MaxBacklog + 1, total(store));
        Assert.AreEqual(3 + Log.MaxBacklog + 1, store.AnalyseCombinedRows("test", IntervalType.Day, day, day.AddDays(1)).Value);
        store.Dispose();
    }

    [TestMethod]
    public void DisposingKeepsWhatWasLeftInTheBacklog() {
        var disk = new IOProviderMemory();
        var (io, store, page) = busy(disk);
        Task disposing;
        try {
            for (var i = 0; i < 10; i++) store.Record("test", H.Entry(H.T0.AddHours(4).AddSeconds(i), ("pInt", 1)));
            disposing = Task.Run(store.Dispose); // waits for the page like anyone else holding the log would
        } finally {
            io.Open();
        }
        Assert.IsTrue(page.Wait(_long));
        Assert.IsTrue(disposing.Wait(_long));
        // entries and statistics alike: the memory provider only keeps what a closed stream wrote
        var reopened = H.Store(disk, H.RichSettings());
        Assert.AreEqual(13, total(reopened));
        Assert.AreEqual(13, reopened.AnalyseCombinedRows("test", IntervalType.Day, day, day.AddDays(1)).Value);
        reopened.Dispose();
    }

    [TestMethod]
    public void NoEntryIsLostWithReadersAndWritersAtOnce() {
        var store = H.Store(new IOProviderMemory(), H.RichSettings());
        const int writers = 8;
        const int each = 4000;
        var finished = 0;
        var reading = Task.Run(() => {
            // everything that holds the log for a while, over and over for as long as there is recording
            for (var round = 0; round < 3 || Volatile.Read(ref finished) < writers; round++) {
                store.ExtractLog("test", H.T0, H.T0.AddDays(1), 0, 100, true, out _);
                store.SearchLog("test", "w3", H.T0, H.T0.AddDays(1), 0, 10, true, out _);
                store.AnalyseRows("test", IntervalType.Minute, H.T0, H.T0.AddHours(1), false, true);
                store.SaveStatistics();
                store.GetLogFileSize("test");
            }
        });
        var writing = Enumerable.Range(0, writers).Select(w => Task.Run(() => {
            for (var i = 0; i < each; i++) {
                store.Record("test", H.Entry(H.T0.AddMilliseconds(i), ("pInt", 1), ("pGroup", "w" + w), ("pUnique", w + "-" + i)));
            }
            Interlocked.Increment(ref finished);
        })).ToArray();
        Assert.IsTrue(Task.WaitAll(writing, TimeSpan.FromSeconds(60)));
        Assert.IsTrue(reading.Wait(TimeSpan.FromSeconds(60)));

        var entries = store.ExtractLog("test", H.T0, H.T0.AddDays(1), 0, int.MaxValue, false, out var count).ToList();
        Assert.AreEqual(writers * each, count);
        Assert.AreEqual(writers * each, entries.Select(e => (string)e.Values["pUnique"]).Distinct().Count(), "every entry, and each of them once");
        Assert.AreEqual(writers * each, store.AnalyseCombinedRows("test", IntervalType.Day, day, day.AddDays(1)).Value);
        Assert.AreEqual(writers * each, store.AnalyseCombinedIntegerSums("test", "pInt", IntervalType.Day, day, day.AddDays(1)).Value);
        var groups = store.AnalyseCombinedGroupCounts("test", "pGroup", IntervalType.Day, day, day.AddDays(1)).Value;
        for (var w = 0; w < writers; w++) Assert.AreEqual(each, groups["w" + w]);
        store.Dispose();
    }
}
