using Relatude.DB.IO;
using Relatude.DB.Logging;

namespace Relatude.Logger;

// The text search over recorded entries: what a query means, and what searching a log gives back.
[TestClass]
public class LogStoreSearchTests {
    static LogSettings settings() => H.Settings(configure: s => {
        s.Properties.Add("message", new LogProperty { Name = "Message", DataType = LogDataType.String });
        s.Properties.Add("type", new LogProperty { Name = "Type", DataType = LogDataType.String });
        s.Properties.Add("ms", new LogProperty { Name = "Duration ms", DataType = LogDataType.Integer });
    });
    // four entries, one per minute from T0
    static LogStore stocked(out IIOProvider io) {
        io = new IOProviderMemory();
        var store = H.Store(io, settings());
        store.Record("test", H.Entry(H.T0.AddMinutes(0), ("message", "Could not open the file"), ("type", "Error"), ("ms", 120)));
        store.Record("test", H.Entry(H.T0.AddMinutes(1), ("message", "Opened the file"), ("type", "Info"), ("ms", 3)));
        store.Record("test", H.Entry(H.T0.AddMinutes(2), ("message", "GetNodes took a while"), ("type", "Warning"), ("ms", 2500)));
        store.Record("test", H.Entry(H.T0.AddMinutes(3), ("message", "getnodes again"), ("type", "Info"), ("ms", 40)));
        return store;
    }
    static string[] search(LogStore store, string query, bool caseSensitive = false) {
        var entries = store.SearchLog("test", LogSearch.Parse(query, caseSensitive), H.T0, H.T0.AddDays(1), 0, 100, false, out _);
        return entries.Select(e => (string)e.Values["message"]).ToArray();
    }

    [TestMethod]
    public void TermWithoutWildcardsIsFoundAnywhereInAValue() {
        var store = stocked(out _);
        CollectionAssert.AreEqual(new[] { "Could not open the file", "Opened the file" }, search(store, "open"));
        store.Dispose();
    }
    [TestMethod]
    public void SearchIsCaseInsensitiveUnlessAskedOtherwise() {
        var store = stocked(out _);
        Assert.AreEqual(2, search(store, "getnodes").Length);
        Assert.AreEqual(1, search(store, "getnodes", caseSensitive: true).Length);
        Assert.AreEqual(1, search(store, "GetNodes", caseSensitive: true).Length);
        store.Dispose();
    }
    [TestMethod]
    public void WildcardsStandForAnythingInsideAValue() {
        var store = stocked(out _);
        // * is any run of characters, in the middle of a value as much as at its ends
        CollectionAssert.AreEqual(new[] { "Could not open the file", "Opened the file" }, search(store, "open*file"));
        CollectionAssert.AreEqual(new[] { "GetNodes took a while", "getnodes again" }, search(store, "get*nodes"));
        // ? is exactly one character
        CollectionAssert.AreEqual(new[] { "Could not open the file", "Opened the file" }, search(store, "?pen"));
        // "not open" has two characters before "pen"; "Opened" at the start of a value has one
        CollectionAssert.AreEqual(new[] { "Could not open the file" }, search(store, "??pen"));
        // a pattern with spaces in it is quoted, or the spaces would make it several terms
        CollectionAssert.AreEqual(new[] { "Opened the file" }, search(store, "\"?pened the file\""));
        store.Dispose();
    }
    [TestMethod]
    public void AStarAtEitherEndOfATermChangesNothing() {
        var store = stocked(out _);
        // the one thing a wildcard must never do is take away entries the term was already finding
        var plain = search(store, "file");
        CollectionAssert.AreEqual(plain, search(store, "file*"));
        CollectionAssert.AreEqual(plain, search(store, "*file"));
        CollectionAssert.AreEqual(plain, search(store, "*file*"));
        store.Dispose();
    }
    [TestMethod]
    public void EveryTermHasToMatchAndADashExcludes() {
        var store = stocked(out _);
        // both entries hold both terms: one of them in the word "Opened"
        CollectionAssert.AreEqual(new[] { "Could not open the file", "Opened the file" }, search(store, "file open"));
        CollectionAssert.AreEqual(new[] { "Could not open the file" }, search(store, "file -opened"));
        Assert.AreEqual(4, search(store, "-nothinglikethis").Length);
        store.Dispose();
    }
    [TestMethod]
    public void QuotesHoldAPhraseTogether() {
        var store = stocked(out _);
        CollectionAssert.AreEqual(new[] { "Could not open the file" }, search(store, "\"could not open\""));
        Assert.AreEqual(0, search(store, "\"open the\" \"not there\"").Length);
        // a phrase can be excluded as well as looked for
        Assert.AreEqual(4, search(store, "-\"not there at all\"").Length);
        Assert.AreEqual(2, search(store, "-\"could not open\" -\"took a while\"").Length);
        store.Dispose();
    }
    [TestMethod]
    public void ATermCanNameTheColumnItSearches() {
        var store = stocked(out _);
        CollectionAssert.AreEqual(new[] { "Opened the file", "getnodes again" }, search(store, "type:info"));
        // by the name the column shows under as well as by its key
        CollectionAssert.AreEqual(new[] { "GetNodes took a while" }, search(store, "\"duration ms\":2500"));
        // a column that holds it, but the term names another one
        Assert.AreEqual(0, search(store, "type:file").Length);
        store.Dispose();
    }
    [TestMethod]
    public void AColumnIsNamedWhateverItsCase() {
        var store = stocked(out _);
        CollectionAssert.AreEqual(new[] { "GetNodes took a while" }, search(store, "TYPE:warning"));
        CollectionAssert.AreEqual(new[] { "GetNodes took a while" }, search(store, "\"DURATION MS\":2500"));
        store.Dispose();
    }
    [TestMethod]
    public void AColumnTheEntryDoesNotCarryMatchesNothing() {
        var io = new IOProviderMemory();
        var store = H.Store(io, settings());
        store.Record("test", H.Entry(H.T0, ("message", "type is nowhere to be seen")));
        // the term names a column of this log, so it is that column that has to hold the text -
        // not the message that happens to contain both words
        Assert.AreEqual(0, search(store, "type:nowhere").Length);
        store.Dispose();
    }
    [TestMethod]
    public void AValueRecordedUnderAKeyTheLogNoLongerDeclaresIsStillNamed() {
        var io = new IOProviderMemory();
        var store = H.Store(io, settings());
        store.Record("test", H.Entry(H.T0, ("message", "plain"), ("oldcolumn", "gone but recorded")));
        CollectionAssert.AreEqual(new[] { "plain" }, search(store, "oldcolumn:recorded"));
        Assert.AreEqual(0, search(store, "oldcolumn:missing").Length);
        store.Dispose();
    }
    [TestMethod]
    public void AColonInSomethingThatIsNoColumnIsSearchedAsText() {
        var io = new IOProviderMemory();
        var store = H.Store(io, settings());
        store.Record("test", H.Entry(H.T0, ("message", "ratio 10:30 of the whole")));
        store.Record("test", H.Entry(H.T0.AddMinutes(1), ("message", "nothing of the sort")));
        CollectionAssert.AreEqual(new[] { "ratio 10:30 of the whole" }, search(store, "10:30"));
        store.Dispose();
    }
    [TestMethod]
    public void TheTimestampIsSearchableByItself() {
        var store = stocked(out _);
        // 2026-06-01 10:00, 10:01, 10:02, 10:03
        Assert.AreEqual(4, search(store, "2026-06-01").Length);
        CollectionAssert.AreEqual(new[] { "GetNodes took a while" }, search(store, "time:*10:02*"));
        store.Dispose();
    }
    [TestMethod]
    public void NumbersAreSearchedAsTheyWereRecorded() {
        var store = stocked(out _);
        CollectionAssert.AreEqual(new[] { "GetNodes took a while" }, search(store, "2500"));
        CollectionAssert.AreEqual(new[] { "Could not open the file" }, search(store, "ms:120"));
        store.Dispose();
    }
    [TestMethod]
    public void AnEmptySearchIsEveryEntry() {
        var store = stocked(out _);
        Assert.IsTrue(LogSearch.Parse("   ").IsEmpty);
        Assert.IsTrue(LogSearch.Parse(null).IsEmpty);
        Assert.AreEqual(4, search(store, "").Length);
        store.Dispose();
    }
    [TestMethod]
    public void TotalCountsEveryMatchAndThePageIsWhatWasAskedFor() {
        var store = stocked(out _);
        var page = store.SearchLog("test", LogSearch.Parse("the"), H.T0, H.T0.AddDays(1), 1, 1, false, out var total).ToArray();
        Assert.AreEqual(2, total); // "Could not open the file" and "Opened the file"
        Assert.AreEqual(1, page.Length);
        Assert.AreEqual("Opened the file", page[0].Values["message"]);
        store.Dispose();
    }
    [TestMethod]
    public void OrderIsByTimestampEitherWay() {
        var store = stocked(out _);
        var newest = store.SearchLog("test", LogSearch.Parse("e"), H.T0, H.T0.AddDays(1), 0, 2, true, out _).ToArray();
        Assert.AreEqual(H.T0.AddMinutes(3), newest[0].Timestamp);
        Assert.AreEqual(H.T0.AddMinutes(2), newest[1].Timestamp);
        var oldest = store.SearchLog("test", LogSearch.Parse("e"), H.T0, H.T0.AddDays(1), 0, 2, false, out _).ToArray();
        Assert.AreEqual(H.T0, oldest[0].Timestamp);
        store.Dispose();
    }
    [TestMethod]
    public void TheRangeBoundsTheSearch() {
        var store = stocked(out _);
        var entries = store.SearchLog("test", LogSearch.Parse("*"), H.T0.AddMinutes(1), H.T0.AddMinutes(3), 0, 100, false, out var total);
        Assert.AreEqual(2, total); // [from, to): minute 1 and 2, not 3
        Assert.AreEqual(2, entries.Count());
        store.Dispose();
    }
    [TestMethod]
    public void SearchingSurvivesARestartAndReadsWhatIsOnDisk() {
        var store = stocked(out var io);
        store.Dispose(); // flushes the buffer to disk
        var reopened = H.Store(io, settings());
        Assert.AreEqual(2, search(reopened, "file").Length);
        reopened.Dispose();
    }
    [TestMethod]
    public void SearchingALogThatIsNotThereGivesNothing() {
        var io = new IOProviderMemory();
        var store = H.Store(io, settings());
        var entries = store.SearchLog("nosuchlog", LogSearch.Parse("anything"), H.T0, H.T0.AddDays(1), 0, 10, false, out var total);
        Assert.AreEqual(0, total);
        Assert.AreEqual(0, entries.Count());
        store.Dispose();
    }
    [TestMethod]
    public void BinaryValuesAreNeverMatched() {
        var io = new IOProviderMemory();
        var s = H.Settings(configure: x => {
            x.Properties.Add("blob", new LogProperty { Name = "Blob", DataType = LogDataType.Bytes });
            x.Properties.Add("message", new LogProperty { Name = "Message", DataType = LogDataType.String });
        });
        var store = H.Store(io, s);
        store.Record("test", H.Entry(H.T0, ("blob", new byte[] { 65, 66, 67 }), ("message", "plain")));
        Assert.AreEqual(1, store.SearchLog("test", LogSearch.Parse("plain"), H.T0, H.T0.AddDays(1), 0, 10, false, out _).Count());
        Assert.AreEqual(0, store.SearchLog("test", LogSearch.Parse("ABC"), H.T0, H.T0.AddDays(1), 0, 10, false, out _).Count());
        store.Dispose();
    }

    // ---- the matcher itself ----

    [TestMethod]
    public void WildcardMatching() {
        Assert.IsTrue(LogSearch.MatchesWildcard("abc", "*", false));
        Assert.IsTrue(LogSearch.MatchesWildcard("abc", "a*c", false));
        Assert.IsTrue(LogSearch.MatchesWildcard("abc", "a?c", false));
        Assert.IsTrue(LogSearch.MatchesWildcard("abc", "***a***b***c***", false));
        Assert.IsTrue(LogSearch.MatchesWildcard("abc", "abc", false));
        Assert.IsTrue(LogSearch.MatchesWildcard("ABC", "abc", false));
        Assert.IsFalse(LogSearch.MatchesWildcard("ABC", "abc", true));
        Assert.IsFalse(LogSearch.MatchesWildcard("abc", "a?", false));
        Assert.IsFalse(LogSearch.MatchesWildcard("abc", "b*", false));
        Assert.IsFalse(LogSearch.MatchesWildcard("abc", "*b", false));
        Assert.IsTrue(LogSearch.MatchesWildcard("", "*", false));
        Assert.IsFalse(LogSearch.MatchesWildcard("", "?", false));
        // the backtracking case: the first run after the star does not fit, a later one does
        Assert.IsTrue(LogSearch.MatchesWildcard("aXaXaXb", "*aXb", false));
        Assert.IsFalse(LogSearch.MatchesWildcard("aXaXaXb", "*aYb", false));
    }
}
