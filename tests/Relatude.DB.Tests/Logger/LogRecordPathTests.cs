using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.Logging.Statistics;

namespace Relatude.Logger;
/// <summary>
/// What is done for every entry recorded - the file it goes to, how its values are converted, the
/// buffer it is written in, the batch it is flushed with - checked where each of the shortcuts taken
/// there has an edge.
/// </summary>
[TestClass]
public class LogRecordPathTests {
    // midnight on the first of a month, where a minute, an hour, a day and a month all end at once
    static readonly DateTime _boundary = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    static LogSettings numbered(FileInterval interval, Action<LogSettings>? configure = null) => H.Settings(configure: s => {
        s.FileInterval = interval;
        s.EnableLogTextFormat = true;
        s.Properties.Add("n", new LogProperty { DataType = LogDataType.Integer });
        configure?.Invoke(s);
    });

    [TestMethod]
    [DataRow(FileInterval.Minute)]
    [DataRow(FileInterval.Hour)]
    [DataRow(FileInterval.Day)]
    [DataRow(FileInterval.Month)]
    public void EntriesGoToTheFileOfTheirOwnInterval(FileInterval interval) {
        var io = new IOProviderMemory();
        var store = H.Store(io, numbered(interval));
        var before = _boundary.AddTicks(-1);
        // back and forth across the boundary, so nearly every entry goes to another file than the last
        for (var i = 0; i < 10; i++) store.Record("test", H.Entry(i % 2 == 0 ? before : _boundary, ("n", i)));
        store.Dispose(); // the memory provider keeps what a stream wrote once the stream is closed

        var earlier = interval switch {
            FileInterval.Minute => new DateTime(2026, 6, 30, 23, 59, 0, DateTimeKind.Utc),
            FileInterval.Hour => new DateTime(2026, 6, 30, 23, 0, 0, DateTimeKind.Utc),
            FileInterval.Day => new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            _ => new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        CollectionAssert.AreEqual(new[] { earlier, _boundary }, FileKeyUtility.Logger_FileDatesBin(io, "test", interval));
        CollectionAssert.AreEqual(new[] { earlier, _boundary }, FileKeyUtility.Logger_FileDatesTxt(io, "test", interval));

        var reopened = H.Store(io, numbered(interval));
        int[] recorded(DateTime from, DateTime to) => [.. reopened.ExtractLog("test", from, to, 0, 100, false, out _).Select(e => (int)e.Values["n"]).Order()];
        CollectionAssert.AreEqual(new[] { 0, 2, 4, 6, 8 }, recorded(_boundary.AddDays(-40), _boundary));
        CollectionAssert.AreEqual(new[] { 1, 3, 5, 7, 9 }, recorded(_boundary, _boundary.AddDays(40)));
        // the text files likewise, a line per entry with the number in its last column
        int[] lines(DateTime fileDate) => [.. io.ReadAllTextUTF8(FileKeyUtility.Logger_FileNameTxt("test", interval, fileDate))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => int.Parse(line.TrimEnd('\r').Split('\t')[^1]))
            .Order()];
        CollectionAssert.AreEqual(new[] { 0, 2, 4, 6, 8 }, lines(earlier));
        CollectionAssert.AreEqual(new[] { 1, 3, 5, 7, 9 }, lines(_boundary));
        reopened.Dispose();
    }

    [TestMethod]
    public void AnEntryAtTheLastMomentThereIsCanBeRecorded() {
        // The last interval there is has no next one to end at. Statistics are off: they cannot
        // count in that interval either, which is a limit of their own and not of the files.
        var store = H.Store(new IOProviderMemory(), numbered(FileInterval.Day, s => s.EnableStatistics = false));
        store.Record("test", H.Entry(DateTime.MaxValue, ("n", 1)));
        store.Record("test", H.Entry(DateTime.MaxValue, ("n", 2)));
        store.Record("test", H.Entry(H.T0, ("n", 3)));
        var entry = store.ExtractLog("test", H.T0, H.T0.AddDays(1), 0, 10, false, out var total).Single();
        Assert.AreEqual(1, total);
        Assert.AreEqual(3, entry.Values["n"]);
        store.Dispose();
    }

    [TestMethod]
    public void EntriesOfEverySizeReadBackAsTheyWereRecorded() {
        // Records are written in a buffer each thread keeps: one grown by a large entry is kept for
        // the next only up to a size, so both the reuse and the letting go are passed through here.
        var store = H.Store(new IOProviderMemory(), H.Settings(configure: s => {
            s.Properties.Add("n", new LogProperty { DataType = LogDataType.Integer });
            s.Properties.Add("text", new LogProperty { DataType = LogDataType.String });
            s.Properties.Add("blob", new LogProperty { DataType = LogDataType.Bytes });
        }));
        int[] sizes = [5, 10_000, 3, 1_000_000, 7, 70_000, 1];
        for (var i = 0; i < sizes.Length; i++) {
            var entry = H.Entry(H.T0.AddSeconds(i), ("n", i), ("text", new string((char)('a' + i), sizes[i])));
            if (i == 3) entry.Values["blob"] = Enumerable.Range(0, 2_000_000).Select(b => (byte)b).ToArray();
            store.Record("test", entry);
        }
        var entries = store.ExtractLog("test", H.T0, H.T0.AddDays(1), 0, 100, false, out _).ToList();
        Assert.AreEqual(sizes.Length, entries.Count);
        for (var i = 0; i < sizes.Length; i++) {
            Assert.AreEqual(i, entries[i].Values["n"]);
            Assert.AreEqual(new string((char)('a' + i), sizes[i]), entries[i].Values["text"], "entry " + i);
            Assert.AreEqual(i == 3, entries[i].Values.ContainsKey("blob"), "entry " + i + " has nothing of another's");
        }
        CollectionAssert.AreEqual(Enumerable.Range(0, 2_000_000).Select(b => (byte)b).ToArray(), (byte[])entries[3].Values["blob"]);
        store.Dispose();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EntriesWrittenInManyBatchesReadBackAsTheyWereRecorded(bool compressed) {
        var io = new IOProviderMemory();
        LogSettings settings() => H.Settings(configure: s => {
            s.Compressed = compressed;
            s.Properties.Add("n", new LogProperty { DataType = LogDataType.Integer, Statistics = [new(StatisticsType.Sum)] });
            s.Properties.Add("text", new LogProperty { DataType = LogDataType.String });
        });
        var store = H.Store(io, settings());
        // some 4 MB over two days: the buffer is written out a megabyte at a time, one batch per file
        const int count = 30_000;
        var padding = new string('x', 100);
        for (var i = 0; i < count; i++) store.Record("test", H.Entry(H.T0.AddSeconds(i * 4), ("n", i), ("text", padding + i)));
        store.Dispose();

        var reopened = H.Store(io, settings());
        var entries = reopened.ExtractLog("test", H.T0, H.T0.AddDays(3), 0, int.MaxValue, false, out var total).ToList();
        Assert.AreEqual(count, total);
        CollectionAssert.AreEqual(Enumerable.Range(0, count).ToArray(), entries.Select(e => (int)e.Values["n"]).ToArray());
        CollectionAssert.AreEqual(Enumerable.Range(0, count).Select(i => padding + i).ToArray(), entries.Select(e => (string)e.Values["text"]).ToArray());
        CollectionAssert.AreEqual(Enumerable.Range(0, count).Select(i => H.T0.AddSeconds(i * 4)).ToArray(), entries.Select(e => e.Timestamp).ToArray());
        Assert.AreEqual(count * (count - 1) / 2, reopened.AnalyseCombinedIntegerSums("test", "n", IntervalType.Day, H.T0.Date, H.T0.Date.AddDays(3)).Value);
        reopened.Dispose();
    }

    [TestMethod]
    public void AValueThatIsAlreadyTheDeclaredTypeIsKeptAsItIs() {
        (object Value, LogDataType Type)[] values = [
            (42, LogDataType.Integer),
            (1.5, LogDataType.Double),
            ("text", LogDataType.String),
            (TimeSpan.FromSeconds(3), LogDataType.TimeSpan),
            (new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), LogDataType.DateTime),
            (new byte[] { 1, 2 }, LogDataType.Bytes),
        ];
        foreach (var (value, type) in values) {
            Assert.IsTrue(LogValues.TryConvert(value, type, out var converted));
            Assert.AreSame(value, converted, type + " is handed back as it came, not boxed again");
        }
    }

    [TestMethod]
    public void AValueThatIsNotTheDeclaredTypeYetIsStillConverted() {
        // not a number is not a measurement, however it arrives
        Assert.IsFalse(LogValues.TryConvert(double.NaN, LogDataType.Double, out _));
        Assert.IsFalse(LogValues.TryConvert(double.PositiveInfinity, LogDataType.Double, out _));
        // a moment on another clock is moved to UTC, and one on no clock is taken to be in UTC already
        var local = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local);
        Assert.IsTrue(LogValues.TryConvert(local, LogDataType.DateTime, out var moved));
        Assert.AreEqual(DateTimeKind.Utc, ((DateTime)moved).Kind);
        Assert.AreEqual(local.ToUniversalTime(), (DateTime)moved);
        var unspecified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);
        Assert.IsTrue(LogValues.TryConvert(unspecified, LogDataType.DateTime, out var taken));
        Assert.AreEqual(DateTimeKind.Utc, ((DateTime)taken).Kind);
        Assert.AreEqual(unspecified.Ticks, ((DateTime)taken).Ticks);
        // and a type next to the declared one becomes it
        Assert.IsTrue(LogValues.TryConvert(7L, LogDataType.Integer, out var integer));
        Assert.AreEqual(7, integer);
        Assert.IsTrue(LogValues.TryConvert(3, LogDataType.Double, out var number));
        Assert.AreEqual(3.0, number);
    }
}
