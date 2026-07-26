using Relatude.DB.Common;

namespace Relatude;

[TestClass]
public class RangeGeneratorTests {

    // shared invariants every generated range list must satisfy
    static void AssertInvariants<T>(List<Tuple<T, T>> ranges, T min, T max, int maxNoRanges) where T : notnull {
        var c = Comparer<T>.Default;
        Assert.IsTrue(ranges.Count >= 1, "at least one range");
        var allowed = maxNoRanges == 1 ? 1 : maxNoRanges + Math.Max(1, maxNoRanges / 5); // the documented soft cap
        Assert.IsTrue(ranges.Count <= allowed, $"{ranges.Count} ranges exceeds allowed {allowed}");
        Assert.IsTrue(c.Compare(ranges[0].Item1, min) <= 0, "first boundary must be at or below min");
        Assert.IsTrue(c.Compare(ranges[^1].Item2, max) >= 0, "last boundary must be at or above max");
        for (var i = 0; i < ranges.Count; i++) {
            Assert.IsTrue(c.Compare(ranges[i].Item1, ranges[i].Item2) < 0, $"range {i} must be ascending");
            if (i > 0) Assert.AreEqual(ranges[i - 1].Item2, ranges[i].Item1, $"range {i} must continue where range {i - 1} ended");
        }
    }

    [TestMethod]
    public void Ints_TightSpanSameMagnitude_GivesFineBuckets() {
        // 50..60 for 10 buckets used to collapse to a single 50-60 bucket (all upper
        // boundaries rounded up to one significant digit = 60); now: ten 1-wide buckets
        var r = RangeGenerators.Ints.GetRanges(50, 60, 10, 1, 10);
        AssertInvariants(r, 50, 60, 10);
        Assert.AreEqual(10, r.Count);
        Assert.AreEqual(50, r[0].Item1);
        Assert.AreEqual(60, r[^1].Item2);
    }

    [TestMethod]
    public void Ints_SameLeadingDigit_GivesFineBuckets() {
        var r = RangeGenerators.Ints.GetRanges(100, 200, 10, 1, 10);
        AssertInvariants(r, 100, 200, 10);
        Assert.AreEqual(10, r.Count);
        foreach (var t in r) Assert.AreEqual(0, t.Item1 % 10, "boundaries must be multiples of the 10-step");
    }

    [TestMethod]
    public void Ints_UnevenSpan_AlignsToNiceStep() {
        var r = RangeGenerators.Ints.GetRanges(137, 873, 10, 1, 10);
        AssertInvariants(r, 137, 873, 10);
        foreach (var t in r) Assert.AreEqual(0, t.Item1 % 100, "boundaries must be multiples of 100");
        Assert.AreEqual(100, r[0].Item1);
        Assert.AreEqual(900, r[^1].Item2);
        Assert.AreEqual(8, r.Count);
    }

    [TestMethod]
    public void Ints_NegativeSpan_AlignedAndZeroIsABoundary() {
        var r = RangeGenerators.Ints.GetRanges(-500, 1000, 10, 1, 10);
        AssertInvariants(r, -500, 1000, 10);
        foreach (var t in r) Assert.AreEqual(0, t.Item1 % 200);
        Assert.IsTrue(r.Any(t => t.Item1 == 0), "zero should be one of the boundaries");
    }

    [TestMethod]
    public void Doubles_UnitSpan_GivesFractionalBuckets() {
        // used to be impossible: boundary offsets were always rounded up to whole numbers
        var r = RangeGenerators.Doubles.GetRanges(0, 1, 10, 1, 10);
        AssertInvariants(r, 0, 1, 10);
        Assert.AreEqual(10, r.Count);
        Assert.AreEqual(0.1, r[0].Item2, 1e-12);
    }

    [TestMethod]
    public void Doubles_PriceLikeSpan_NiceBuckets() {
        var r = RangeGenerators.Doubles.GetRanges(1.5, 105, 10, 1, 10);
        AssertInvariants(r, 1.5, 105, 10);
        Assert.IsTrue(r.Count >= 8, $"expected close to 10 buckets, got {r.Count}");
        foreach (var t in r.Skip(1)) Assert.AreEqual(0, (decimal)t.Item1 % 10, "interior boundaries must be multiples of 10");
    }

    [TestMethod]
    public void Bytes_FullRange_ClampsLastBoundary() {
        var r = RangeGenerators.Bytes.GetRanges(byte.MinValue, byte.MaxValue, 10, 1, 10);
        AssertInvariants(r, byte.MinValue, byte.MaxValue, 10);
        Assert.AreEqual((byte)255, r[^1].Item2);
        Assert.IsTrue(r.Count >= 6, $"expected several buckets, got {r.Count}");
    }

    [TestMethod]
    public void Ints_SpanSmallerThanRequestedCount_OneBucketPerInteger() {
        var r = RangeGenerators.Ints.GetRanges(5, 8, 10, 1, 10);
        AssertInvariants(r, 5, 8, 10);
        Assert.AreEqual(3, r.Count, "cannot have more buckets than whole numbers in the span");
    }

    [TestMethod]
    public void SingleRangeRequest_GivesExactlyOne() {
        var r = RangeGenerators.Doubles.GetRanges(137.2, 873.4, 1, 1, 10);
        Assert.AreEqual(1, r.Count);
        Assert.IsTrue(r[0].Item1 <= 137.2 && r[0].Item2 >= 873.4);
        var negativeStraddle = RangeGenerators.Ints.GetRanges(-500, 1000, 1, 1, 10);
        Assert.AreEqual(1, negativeStraddle.Count);
        Assert.IsTrue(negativeStraddle[0].Item1 <= -500 && negativeStraddle[0].Item2 >= 1000);
    }

    [TestMethod]
    public void EqualValues_GiveSingleRange() {
        var r = RangeGenerators.Ints.GetRanges(42, 42, 10, 1, 10);
        Assert.AreEqual(1, r.Count);
        Assert.AreEqual(42, r[0].Item1);
        Assert.AreEqual(42, r[0].Item2);
    }

    [TestMethod]
    public void PowerBase_GrowingBucketsWithRoundBoundaries() {
        var r = RangeGenerators.Ints.GetRanges(0, 1000, 5, 2, 10);
        AssertInvariants(r, 0, 1000, 5);
        Assert.IsTrue(r.Count >= 3, $"power curve should keep several buckets, got {r.Count}");
        var widths = r.Select(t => t.Item2 - t.Item1).ToList();
        Assert.IsTrue(widths[^1] > widths[0], "buckets must grow towards the max for powerBase > 1");
    }

    [TestMethod]
    public void DateTimes_MultiYearSpan_AlignsToCalendarUnits() {
        // used to collapse to a single bucket ending in year 2219 (ticks rounded to one significant digit)
        var r = RangeGenerators.DateTimes.GetRanges(new DateTime(2020, 1, 12), new DateTime(2022, 2, 9), 10, 1, 10);
        AssertInvariants(r, new DateTime(2020, 1, 12), new DateTime(2022, 2, 9), 10);
        Assert.IsTrue(r.Count >= 5, $"expected several buckets, got {r.Count}");
        foreach (var t in r) {
            Assert.AreEqual(1, t.Item1.Day, "calendar boundaries must fall on the 1st of a month");
            Assert.AreEqual(TimeSpan.Zero, t.Item1.TimeOfDay, "calendar boundaries must fall on midnight");
        }
    }

    [TestMethod]
    public void DateTimes_DecadeSpan_AlignsToYears() {
        var r = RangeGenerators.DateTimes.GetRanges(new DateTime(2015, 3, 7), new DateTime(2024, 8, 20), 10, 1, 10);
        AssertInvariants(r, new DateTime(2015, 3, 7), new DateTime(2024, 8, 20), 10);
        foreach (var t in r.Skip(1)) {
            Assert.AreEqual(1, t.Item1.Month, "interior boundaries must be January 1st");
            Assert.AreEqual(1, t.Item1.Day);
        }
        Assert.IsTrue(r.Count >= 8, $"a ten year span should give about one bucket per year, got {r.Count}");
    }

    [TestMethod]
    public void DateTimes_IntradaySpan_AlignsToWholeHours() {
        var from = new DateTime(2024, 5, 17, 8, 13, 22);
        var to = new DateTime(2024, 5, 17, 17, 47, 3);
        var r = RangeGenerators.DateTimes.GetRanges(from, to, 10, 1, 10);
        AssertInvariants(r, from, to, 10);
        Assert.AreEqual(10, r.Count);
        foreach (var t in r) {
            Assert.AreEqual(0, t.Item1.Minute);
            Assert.AreEqual(0, t.Item1.Second);
        }
    }

    [TestMethod]
    public void DateTimes_WeekScaleSpan_AlignsToMidnight() {
        var from = new DateTime(2024, 5, 3, 14, 0, 0);
        var to = new DateTime(2024, 5, 15, 9, 0, 0);
        var r = RangeGenerators.DateTimes.GetRanges(from, to, 12, 1, 10);
        AssertInvariants(r, from, to, 12);
        foreach (var t in r) Assert.AreEqual(TimeSpan.Zero, t.Item1.TimeOfDay, "day buckets must start at midnight");
    }

    [TestMethod]
    public void TimeSpans_AlignToNaturalClockSteps() {
        var r = RangeGenerators.TimeSpans.GetRanges(TimeSpan.Zero, TimeSpan.FromMinutes(90), 10, 1, 10);
        AssertInvariants(r, TimeSpan.Zero, TimeSpan.FromMinutes(90), 10);
        Assert.AreEqual(9, r.Count);
        foreach (var t in r) Assert.AreEqual(0, t.Item1.Ticks % TimeSpan.TicksPerMinute / TimeSpan.TicksPerSecond, "boundaries must be whole minutes");
    }

    [TestMethod]
    public void TimeSpans_NegativeSpan_Covered() {
        var r = RangeGenerators.TimeSpans.GetRanges(TimeSpan.FromHours(-5), TimeSpan.FromHours(5), 10, 1, 10);
        AssertInvariants(r, TimeSpan.FromHours(-5), TimeSpan.FromHours(5), 10);
        Assert.IsTrue(r.Any(t => t.Item1 == TimeSpan.Zero), "zero should be one of the boundaries");
    }

    [TestMethod]
    public void ExtremeDoubles_DoNotThrow_AndCover() {
        var r = RangeGenerators.Doubles.GetRanges(-1e300, 1e300, 10, 1, 10);
        Assert.IsTrue(r.Count >= 1);
        Assert.IsTrue(r[0].Item1 <= -1e300);
        Assert.IsTrue(r[^1].Item2 >= 1e300);
    }

    [TestMethod]
    public void SweepManySpans_InvariantsAlwaysHold() {
        var interesting = new double[] { 0, 1, 1.5, 7, 10, 99, 100, 101, 137, 999, 1000, 12345, 0.001, 0.5 };
        foreach (var a in interesting) {
            foreach (var b in interesting) {
                if (a >= b) continue;
                foreach (var n in new[] { 1, 2, 3, 5, 10, 20 }) {
                    var rd = RangeGenerators.Doubles.GetRanges(a, b, n, 1, 10);
                    AssertInvariants(rd, a, b, n);
                    var ri = RangeGenerators.Ints.GetRanges((int)a, (int)(b + 1), n, 1, 10);
                    AssertInvariants(ri, (int)a, (int)(b + 1), n);
                    var rp = RangeGenerators.Doubles.GetRanges(a, b, n, 3, 10);
                    AssertInvariants(rp, a, b, n);
                }
            }
        }
    }

    [TestMethod]
    public void RequestedCountIsApproached_NotJustBounded() {
        // the old generator often collapsed to 1-2 buckets; make sure typical spans get close to the request
        foreach (var (a, b) in new (double, double)[] { (50, 60), (100, 200), (0, 873), (1.5, 105), (0.2, 0.9), (990, 1010) }) {
            var r = RangeGenerators.Doubles.GetRanges(a, b, 10, 1, 10);
            Assert.IsTrue(r.Count >= 5, $"span {a}..{b}: expected at least 5 buckets for a request of 10, got {r.Count}");
        }
    }
}
