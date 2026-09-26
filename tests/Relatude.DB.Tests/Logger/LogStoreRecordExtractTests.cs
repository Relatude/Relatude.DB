using Relatude.DB.IO;
using Relatude.DB.Logging;

namespace Relatude.Logger;
[TestClass]
public class LogStoreRecordExtractTests {
    [TestMethod]
    public void RecordAndExtractRoundTripAllDataTypes() {
        var io = new IOProviderMemory();
        var settings = H.Settings(configure: s => {
            s.Properties.Add("pDt", new LogProperty { DataType = LogDataType.DateTime });
            s.Properties.Add("pTs", new LogProperty { DataType = LogDataType.TimeSpan });
            s.Properties.Add("pStr", new LogProperty { DataType = LogDataType.String });
            s.Properties.Add("pInt", new LogProperty { DataType = LogDataType.Integer });
            s.Properties.Add("pDbl", new LogProperty { DataType = LogDataType.Double });
            s.Properties.Add("pBytes", new LogProperty { DataType = LogDataType.Bytes });
        });
        var store = H.Store(io, settings);
        var dtVal = new DateTime(2025, 12, 24, 18, 30, 0, DateTimeKind.Utc);
        var tsVal = new TimeSpan(1, 2, 3);
        var bytes = new byte[] { 1, 2, 3, 255 };
        store.Record("test", H.Entry(H.T0,
            ("pDt", dtVal), ("pTs", tsVal), ("pStr", "hello æøå"), ("pInt", 42), ("pDbl", 3.14), ("pBytes", bytes)));
        var e = store.ExtractLog("test", H.T0, H.T0.AddDays(1), 0, 10, false, out var total).Single();
        Assert.AreEqual(1, total);
        Assert.AreEqual(H.T0, e.Timestamp);
        Assert.AreEqual(DateTimeKind.Utc, e.Timestamp.Kind);
        Assert.AreEqual(dtVal, e.Values["pDt"]);
        Assert.AreEqual(DateTimeKind.Utc, ((DateTime)e.Values["pDt"]).Kind);
        Assert.AreEqual(tsVal, e.Values["pTs"]);
        Assert.AreEqual("hello æøå", e.Values["pStr"]);
        Assert.AreEqual(42, e.Values["pInt"]);
        Assert.AreEqual(3.14, e.Values["pDbl"]);
        CollectionAssert.AreEqual(bytes, (byte[])e.Values["pBytes"]);
        store.Dispose();
    }
    [TestMethod]
    public void UndeclaredPropertiesAreCoercedToLegalTypes() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.Settings());
        var guid = Guid.NewGuid();
        store.Record("test", H.Entry(H.T0, ("guid", guid), ("num", 7), ("long", 123L)));
        var e = store.ExtractLog("test", H.T0, H.T0.AddDays(1), 0, 10, false, out _).Single();
        Assert.AreEqual(guid.ToString(), e.Values["guid"]); // unknown types become strings
        Assert.AreEqual(7, e.Values["num"]); // int is a native type and survives as is
        Assert.AreEqual("123", e.Values["long"]); // long is not a native log type, stored as string
        store.Dispose();
    }
    [TestMethod]
    public void DeclaredTypeWinsOverRecordedType() {
        var io = new IOProviderMemory();
        var settings = H.Settings(configure: s => {
            s.Properties.Add("pInt", new LogProperty { DataType = LogDataType.Integer });
            s.Properties.Add("pDbl", new LogProperty { DataType = LogDataType.Double });
            s.Properties.Add("pLong", new LogProperty { DataType = LogDataType.Integer });
            s.Properties.Add("pText", new LogProperty { DataType = LogDataType.String });
            s.Properties.Add("pWhen", new LogProperty { DataType = LogDataType.DateTime });
            s.Properties.Add("pSpan", new LogProperty { DataType = LogDataType.TimeSpan });
        });
        var store = H.Store(io, settings);
        var when = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));
        store.Record("test", H.Entry(H.T0, ("pInt", "abc"), ("pDbl", 5), ("pLong", 1234567890123L), ("pText", 1.5m), ("pWhen", when), ("pSpan", 250)));
        var e = store.ExtractLog("test", H.T0, H.T0.AddDays(1), 0, 10, false, out _).Single();
        // values are converted to the declared type when they are recorded...
        Assert.AreEqual(5.0, e.Values["pDbl"]);
        Assert.AreEqual(int.MaxValue, e.Values["pLong"]); // clamped rather than wrapped
        Assert.AreEqual("1.5", e.Values["pText"]); // invariant, whatever the server's culture
        Assert.AreEqual(when.UtcDateTime, e.Values["pWhen"]);
        Assert.AreEqual(DateTimeKind.Utc, ((DateTime)e.Values["pWhen"]).Kind);
        Assert.AreEqual(TimeSpan.FromMilliseconds(250), e.Values["pSpan"]);
        // ...and one with no reading as that type is left out rather than recorded as a zero
        Assert.IsFalse(e.Values.ContainsKey("pInt"));
        store.Dispose();
    }
    [TestMethod]
    public void ValuesRecordedBeforeATypeChangeAreReadAsTheNewType() {
        var io = new IOProviderMemory();
        var before = H.Settings(configure: s => {
            s.Properties.Add("n", new LogProperty { DataType = LogDataType.Integer });
            s.Properties.Add("t", new LogProperty { DataType = LogDataType.String });
        });
        var store = H.Store(io, before);
        store.Record("test", H.Entry(H.T0, ("n", 7), ("t", "not a number")));
        store.Dispose();
        var after = H.Settings(configure: s => {
            s.Properties.Add("n", new LogProperty { DataType = LogDataType.Double });
            s.Properties.Add("t", new LogProperty { DataType = LogDataType.Integer });
        });
        store = H.Store(io, after);
        var e = store.ExtractLog("test", H.T0, H.T0.AddDays(1), 0, 10, false, out _).Single();
        Assert.AreEqual(7.0, e.Values["n"]);
        // no reading as the new type: kept as it was stored, never shown as a zero nobody recorded
        Assert.AreEqual("not a number", e.Values["t"]);
        store.Dispose();
    }
    [TestMethod]
    public void DeclaredColumnsKeepTheirSpellingAndNullsAreLeftOut() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.RichSettings());
        store.Record("test", H.Entry(H.T0, ("PINT", 3), ("pDouble", null!)));
        var e = store.ExtractLog("test", H.T0, H.T0.AddDays(1), 0, 10, false, out _).Single();
        Assert.IsTrue(e.Values.ContainsKey("pInt"));
        Assert.IsFalse(e.Values.ContainsKey("PINT"));
        Assert.IsFalse(e.Values.ContainsKey("pDouble"));
        // and the statistics of the column saw it under its own name
        Assert.AreEqual(3, store.AnalyseCombinedIntegerSums("test", "pInt", Relatude.DB.Logging.Statistics.IntervalType.Hour, H.T0, H.T0.AddHours(1)).Value);
        store.Dispose();
    }
    [TestMethod]
    public void LocalTimestampsAreStoredAsUtc() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.Settings());
        var local = H.T0.ToLocalTime();
        store.Record("test", new LogEntry { Timestamp = local, Values = { ["n"] = 1 } });
        var e = store.ExtractLog("test", H.T0, H.T0.AddDays(1), 0, 10, false, out _).Single();
        Assert.AreEqual(H.T0, e.Timestamp);
        store.Dispose();
    }
    [TestMethod]
    public void ForcingOffLeavesAnEntryOut() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.RichSettings());
        store.Record("test", H.Entry(H.T0, ("pInt", 5)), forceStatistics: false);
        store.ExtractLog("test", H.T0, H.T0.AddDays(1), 0, 10, false, out var total);
        Assert.AreEqual(1, total); // written...
        Assert.AreEqual(0, store.AnalyseCombinedRows("test", Relatude.DB.Logging.Statistics.IntervalType.Hour, H.T0, H.T0.AddHours(1)).Value); // ...but not counted
        store.Record("test", H.Entry(H.T0.AddMinutes(1), ("pInt", 5)), forceLogging: false);
        store.ExtractLog("test", H.T0, H.T0.AddDays(1), 0, 10, false, out total);
        Assert.AreEqual(1, total); // counted, not written
        Assert.AreEqual(1, store.AnalyseCombinedRows("test", Relatude.DB.Logging.Statistics.IntervalType.Hour, H.T0, H.T0.AddHours(1)).Value);
        store.Dispose();
    }
    [TestMethod]
    public void PagesOfALargeRangeMatchTheWholeRangeSorted() {
        // more entries than a page keeps while reading, so the window is trimmed many times over;
        // shuffled, and sharing timestamps, which is where a trimmed window could go wrong
        var io = new IOProviderMemory();
        var store = H.Store(io, H.Settings());
        var random = new Random(7);
        var written = Enumerable.Range(0, 3000).Select(i => (Time: H.T0.AddSeconds(random.Next(600)), N: i)).ToList();
        foreach (var (time, n) in written) store.Record("test", H.Entry(time, ("n", n)));
        foreach (var descending in new[] { true, false }) {
            // a stable sort by time: entries sharing one keep the order they were written in
            var expected = (descending ? written.OrderByDescending(w => w.Time) : written.OrderBy(w => w.Time)).Select(w => w.N).ToList();
            foreach (var (skip, take) in new[] { (0, 100), (100, 100), (2950, 100), (0, 3000), (1234, 1) }) {
                var page = store.ExtractLog("test", H.T0, H.T0.AddHours(1), skip, take, descending, out var total).Select(e => (int)e.Values["n"]).ToList();
                Assert.AreEqual(3000, total);
                CollectionAssert.AreEqual(expected.Skip(skip).Take(take).ToList(), page, $"descending={descending} skip={skip} take={take}");
            }
        }
        store.Dispose();
    }
    [TestMethod]
    public void PagingSkipTakeTotalAndOrdering() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.Settings(configure: s => s.FileInterval = FileInterval.Minute));
        var timestamps = Enumerable.Range(0, 10).Select(i => H.T0.AddSeconds(30 * i)).ToArray(); // spans 5 minute-files
        foreach (var ts in timestamps) store.Record("test", H.Entry(ts, ("n", ts.Second)));
        var ascending = store.ExtractLog("test", H.T0, H.T0.AddMinutes(10), 2, 3, false, out var total).ToList();
        Assert.AreEqual(10, total);
        CollectionAssert.AreEqual(new[] { timestamps[2], timestamps[3], timestamps[4] }, ascending.Select(e => e.Timestamp).ToArray());
        var descending = store.ExtractLog("test", H.T0, H.T0.AddMinutes(10), 0, 3, true, out _).ToList();
        CollectionAssert.AreEqual(new[] { timestamps[9], timestamps[8], timestamps[7] }, descending.Select(e => e.Timestamp).ToArray());
        // from is inclusive, until is exclusive
        store.ExtractLog("test", H.T0, timestamps[9], 0, int.MaxValue, false, out var totalUntil);
        Assert.AreEqual(9, totalUntil);
        store.ExtractLog("test", timestamps[1], timestamps[9].AddTicks(1), 0, int.MaxValue, false, out var totalFrom);
        Assert.AreEqual(9, totalFrom);
        store.Dispose();
    }
    [TestMethod]
    public void ExtractExtremeRangesClampToExistingFiles() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.Settings());
        for (var i = 0; i < 100; i++) store.Record("test", H.Entry(H.T0.AddSeconds(i), ("n", i)));
        var minDt = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        var maxDt = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
        var all = store.ExtractLog("test", minDt, maxDt, 0, int.MaxValue, false, out var total).ToList();
        Assert.AreEqual(100, total);
        Assert.AreEqual(100, all.Count);
        Assert.AreEqual(H.T0, store.GetTimestampOfFirstRecord("test"));
        Assert.AreEqual(H.T0.AddSeconds(99), store.GetTimestampOfLastRecord("test"));
        store.Dispose();
    }
    [TestMethod]
    public void ExtractRequiresUtcDateTimeKind() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.Settings());
        store.Record("test", H.Entry(H.T0, ("n", 1)));
        var local = DateTime.SpecifyKind(H.T0, DateTimeKind.Local);
        var unspecified = DateTime.SpecifyKind(H.T0.AddDays(1), DateTimeKind.Unspecified);
        Assert.ThrowsExactly<Exception>(() => store.ExtractLog("test", local, H.T0.AddDays(1), 0, 1, false, out _));
        Assert.ThrowsExactly<Exception>(() => store.ExtractLog("test", H.T0, unspecified, 0, 1, false, out _));
        store.Dispose();
    }
    [TestMethod]
    public void CompressedLogRoundTripsAcrossRestart() {
        var io = new IOProviderMemory();
        var settings = H.Settings(configure: s => s.Compressed = true);
        var store = H.Store(io, settings);
        for (var i = 0; i < 50; i++) store.Record("test", H.Entry(H.T0.AddSeconds(i), ("text", $"repetitive payload {i % 5}")));
        store.Dispose();
        var store2 = H.Store(io, settings);
        var all = store2.ExtractLog("test", H.T0, H.T0.AddDays(1), 0, int.MaxValue, false, out var total).ToList();
        Assert.AreEqual(50, total);
        Assert.AreEqual("repetitive payload 3", all[3].Values["text"]);
        Assert.IsTrue(store2.GetLogFileSize("test") > 0);
        store2.Dispose();
    }
    [TestMethod]
    public void LogPersistsAcrossRestart() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.Settings());
        store.Record("test", H.Entry(H.T0, ("n", 1)));
        store.Record("test", H.Entry(H.T0.AddSeconds(1), ("n", 2)), flushToDisk: true);
        store.Dispose();
        var store2 = H.Store(io, H.Settings());
        var all = store2.ExtractLog("test", H.T0, H.T0.AddDays(1), 0, int.MaxValue, false, out var total).ToList();
        Assert.AreEqual(2, total);
        Assert.AreEqual(1, all[0].Values["n"]);
        Assert.AreEqual(2, all[1].Values["n"]);
        store2.Dispose();
    }
    [TestMethod]
    public void DisabledLogRecordsNothingUnlessForced() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.Settings(configure: s => s.EnableLog = false));
        Assert.IsTrue(store.Record("test", H.Entry(H.T0, ("n", 1)))); // true: the log exists, even if disabled
        store.ExtractLog("test", H.T0, H.T0.AddDays(1), 0, 10, false, out var total);
        Assert.AreEqual(0, total);
        store.Record("test", H.Entry(H.T0.AddSeconds(1), ("n", 2)), forceLogging: true);
        var all = store.ExtractLog("test", H.T0, H.T0.AddDays(1), 0, 10, false, out total).ToList();
        Assert.AreEqual(1, total);
        Assert.AreEqual(2, all[0].Values["n"]);
        store.Dispose();
    }
    [TestMethod]
    public void DisabledStatisticsRecordNothingUnlessForced() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.Settings(configure: s => s.EnableStatistics = false));
        store.Record("test", H.Entry(H.T0, ("n", 1)));
        Assert.AreEqual(0, store.AnalyseCombinedRows("test", Relatude.DB.Logging.Statistics.IntervalType.Hour, H.T0, H.T0.AddHours(1)).Value);
        store.Record("test", H.Entry(H.T0.AddSeconds(1), ("n", 2)), forceStatistics: true);
        Assert.AreEqual(1, store.AnalyseCombinedRows("test", Relatude.DB.Logging.Statistics.IntervalType.Hour, H.T0, H.T0.AddHours(1)).Value);
        store.Dispose();
    }
    [TestMethod]
    public void TextFormatWritesTabSeparatedLinesWithEscapedValues() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.Settings(configure: s => s.EnableLogTextFormat = true));
        store.Record("test", H.Entry(H.T0, ("a", "va\tl"), ("b", "x\ny"), ("n", 42)));
        store.Dispose();
        var txtKey = FileKeyUtility.Logger_FileNameTxt("test", FileInterval.Day, H.T0.Date);
        Assert.IsTrue(io.ExistsAndIsNotEmpty(txtKey));
        var txt = io.ReadAllTextUTF8(txtKey);
        StringAssert.Contains(txt, "2026-06-01 10:00:00.000");
        StringAssert.Contains(txt, "va l"); // tabs inside values are replaced with spaces
        StringAssert.Contains(txt, "x[CR]y"); // line breaks inside values are replaced with [CR]
        StringAssert.Contains(txt, "42");
    }
    [TestMethod]
    public void RecordToUnknownLogKeyReturnsFalse() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.Settings());
        Assert.IsFalse(store.Record("nope", H.Entry(H.T0, ("n", 1))));
        var result = store.ExtractLog("nope", H.T0, H.T0.AddDays(1), 0, 10, false, out var total);
        Assert.AreEqual(0, total);
        Assert.AreEqual(0, result.Count());
        store.Dispose();
    }
    [TestMethod]
    public void EmptyLogHasNoRecordsAndNoTimestamps() {
        var io = new IOProviderMemory();
        var store = H.Store(io, H.Settings());
        store.ExtractLog("test", H.T0, H.T0.AddDays(1), 0, 10, false, out var total);
        Assert.AreEqual(0, total);
        Assert.IsNull(store.GetTimestampOfFirstRecord("test"));
        Assert.IsNull(store.GetTimestampOfLastRecord("test"));
        Assert.IsNull(store.GetTimestampOfFirstRecord("nope"));
        Assert.IsNull(store.GetTimestampOfLastRecord("nope"));
        store.Dispose();
    }
}
