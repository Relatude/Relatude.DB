namespace Relatude.DB.Common;

/// <summary>
/// Spatial search support for <see cref="GeoCoordinate"/>: computes the Z-order (Morton) value
/// ranges a sorted coordinate index must scan to find every candidate within a radius.
/// </summary>
public static class GeoSpatial {
    /// <summary>
    /// Inclusive, ascending, non-overlapping coordinate ranges that together contain every stored
    /// (grid-snapped) coordinate within radiusMeters of center. The cover over-scans (Z-order
    /// cells are square, circles are not), so candidates must be refined with
    /// <see cref="GeoCoordinate.DistanceTo"/>. Empty center or negative radius gives no ranges.
    /// </summary>
    public static List<(GeoCoordinate From, GeoCoordinate To)> CoverRadius(GeoCoordinate center, double radiusMeters) {
        if (center.IsEmpty || radiusMeters < 0 || double.IsNaN(radiusMeters)) return [];
        var codeRanges = GeoCode.CoverRadius(center.StorageValue - 1, radiusMeters);
        var result = new List<(GeoCoordinate, GeoCoordinate)>(codeRanges.Count);
        foreach (var (from, to) in codeRanges) result.Add((GeoCoordinate.FromStorageValue(from + 1), GeoCoordinate.FromStorageValue(to + 1)));
        return result;
    }
}

/// <summary>
/// The 62-bit Morton (Z-order) coordinate code: 31 bits per axis, latitude on even bits,
/// longitude on odd bits. Grid cells are ~9 mm (lat) x ~19 mm (lon at the equator); coordinates
/// snap to cell centers so encode/decode round-trips exactly.
/// </summary>
internal static class GeoCode {
    internal const int BitsPerAxis = 31;
    internal const uint MaxAxisIndex = (1u << BitsPerAxis) - 1;
    internal const ulong MaxCode = (1UL << (2 * BitsPerAxis)) - 1;
    const double EarthRadiusMeters = 6371000.0; // mean radius; must match between cover and distance
    const double DegToRad = Math.PI / 180.0;
    static readonly double LatScale = (1UL << BitsPerAxis) / 180.0; // cells per degree
    static readonly double LonScale = (1UL << BitsPerAxis) / 360.0;

    internal static ulong Encode(double latitude, double longitude) => Interleave(LatIndex(latitude), LonIndex(longitude));
    internal static uint LatIndex(double latitude) {
        if (latitude < -90.0) latitude = -90.0; else if (latitude > 90.0) latitude = 90.0;
        var u = (ulong)((latitude + 90.0) * LatScale);
        return u > MaxAxisIndex ? MaxAxisIndex : (uint)u;
    }
    internal static uint LonIndex(double longitude) {
        longitude -= Math.Floor((longitude + 180.0) / 360.0) * 360.0; // wrap to [-180, 180)
        var u = (ulong)((longitude + 180.0) * LonScale);
        return u > MaxAxisIndex ? MaxAxisIndex : (uint)u;
    }
    // box edges must clamp, never wrap: an upper edge of exactly +180 must stay at the top cell
    static uint lonIndexClamped(double longitude) {
        if (longitude < -180.0) longitude = -180.0; else if (longitude > 180.0) longitude = 180.0;
        var u = (ulong)((longitude + 180.0) * LonScale);
        return u > MaxAxisIndex ? MaxAxisIndex : (uint)u;
    }
    internal static double DecodeLatitude(ulong code) => (Compact(code) + 0.5) / LatScale - 90.0;
    internal static double DecodeLongitude(ulong code) => (Compact(code >> 1) + 0.5) / LonScale - 180.0;

    internal static ulong Interleave(uint latIndex, uint lonIndex) => Spread(latIndex) | (Spread(lonIndex) << 1);
    internal static ulong Spread(uint v) { // spaces the 32 bits of v onto the even bits of a ulong
        ulong x = v;
        x = (x | (x << 16)) & 0x0000FFFF0000FFFF;
        x = (x | (x << 8)) & 0x00FF00FF00FF00FF;
        x = (x | (x << 4)) & 0x0F0F0F0F0F0F0F0F;
        x = (x | (x << 2)) & 0x3333333333333333;
        x = (x | (x << 1)) & 0x5555555555555555;
        return x;
    }
    internal static uint Compact(ulong x) { // inverse of Spread (reads the even bits)
        x &= 0x5555555555555555;
        x = (x | (x >> 1)) & 0x3333333333333333;
        x = (x | (x >> 2)) & 0x0F0F0F0F0F0F0F0F;
        x = (x | (x >> 4)) & 0x00FF00FF00FF00FF;
        x = (x | (x >> 8)) & 0x0000FFFF0000FFFF;
        x = (x | (x >> 16)) & 0x00000000FFFFFFFF;
        return (uint)x;
    }

    internal static double DistanceMeters(ulong codeA, ulong codeB) {
        if (codeA == codeB) return 0;
        var latA = DecodeLatitude(codeA) * DegToRad;
        var latB = DecodeLatitude(codeB) * DegToRad;
        var dLat = latB - latA;
        var dLon = (DecodeLongitude(codeB) - DecodeLongitude(codeA)) * DegToRad;
        var sinLat = Math.Sin(dLat / 2);
        var sinLon = Math.Sin(dLon / 2);
        var a = sinLat * sinLat + Math.Cos(latA) * Math.Cos(latB) * sinLon * sinLon;
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    /// <summary>
    /// Z-order code ranges (inclusive, ascending, merged) covering every grid cell whose center
    /// can lie within radiusMeters of the (snapped) center coordinate. Uses the exact bounding
    /// box of a spherical cap, split at the antimeridian and widened to the full longitude
    /// circle when the cap touches a pole.
    /// </summary>
    internal static List<(ulong From, ulong To)> CoverRadius(ulong centerCode, double radiusMeters) {
        var ranges = new List<(ulong From, ulong To)>();
        if (radiusMeters < 0 || double.IsNaN(radiusMeters)) return ranges;
        var c = radiusMeters / EarthRadiusMeters; // angular radius in radians
        if (c >= Math.PI) { ranges.Add((0, MaxCode)); return ranges; } // covers the whole planet
        var latC = DecodeLatitude(centerCode) * DegToRad;
        var lonC = DecodeLongitude(centerCode) * DegToRad;
        var latMin = latC - c;
        var latMax = latC + c;
        const double halfPi = Math.PI / 2;
        if (latMin <= -halfPi + 1e-12 || latMax >= halfPi - 1e-12) {
            // the cap touches a pole: every longitude is in reach over the clamped latitude band
            addBoxRanges(Math.Max(latMin, -halfPi), Math.Min(latMax, halfPi), -Math.PI, Math.PI, ranges);
        } else {
            // exact longitude extent of a spherical cap; the pole guard above ensures sin(c) < cos(latC)
            var deltaLon = Math.Asin(Math.Min(1.0, Math.Sin(c) / Math.Cos(latC)));
            var lonMin = lonC - deltaLon;
            var lonMax = lonC + deltaLon;
            if (lonMin < -Math.PI) { // wraps across the antimeridian on the west side
                addBoxRanges(latMin, latMax, lonMin + 2 * Math.PI, Math.PI, ranges);
                addBoxRanges(latMin, latMax, -Math.PI, lonMax, ranges);
            } else if (lonMax > Math.PI) { // wraps on the east side
                addBoxRanges(latMin, latMax, lonMin, Math.PI, ranges);
                addBoxRanges(latMin, latMax, -Math.PI, lonMax - 2 * Math.PI, ranges);
            } else {
                addBoxRanges(latMin, latMax, lonMin, lonMax, ranges);
            }
        }
        // each box emits in ascending code order, but two boxes may interleave: sort, then merge
        ranges.Sort((x, y) => x.From.CompareTo(y.From));
        var merged = new List<(ulong From, ulong To)>(ranges.Count);
        foreach (var r in ranges) {
            if (merged.Count > 0 && r.From <= merged[^1].To + 1) { // To is at most MaxCode, so +1 cannot overflow
                if (r.To > merged[^1].To) merged[^1] = (merged[^1].From, r.To);
            } else {
                merged.Add(r);
            }
        }
        return merged;
    }

    // Merged ranges per box are capped: square Z-order cells cannot cover extreme wide-thin
    // boxes (a pole-touching cap spans every longitude) at fine granularity without exploding
    // into thousands of ranges, so the cover uses the finest level that stays within budget and
    // accepts coarser cells (more over-scan, filtered out by distance refinement) instead.
    const int RangeBudgetPerBox = 64;

    // converts a lat/lon box (radians, lonMin <= lonMax, no wrapping) into Z-order cell ranges
    static void addBoxRanges(double latMinRad, double latMaxRad, double lonMinRad, double lonMaxRad, List<(ulong From, ulong To)> ranges) {
        const double radToDeg = 180.0 / Math.PI;
        var latLo = LatIndex(latMinRad * radToDeg);
        var latHi = LatIndex(latMaxRad * radToDeg);
        var lonLo = lonIndexClamped(lonMinRad * radToDeg);
        var lonHi = lonIndexClamped(lonMaxRad * radToDeg);
        // iterative deepening: each level subdivides only boundary cells (interior cells emit as
        // single coarse ranges), keeping the finest cover whose merged range count fits the budget
        List<(ulong From, ulong To)>? best = null;
        for (var maxLevel = 0; maxLevel <= BitsPerAxis; maxLevel++) {
            var attempt = new List<(ulong From, ulong To)>();
            var exact = descend(0, 0, 0, maxLevel, latLo, latHi, lonLo, lonHi, attempt);
            if (attempt.Count > RangeBudgetPerBox && best != null) break; // keep the previous, coarser cover
            best = attempt;
            if (exact) break; // no emission was cut short by the level cap - deeper levels change nothing
        }
        ranges.AddRange(best!);
    }
    // emits the cell ranges of one quadtree cell in ascending code order, merging adjacent ranges
    // inline; returns false when any emission was forced by the level cap (the cover is inexact)
    // or the budget overflowed (the attempt will be discarded)
    static bool descend(int level, uint latPrefix, uint lonPrefix, int maxLevel, uint latLo, uint latHi, uint lonLo, uint lonHi, List<(ulong From, ulong To)> ranges) {
        if (ranges.Count > RangeBudgetPerBox) return false; // attempt already failed - unwind cheaply
        var shift = BitsPerAxis - level;
        var cellLatLo = latPrefix << shift;
        var cellLatHi = cellLatLo | (uint)((1UL << shift) - 1);
        var cellLonLo = lonPrefix << shift;
        var cellLonHi = cellLonLo | (uint)((1UL << shift) - 1);
        if (cellLatLo > latHi || cellLatHi < latLo || cellLonLo > lonHi || cellLonHi < lonLo) return true; // disjoint
        var contained = cellLatLo >= latLo && cellLatHi <= latHi && cellLonLo >= lonLo && cellLonHi <= lonHi;
        if (contained || level == maxLevel) {
            var from = Interleave(cellLatLo, cellLonLo); // an aligned square cell is one contiguous code range
            var to = from | ((1UL << (2 * shift)) - 1);
            if (ranges.Count > 0 && from <= ranges[^1].To + 1) ranges[^1] = (ranges[^1].From, Math.Max(ranges[^1].To, to));
            else ranges.Add((from, to));
            return contained;
        }
        // children in ascending code order: at each bit pair the code is latBit + 2 * lonBit;
        // all four must run (no short-circuit), a partial cover of a cell is never usable
        var exact = descend(level + 1, latPrefix << 1, lonPrefix << 1, maxLevel, latLo, latHi, lonLo, lonHi, ranges);
        exact &= descend(level + 1, (latPrefix << 1) | 1, lonPrefix << 1, maxLevel, latLo, latHi, lonLo, lonHi, ranges);
        exact &= descend(level + 1, latPrefix << 1, (lonPrefix << 1) | 1, maxLevel, latLo, latHi, lonLo, lonHi, ranges);
        exact &= descend(level + 1, (latPrefix << 1) | 1, (lonPrefix << 1) | 1, maxLevel, latLo, latHi, lonLo, lonHi, ranges);
        return exact;
    }
}
