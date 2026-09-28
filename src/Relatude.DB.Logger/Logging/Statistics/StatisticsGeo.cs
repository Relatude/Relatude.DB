using Relatude.DB.Common;
using System.Globalization;

namespace Relatude.DB.Logging.Statistics;

// The statistics a column of positions (LogDataType.GeoCoordinate) can keep. Two of them have
// aggregators of their own - the centre and spread, and the heatmap - and the rest turn a position
// into something an existing statistic already counts: a distance for the CountSumAvgMinMax, the
// name of a band or a zone for the count per value, the name of a grid cell for the HyperLogLog. So
// those are drawn by the graphs the other statistics already have.

/// <summary>
/// Where the positions of each interval were: their centre, how far from it they lay, the shape of
/// the spread and the box round them (see <see cref="GeoMoments"/>). Fifteen numbers an interval,
/// and any range of intervals merges exactly.
/// </summary>
public sealed class StatisticsGeoSpread : StatisticsBase<GeoMoments, GeoCoordinate> {
    public StatisticsGeoSpread(StatisticsInfo info, DayOfWeek firstDayOfWeek, string key) : base(info, firstDayOfWeek, key) { }
    public override void RecordIfPossible(DateTime dtUtc, object value) {
        if (value is GeoCoordinate { IsEmpty: false } position) Record(dtUtc, position);
    }
    protected override StatisticsIntervalBase<GeoMoments, GeoCoordinate> CreateStatistics(StatisticsInfo info, IntervalType intervalType, int maxNoIntervals, DayOfWeek firstDayOfWeek) {
        return new GeoSpreadIntervals(info, intervalType, maxNoIntervals, firstDayOfWeek);
    }
    public override bool CanCombine => true;
    public override Interval<GeoMoments> Combine(List<Interval<GeoMoments>> values, DateTime from, DateTime to, IntervalType interval) {
        var combined = new GeoMoments(); // a new one: the intervals' own go on being counted into
        foreach (var v in values) if (v.HasValue) combined.Add(v.Value);
        return new(from, to, combined);
    }
}
sealed class GeoSpreadIntervals : StatisticsIntervalBase<GeoMoments, GeoCoordinate> {
    public GeoSpreadIntervals(StatisticsInfo info, IntervalType intervalType, int maxNoIntervals, DayOfWeek firstDayOfWeek) : base(info, intervalType, maxNoIntervals, firstDayOfWeek) { }
    protected override GeoMoments CreateAggregator() => new();
    protected override void Record(Interval<GeoMoments> interval, GeoCoordinate recordValue) => interval.Value.Add(recordValue);
    protected override byte[] SerializeAggregator(GeoMoments item) {
        var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms)) item.Write(bw);
        return ms.ToArray();
    }
    protected override GeoMoments DeserializeAggregator(byte[] bytes) {
        using var br = new BinaryReader(new MemoryStream(bytes));
        return GeoMoments.Read(br);
    }
}

/// <summary>How far the positions were from a reference point, in meters: count, total, average, min and max.</summary>
public sealed class StatisticsGeoDistance : StatisticsCountSumAvgMinMax {
    readonly GeoCoordinate _from;
    public StatisticsGeoDistance(StatisticsInfo info, DayOfWeek firstDayOfWeek, string key) : base(info, firstDayOfWeek, key) => _from = info.Reference;
    public override void RecordIfPossible(DateTime dtUtc, object value) {
        if (value is GeoCoordinate { IsEmpty: false } position && !_from.IsEmpty) Record(dtUtc, position.DistanceTo(_from));
    }
}

/// <summary>How many positions fell in each band of distance from a reference point.</summary>
public sealed class StatisticsGeoBands : StatisticsGroupCount {
    readonly GeoCoordinate _from;
    readonly double[] _edges;
    readonly string[] _labels;
    public StatisticsGeoBands(StatisticsInfo info, DayOfWeek firstDayOfWeek, string key) : base(info, firstDayOfWeek, key) {
        _from = info.Reference;
        _edges = info.Bands ?? [];
        _labels = LabelsOf(_edges);
    }
    public override void RecordIfPossible(DateTime dtUtc, object value) {
        if (value is not GeoCoordinate { IsEmpty: false } position || _from.IsEmpty) return;
        var distance = position.DistanceTo(_from);
        var band = 0;
        while (band < _edges.Length && distance >= _edges[band]) band++;
        Record(dtUtc, _labels[band]);
    }
    /// <summary>The bands' names, nearest first: what the counts are kept under, and the order they are shown in.</summary>
    public static string[] LabelsOf(StatisticsInfo info) => LabelsOf(info.Bands ?? []);
    public static string[] LabelsOf(IReadOnlyList<double> edges) {
        if (edges.Count == 0) return ["any distance"];
        var labels = new string[edges.Count + 1];
        labels[0] = "under " + FormatDistance(edges[0]);
        for (var i = 1; i < edges.Count; i++) labels[i] = rangeText(edges[i - 1], edges[i]);
        labels[^1] = "over " + FormatDistance(edges[^1]);
        return labels;
    }
    // one unit for both ends when they share it: "1-5 km" rather than "1 km-5 km"
    static string rangeText(double from, double to) {
        if (from >= 1000) return number(from / 1000) + "–" + number(to / 1000) + " km";
        if (to < 1000) return number(from) + "–" + number(to) + " m";
        return FormatDistance(from) + "–" + FormatDistance(to);
    }
    /// <summary>A distance the way a band is named by it: meters under a kilometre, kilometres from there.</summary>
    public static string FormatDistance(double meters) => meters >= 1000 ? number(meters / 1000) + " km" : number(meters) + " m";
    static string number(double value) => value.ToString(value >= 100 || value == Math.Round(value) ? "0" : "0.#", CultureInfo.InvariantCulture);
}

/// <summary>How many positions fell in each of a set of named zones: the first zone a position is in, or <see cref="GeoZone.OutsideName"/>.</summary>
public sealed class StatisticsGeoZones : StatisticsGroupCount {
    readonly GeoZone[] _zones;
    public StatisticsGeoZones(StatisticsInfo info, DayOfWeek firstDayOfWeek, string key) : base(info, firstDayOfWeek, key) => _zones = info.Zones ?? [];
    public override void RecordIfPossible(DateTime dtUtc, object value) {
        if (value is not GeoCoordinate { IsEmpty: false } position) return;
        foreach (var zone in _zones) {
            if (zone.Contains(position)) {
                Record(dtUtc, zone.Name);
                return;
            }
        }
        Record(dtUtc, GeoZone.OutsideName);
    }
    /// <summary>The zones' names in the order they are tested, and last what a position in none of them is.</summary>
    public static string[] LabelsOf(StatisticsInfo info) => [.. (info.Zones ?? []).Select(z => z.Name), GeoZone.OutsideName];
}

/// <summary>
/// How many different cells of a grid had a position in them (see <see cref="GeoCell"/>): how much
/// ground the entries covered, estimated with the same HyperLogLog as any other unique count.
/// </summary>
public sealed class StatisticsGeoCoverage : StatisticsEstimatedUniqueCount {
    readonly int _level;
    public StatisticsGeoCoverage(StatisticsInfo info, DayOfWeek firstDayOfWeek, string key) : base(info, firstDayOfWeek, key) => _level = info.EffectiveLevel;
    public override void RecordIfPossible(DateTime dtUtc, object value) {
        if (value is GeoCoordinate { IsEmpty: false } position) Record(dtUtc, GeoCell.Of(position, _level).Key.ToString("x", CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// How many positions each cell of a grid held, per interval: a heatmap for every hour, day, week and
/// month. The cells are counted at the statistic's level, and an interval that is over is kept in a
/// budget of cells, merged where there is little in them and fine where there is a lot, without a
/// count lost (see <see cref="GeoCellCounts.Compress"/>).
/// </summary>
public sealed class StatisticsGeoHeatmap : StatisticsBase<AggregatorGeoGrid, GeoCoordinate> {
    /// <summary>How many cells a range of intervals is drawn in, unless asked for another number.</summary>
    public const int DefaultCombinedCells = 4096;
    public StatisticsGeoHeatmap(StatisticsInfo info, DayOfWeek firstDayOfWeek, string key) : base(info, firstDayOfWeek, key) { }
    public override void RecordIfPossible(DateTime dtUtc, object value) {
        if (value is GeoCoordinate { IsEmpty: false } position) Record(dtUtc, position);
    }
    protected override StatisticsIntervalBase<AggregatorGeoGrid, GeoCoordinate> CreateStatistics(StatisticsInfo info, IntervalType intervalType, int maxNoIntervals, DayOfWeek firstDayOfWeek) {
        return new GeoGridIntervals(info, intervalType, maxNoIntervals, firstDayOfWeek);
    }
    public override bool CanCombine => true;
    public override Interval<AggregatorGeoGrid> Combine(List<Interval<AggregatorGeoGrid>> values, DateTime from, DateTime to, IntervalType interval) {
        return new(from, to, AggregatorGeoGrid.Merge(values.Where(v => v.HasValue).Select(v => v.Value), Info.EffectiveLevel, DefaultCombinedCells));
    }
    /// <summary>The heatmap of a whole range, in at most <paramref name="maxCells"/> cells.</summary>
    public Interval<AggregatorGeoGrid> GetCombinedValue(IntervalType intervalType, DateTime fromUtc, DateTime toUtc, int maxCells) {
        var values = GetValues(intervalType, fromUtc, toUtc, false, false, null).Where(v => v.HasValue).Select(v => v.Value).ToList();
        return new(fromUtc, toUtc, AggregatorGeoGrid.Merge(values, Info.EffectiveLevel, Math.Max(1, maxCells)));
    }
}
sealed class GeoGridIntervals : StatisticsIntervalBase<AggregatorGeoGrid, GeoCoordinate> {
    readonly int _level;
    readonly int _budget;
    public GeoGridIntervals(StatisticsInfo info, IntervalType intervalType, int maxNoIntervals, DayOfWeek firstDayOfWeek) : base(info, intervalType, maxNoIntervals, firstDayOfWeek) {
        _level = info.EffectiveLevel;
        _budget = AggregatorGeoGrid.BudgetOf(intervalType);
    }
    protected override AggregatorGeoGrid CreateAggregator() => new(_level, _budget);
    protected override void Record(Interval<AggregatorGeoGrid> interval, GeoCoordinate recordValue) => interval.Value.Record(recordValue);
    protected override byte[] SerializeAggregator(AggregatorGeoGrid item) => item.Serialize();
    protected override AggregatorGeoGrid DeserializeAggregator(byte[] bytes) => AggregatorGeoGrid.Deserialize(bytes, _budget);
}

/// <summary>A heatmap as it was when it was read: the cells, disjoint, in code order, and the total they hold.</summary>
public sealed record GeoHeatmap(int Level, long Total, IReadOnlyList<GeoCellCount> Cells);

/// <summary>The counts per cell of one interval of a heatmap.</summary>
public sealed class AggregatorGeoGrid : ICondensable {
    /// <summary>
    /// How many cells an interval is kept in once it is over, by how long it is: a second holds a
    /// handful of positions, a month may hold a country. Worst case, at the default level of detail
    /// (3), that is about 290,000 cells a heatmap - under 2 MB in the statistics file, about 5 MB in
    /// memory; a quiet log keeps far less.
    /// </summary>
    public static int BudgetOf(IntervalType interval) => interval switch {
        IntervalType.Second => 8,
        IntervalType.Minute => 32,
        IntervalType.Hour => 128,
        _ => 512,
    };
    // an interval being counted into holds at most this many cells before it is compressed to four
    // times its budget: a busy month at a fine level would otherwise hold every place there is
    const int openLimit = 16_384;

    readonly int _level;
    readonly int _budget;
    Dictionary<ulong, long>? _open; // cell key -> count, while being counted into
    GeoCellCount[] _kept = [];      // or compressed, once the interval is over
    public AggregatorGeoGrid(int level, int budget) {
        _level = Math.Clamp(level, 1, GeoCell.MaxLevel);
        _budget = Math.Max(1, budget);
    }
    public int Level => _level;
    public long Total { get; private set; }
    public void Record(GeoCoordinate position) {
        if (position.IsEmpty) return;
        var open = ensureOpen();
        var key = GeoCell.Of(position, _level).Key;
        open[key] = open.TryGetValue(key, out var n) ? n + 1 : 1;
        Total++;
        if (open.Count > openLimit) {
            _kept = [.. GeoCellCounts.Compress(cellsOf(open), _budget * 4)];
            _open = null;
        }
    }
    Dictionary<ulong, long> ensureOpen() {
        if (_open != null) return _open;
        _open = new(Math.Max(16, _kept.Length));
        foreach (var c in _kept) _open[c.Cell.Key] = _open.TryGetValue(c.Cell.Key, out var had) ? had + c.Count : c.Count;
        _kept = [];
        return _open;
    }
    static IEnumerable<GeoCellCount> cellsOf(Dictionary<ulong, long> open) => open.Select(kv => new GeoCellCount(GeoCell.FromKey(kv.Key), kv.Value));
    public IEnumerable<GeoCellCount> Cells => _open != null ? cellsOf(_open) : _kept;
    /// <summary>A copy of the cells, disjoint and in code order (a coarse count still covering finer
    /// ones recorded since is shared among them, see <see cref="GeoCellCounts.Compress"/>).</summary>
    public GeoHeatmap Snapshot() => new(_level, Total, GeoCellCounts.Compress(Cells, int.MaxValue));
    public void Condense() {
        if (_open == null) return;
        _kept = [.. GeoCellCounts.Compress(cellsOf(_open), _budget)];
        _open = null;
    }
    // a record out of order into an interval that is over opens it again: nothing is thrown away by condensing
    public bool AcceptsValues => true;

    /// <summary>Several intervals as one, in at most <paramref name="budget"/> cells.</summary>
    public static AggregatorGeoGrid Merge(IEnumerable<AggregatorGeoGrid> grids, int level, int budget) {
        var merged = new AggregatorGeoGrid(level, budget);
        var all = new List<GeoCellCount>();
        foreach (var grid in grids) {
            all.AddRange(grid.Cells);
            merged.Total += grid.Total;
        }
        merged._kept = [.. GeoCellCounts.Compress(all, budget)];
        return merged;
    }

    // ---- as bytes: the cells in key order, each as the distance from the last key and its count ----

    const byte formatVersion = 1;
    public byte[] Serialize() {
        var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms)) {
            bw.Write(formatVersion);
            bw.Write((byte)_level);
            bw.Write(Total);
            var cells = Cells.OrderBy(c => c.Cell.Key).ToArray();
            bw.Write(cells.Length);
            ulong last = 0;
            foreach (var c in cells) {
                bw.Write7BitEncodedInt64((long)(c.Cell.Key - last)); // keys stay below 2^63: the level is at most 29
                bw.Write7BitEncodedInt64(c.Count);
                last = c.Cell.Key;
            }
        }
        return ms.ToArray();
    }
    public static AggregatorGeoGrid Deserialize(byte[] bytes, int budget) {
        using var br = new BinaryReader(new MemoryStream(bytes));
        var version = br.ReadByte();
        if (version != formatVersion) throw new InvalidDataException("Unknown heatmap format " + version + ". ");
        var grid = new AggregatorGeoGrid(br.ReadByte(), budget);
        grid.Total = br.ReadInt64();
        var count = br.ReadInt32();
        var cells = new GeoCellCount[count];
        ulong key = 0;
        for (var i = 0; i < count; i++) {
            key += (ulong)br.Read7BitEncodedInt64();
            cells[i] = new GeoCellCount(GeoCell.FromKey(key), br.Read7BitEncodedInt64());
        }
        grid._kept = cells;
        return grid;
    }
}
