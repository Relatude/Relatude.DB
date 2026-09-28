using System.Globalization;

namespace Relatude.DB.Common;

/// <summary>
/// A cell of the quadtree that a <see cref="GeoCoordinate"/>'s storage value already is.
///
/// The storage value is a Morton code - latitude on the even bits, longitude on the odd ones - so the
/// top 2k bits of it name one of 4^k cells of an equal-angle grid: 2^k rows of 180/2^k degrees and
/// 2^k columns of 360/2^k degrees. A cell's parent is the same prefix two bits shorter, a cell and
/// everything inside it are one contiguous run of codes, and the cells of one level sort in the same
/// order the index does. Nothing has to be looked up or stored to find a position's cell: it is a
/// shift.
///
/// Cells are twice as wide in degrees as they are tall, which on the ground is square at 60 degrees
/// north or south (a degree of longitude is half a degree of latitude there) - Oslo, Helsinki,
/// Anchorage - and 2:1 at the equator. Their area shrinks with the cosine of the latitude, so a count
/// per cell is a density only once it is divided by <see cref="AreaSquareMeters"/>.
///
/// Level 12 is about 4.9 km tall, 14 about 1.2 km, 16 about 305 m, 18 about 76 m, 20 about 19 m.
/// </summary>
public readonly struct GeoCell : IEquatable<GeoCell>, IComparable<GeoCell> {
    /// <summary>The finest level a cell can be: its prefix is 58 bits, and the level sits in the six above them.</summary>
    public const int MaxLevel = 29;
    const int levelShift = 58;
    const ulong prefixMask = (1UL << levelShift) - 1;
    readonly ulong _key;
    GeoCell(ulong key) => _key = key;

    public GeoCell(int level, ulong prefix) {
        checkLevel(level);
        if ((prefix >> (2 * level)) != 0) throw new ArgumentOutOfRangeException(nameof(prefix), "The prefix has more bits than a cell of level " + level + " holds. ");
        _key = ((ulong)level << levelShift) | prefix;
    }
    static void checkLevel(int level) {
        if (level < 0 || level > MaxLevel) throw new ArgumentOutOfRangeException(nameof(level), "A cell's level is 0 to " + MaxLevel + ". ");
    }

    /// <summary>The cell of the given level a position is in.</summary>
    public static GeoCell Of(GeoCoordinate position, int level) {
        if (position.IsEmpty) throw new ArgumentException("An empty position is in no cell. ", nameof(position));
        checkLevel(level);
        var code = position.StorageValue - 1;
        return new GeoCell(((ulong)level << levelShift) | (code >> (62 - 2 * level)));
    }
    /// <summary>The cell a <see cref="Key"/> stands for.</summary>
    public static GeoCell FromKey(ulong key) {
        var level = (int)(key >> levelShift);
        checkLevel(level);
        if (((key & prefixMask) >> (2 * level)) != 0) throw new ArgumentOutOfRangeException(nameof(key), "Not a cell key. ");
        return new GeoCell(key);
    }
    public static bool TryFromKey(ulong key, out GeoCell cell) {
        var level = (int)(key >> levelShift);
        cell = default;
        if (level > MaxLevel || ((key & prefixMask) >> (2 * level)) != 0) return false;
        cell = new GeoCell(key);
        return true;
    }

    /// <summary>The level and the prefix in one number: unique per cell, and what a cell is stored as.</summary>
    public ulong Key => _key;
    public int Level => (int)(_key >> levelShift);
    /// <summary>The top 2 x <see cref="Level"/> bits of the Morton code of every position inside the cell.</summary>
    public ulong Prefix => _key & prefixMask;
    public GeoCell Parent => Level == 0 ? this : new GeoCell(Level - 1, Prefix >> 2);
    /// <summary>The cell of a coarser (or the same) level this one is inside.</summary>
    public GeoCell AncestorAt(int level) {
        if (level > Level || level < 0) throw new ArgumentOutOfRangeException(nameof(level), "An ancestor is at the cell's own level or coarser. ");
        return new GeoCell(level, Prefix >> (2 * (Level - level)));
    }
    /// <summary>Which row of its level the cell is in, counted from the south pole.</summary>
    public uint Row => GeoCode.Compact(Prefix);
    /// <summary>Which column of its level the cell is in, counted east from the antimeridian.</summary>
    public uint Column => GeoCode.Compact(Prefix >> 1);
    /// <summary>The cell of a level at a row and a column: 2^level of each.</summary>
    public static GeoCell At(int level, uint row, uint column) {
        checkLevel(level);
        var size = 1UL << level;
        if (row >= size || column >= size) throw new ArgumentOutOfRangeException(nameof(row), "No such row or column at level " + level + ". ");
        return new GeoCell(level, GeoCode.Interleave(row, column));
    }
    /// <summary>
    /// The cell so many rows north and columns east of this one, at the same level. Columns wrap round
    /// the antimeridian; there is nothing past a pole, which is null.
    /// </summary>
    public GeoCell? Neighbour(int rowsNorth, int columnsEast) {
        var size = 1L << Level;
        var row = Row + (long)rowsNorth;
        if (row < 0 || row >= size) return null;
        var column = ((Column + (long)columnsEast) % size + size) % size;
        return At(Level, (uint)row, (uint)column);
    }
    /// <summary>The four cells of the next level that make up this one; none at the finest level.</summary>
    public GeoCell[] Children() {
        if (Level >= MaxLevel) return [];
        var p = Prefix << 2;
        var l = Level + 1;
        return [new GeoCell(l, p), new GeoCell(l, p | 1), new GeoCell(l, p | 2), new GeoCell(l, p | 3)];
    }
    public bool Contains(GeoCell other) => other.Level >= Level && other.AncestorAt(Level)._key == _key;
    public bool Contains(GeoCoordinate position) => !position.IsEmpty && Of(position, Level)._key == _key;

    /// <summary>The lowest code of the 62-bit grid inside the cell: a cell and every cell inside it begin
    /// at or after it and end at or before <see cref="LastCode"/>.</summary>
    public ulong FirstCode => Prefix << (62 - 2 * Level);
    public ulong LastCode => FirstCode | ((1UL << (62 - 2 * Level)) - 1);

    public double HeightDegrees => 180.0 / (1L << Level);
    public double WidthDegrees => 360.0 / (1L << Level);
    public double South => GeoCode.Compact(Prefix) * HeightDegrees - 90;
    public double North => South + HeightDegrees;
    public double West => GeoCode.Compact(Prefix >> 1) * WidthDegrees - 180;
    public double East => West + WidthDegrees;
    public double CenterLatitude => South + HeightDegrees / 2;
    public double CenterLongitude => West + WidthDegrees / 2;
    public GeoCoordinate Center => new(CenterLatitude, CenterLongitude);
    /// <summary>How tall the cell is on the ground, the same at every latitude.</summary>
    public double HeightMeters => Math.PI * GeoMoments.EarthRadiusMeters / (1L << Level);
    /// <summary>The cell's area on the sphere.</summary>
    public double AreaSquareMeters {
        get {
            const double r = GeoMoments.EarthRadiusMeters;
            var south = South * Math.PI / 180;
            var north = North * Math.PI / 180;
            return r * r * (WidthDegrees * Math.PI / 180) * (Math.Sin(north) - Math.Sin(south));
        }
    }

    /// <summary>The coarsest level whose cells are no taller than the given distance.</summary>
    public static int LevelForMeters(double meters) {
        if (!(meters > 0)) return MaxLevel;
        for (var level = 0; level <= MaxLevel; level++) {
            if (Math.PI * GeoMoments.EarthRadiusMeters / (1L << level) <= meters) return level;
        }
        return MaxLevel;
    }

    public override string ToString() => Level.ToString(CultureInfo.InvariantCulture) + "/" + Prefix.ToString("x", CultureInfo.InvariantCulture);
    public static bool TryParse(string? text, out GeoCell cell) {
        cell = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var slash = text.IndexOf('/');
        if (slash <= 0) return false;
        if (!int.TryParse(text.AsSpan(0, slash), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) || level < 0 || level > MaxLevel) return false;
        if (!ulong.TryParse(text.AsSpan(slash + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var prefix) || (prefix >> (2 * level)) != 0) return false;
        cell = new GeoCell(level, prefix);
        return true;
    }

    public bool Equals(GeoCell other) => _key == other._key;
    public override bool Equals(object? obj) => obj is GeoCell c && Equals(c);
    public override int GetHashCode() => _key.GetHashCode();
    public int CompareTo(GeoCell other) => _key.CompareTo(other._key);
    public static bool operator ==(GeoCell a, GeoCell b) => a._key == b._key;
    public static bool operator !=(GeoCell a, GeoCell b) => a._key != b._key;
}

/// <summary>A cell and how many positions are counted in it.</summary>
public readonly record struct GeoCellCount(GeoCell Cell, long Count);

/// <summary>
/// Counts per cell, kept within a budget of cells without losing a single count.
///
/// A heatmap kept at a fine level has as many cells as there are places, and a statistic keeping one
/// per interval cannot hold that. Keeping the busiest cells and dropping the rest (what a count per
/// value does) loses the countryside to the city. Instead the cells are merged into coarser ones
/// where there is little in them and kept fine where there is a lot: <see cref="Compress"/> starts
/// from the one cell holding the whole world and keeps splitting the heaviest cell it has into its
/// four children, for as long as the budget allows. A cell holding a single busy spot splits for
/// free (one child replaces it), so a lone hotspot goes all the way down to the finest level.
///
/// Counts of different levels may cover the same ground - a coarse cell from an interval that was
/// compressed, and fine ones recorded since, or two intervals merged - and a coarse count is then
/// shared among the finer cells inside it in proportion to what they hold: the assumption is that
/// what was counted coarsely lay where the rest of it lies. The total is kept exact.
/// </summary>
public static class GeoCellCounts {
    /// <summary>
    /// At most <paramref name="budget"/> cells, none overlapping another, holding exactly the total
    /// the given counts hold. Counts for the same cell are added up; counts of zero or less are left out.
    /// The result is in the order of the cells' codes.
    /// </summary>
    public static List<GeoCellCount> Compress(IEnumerable<GeoCellCount> counts, int budget) {
        budget = Math.Max(1, budget);
        var byKey = new Dictionary<ulong, long>();
        foreach (var c in counts) {
            if (c.Count <= 0) continue;
            byKey[c.Cell.Key] = byKey.TryGetValue(c.Cell.Key, out var had) ? had + c.Count : c.Count;
        }
        if (byKey.Count == 0) return [];
        // in code order, and a coarse cell before the finer ones that begin where it begins: a
        // quadtree node's members are then one run of this list, with the ones covering the whole
        // node at the front of it
        var items = byKey.Select(kv => GeoCell.FromKey(kv.Key)).Select(c => (Cell: c, First: c.FirstCode, Count: byKey[c.Key])).ToArray();
        Array.Sort(items, (a, b) => a.First != b.First ? a.First.CompareTo(b.First) : a.Cell.Level.CompareTo(b.Cell.Level));
        // nothing to merge and nothing overlapping: handed back as it is
        if (items.Length <= budget && disjoint(items)) return [.. items.Select(i => new GeoCellCount(i.Cell, i.Count))];

        var sums = new long[items.Length + 1];
        for (var i = 0; i < items.Length; i++) sums[i + 1] = sums[i] + items[i].Count;
        long total = sums[^1];

        var final = new List<(GeoCell Cell, double Mass)>();
        // the heaviest node first: splitting it is what refines the picture where there is the most to see
        var heap = new PriorityQueue<Node, double>();
        var root = new Node(new GeoCell(0, 0), 0, items.Length, 0);
        heap.Enqueue(root, -root.Mass(sums));
        var nodes = 1;
        var children = new Node[4];
        while (heap.TryDequeue(out var node, out _)) {
            var level = node.Cell.Level;
            var start = node.Start;
            // the members that cover the whole node: counted at this node's level or coarser
            double residual = node.Inherited;
            while (start < node.End && items[start].Cell.Level <= level) residual += items[start++].Count;
            if (start >= node.End || level >= GeoCell.MaxLevel) {
                final.Add((node.Cell, residual + (sums[node.End] - sums[start])));
                continue;
            }
            // the four children, each a run of what is left: the two bits below the node's prefix
            var shift = 62 - 2 * (level + 1);
            var found = 0;
            var i = start;
            while (i < node.End) {
                var quadrant = (items[i].First >> shift) & 3;
                var j = i;
                while (j < node.End && ((items[j].First >> shift) & 3) == quadrant) j++;
                children[found++] = new Node(new GeoCell(level + 1, (node.Cell.Prefix << 2) | quadrant), i, j, 0);
                i = j;
            }
            if (nodes + found - 1 > budget) {
                final.Add((node.Cell, residual + (sums[node.End] - sums[start])));
                continue;
            }
            nodes += found - 1;
            // what was counted for the whole node goes to its children the way the rest of it lies
            double own = sums[node.End] - sums[start];
            for (var k = 0; k < found; k++) {
                var child = children[k];
                double mass = sums[child.End] - sums[child.Start];
                var next = child with { Inherited = own > 0 ? residual * mass / own : residual / found };
                heap.Enqueue(next, -next.Mass(sums));
            }
        }
        return roundExactly(final, total);
    }

    readonly record struct Node(GeoCell Cell, int Start, int End, double Inherited) {
        public double Mass(long[] sums) => Inherited + (sums[End] - sums[Start]);
    }

    static bool disjoint((GeoCell Cell, ulong First, long Count)[] sorted) {
        ulong reached = 0;
        var any = false;
        foreach (var item in sorted) {
            if (any && item.First <= reached) return false;
            reached = item.Cell.LastCode;
            any = true;
        }
        return true;
    }

    // Masses shared out in proportion are fractions; the counts handed back are whole numbers adding
    // up to the total exactly, the remainders going to the cells with the largest fractions of one.
    static List<GeoCellCount> roundExactly(List<(GeoCell Cell, double Mass)> cells, long total) {
        var floors = new long[cells.Count];
        long sum = 0;
        for (var i = 0; i < cells.Count; i++) {
            floors[i] = (long)Math.Floor(cells[i].Mass);
            sum += floors[i];
        }
        var left = total - sum;
        if (left > 0) {
            var order = Enumerable.Range(0, cells.Count).OrderByDescending(i => cells[i].Mass - floors[i]).ToArray();
            for (var k = 0; k < order.Length && left > 0; k++, left--) floors[order[k]]++;
        }
        var result = new List<GeoCellCount>(cells.Count);
        for (var i = 0; i < cells.Count; i++) if (floors[i] > 0) result.Add(new GeoCellCount(cells[i].Cell, floors[i]));
        result.Sort((a, b) => a.Cell.FirstCode.CompareTo(b.Cell.FirstCode));
        return result;
    }
}
