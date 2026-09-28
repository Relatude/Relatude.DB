using Relatude.DB.Common;
using Relatude.DB.Logging;
using System.Globalization;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// What a column of positions says when its entries are read, rather than its statistics: the
/// Analyse view of a <see cref="LogDataType.GeoCoordinate"/> column (see UICustomLogs.analyse).
///
/// The statistics keep what merges - a centre, a spread, counts per cell - because that is all an
/// interval can hold and still add up with the next one. Read from the entries, anything goes:
///   - the median centre (the geometric median, found by Weiszfeld's iteration): the place the sum of
///     the distances to every position is smallest, which a few far-off positions do not drag away
///     the way they drag the mean;
///   - how far from it the positions are, as percentiles and a histogram: "half within 2 km, nine in
///     ten within 11";
///   - clusters: the crowded cells of a grid sized to the spread, joined where they touch;
///   - tracks, when a column names what moved (a device, a vehicle, a courier): each one's positions in
///     time order, with the distance covered, the speeds, the stops, and the jumps no vehicle makes;
///   - and the positions themselves, for the map, in the same packed form the query map sends them.
/// </summary>
static class UIGeoAnalysis {
    /// <summary>Every position read, with its time and - when asked for - what it belongs to and what it is coloured by.</summary>
    internal sealed class Collected {
        public readonly List<GeoCoordinate> Positions = [];
        public readonly List<DateTime> Times = [];
        public readonly List<string?> Tracks = [];
        public readonly List<string?> Colours = [];
        public void Add(DateTime time, GeoCoordinate position, string? track, string? colour) {
            Positions.Add(position);
            Times.Add(time);
            Tracks.Add(track);
            Colours.Add(colour);
        }
        public int Count => Positions.Count;
    }

    // what a vehicle does not do: faster than an airliner, a GPS jump, left out of the distance
    const double maxSpeedMetersPerSecond = 300;
    // slower than a walk is standing still
    const double movingMetersPerSecond = 0.5;
    // a stop: within this far of where it began, for at least this long
    const double stopRadiusMeters = 100;
    static readonly TimeSpan stopAtLeast = TimeSpan.FromMinutes(5);
    const int maxTracks = 20_000;
    const int topTracks = 25;
    const int maxClusters = 12;
    const int maxColourGroups = 12;
    const int histogramBins = 30;
    // the median is found on at most this many positions: it moves by centimetres beyond that
    const int medianSample = 200_000;

    /// <summary>The analysis, and the positions for the map (at most <paramref name="mapPoints"/> of them, evenly thinned).</summary>
    internal static object Summarize(Collected c, int mapPoints, string? trackBy, string? colourBy) {
        var moments = new GeoMoments();
        foreach (var p in c.Positions) moments.Add(p);
        var spread = moments.Summarize();
        var median = geometricMedian(c.Positions, spread.Center);
        return new {
            c.Count,
            Spread = UILogReader.GeoSummary(spread),
            Median = median.IsEmpty ? null : new { median.Latitude, median.Longitude },
            Distances = distances(c.Positions, median.IsEmpty ? spread.Center : median),
            Clusters = clusters(c.Positions, spread),
            Tracks = trackBy == null ? null : tracks(c, trackBy),
            Points = points(c, mapPoints, colourBy),
        };
    }

    /// <summary>
    /// Weiszfeld's iteration on the sphere: each step is the average of the positions weighted by one
    /// over their distance from the last guess, put back on the surface. It converges on the point
    /// with the least total distance to the positions, which is the centre a median is to a mean.
    /// </summary>
    static GeoCoordinate geometricMedian(List<GeoCoordinate> positions, GeoCoordinate start) {
        if (positions.Count == 0 || start.IsEmpty) return GeoCoordinate.Empty;
        var step = Math.Max(1, positions.Count / medianSample);
        var n = (positions.Count + step - 1) / step;
        var xs = new double[n];
        var ys = new double[n];
        var zs = new double[n];
        for (int i = 0, k = 0; i < positions.Count && k < n; i += step, k++) {
            GeoMoments.UnitVector(positions[i].Latitude, positions[i].Longitude, out xs[k], out ys[k], out zs[k]);
        }
        GeoMoments.UnitVector(start.Latitude, start.Longitude, out var gx, out var gy, out var gz);
        for (var iteration = 0; iteration < 60; iteration++) {
            double sx = 0, sy = 0, sz = 0, sw = 0;
            for (var k = 0; k < n; k++) {
                var dx = xs[k] - gx;
                var dy = ys[k] - gy;
                var dz = zs[k] - gz;
                // a position on the guess itself would weigh without end: a centimetre is near enough
                var d = Math.Max(1.6e-9, Math.Sqrt(dx * dx + dy * dy + dz * dz));
                var w = 1 / d;
                sx += xs[k] * w;
                sy += ys[k] * w;
                sz += zs[k] * w;
                sw += w;
            }
            var length = Math.Sqrt(sx * sx + sy * sy + sz * sz);
            if (length < 1e-15) break;
            var nx = sx / length;
            var ny = sy / length;
            var nz = sz / length;
            var moved = Math.Sqrt((nx - gx) * (nx - gx) + (ny - gy) * (ny - gy) + (nz - gz) * (nz - gz));
            (gx, gy, gz) = (nx, ny, nz);
            if (moved < 1.6e-9) break; // a centimetre
        }
        return new GeoCoordinate(Math.Asin(Math.Clamp(gz, -1, 1)) * 180 / Math.PI, Math.Atan2(gy, gx) * 180 / Math.PI);
    }

    static object? distances(List<GeoCoordinate> positions, GeoCoordinate from) {
        if (positions.Count == 0 || from.IsEmpty) return null;
        var d = new double[positions.Count];
        for (var i = 0; i < d.Length; i++) d[i] = positions[i].DistanceTo(from);
        Array.Sort(d);
        double[] ranks = [10, 25, 50, 75, 90, 95, 99];
        // the bars reach as far as nearly everything, so one far-off position does not squeeze the rest into the first bar
        var reach = percentile(d, 99);
        if (reach <= 0) reach = d[^1];
        var bins = new int[histogramBins];
        var width = reach > 0 ? reach / histogramBins : 1;
        var beyond = 0;
        foreach (var v in d) {
            if (v > reach) {
                beyond++;
                continue;
            }
            bins[Math.Min(histogramBins - 1, (int)(v / width))]++;
        }
        return new {
            From = new { from.Latitude, from.Longitude },
            Mean = d.Average(),
            Max = d[^1],
            Percentiles = ranks.Select(r => new { P = r, Value = percentile(d, r) }).ToArray(),
            Histogram = Enumerable.Range(0, histogramBins).Select(i => new { From = i * width, To = (i + 1) * width, Count = bins[i] }).ToArray(),
            Beyond = beyond,
        };
    }
    static double percentile(double[] sorted, double p) {
        if (sorted.Length == 1) return sorted[0];
        var rank = p / 100 * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = Math.Min(sorted.Length - 1, lower + 1);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
    }

    /// <summary>
    /// Where the positions crowd: a grid of cells a sixth of the spread across (between 30 m and
    /// 50 km), the cells holding at least twice what an occupied cell holds on average, and those
    /// joined into clusters where they touch, corners included. Each cluster is then measured from
    /// its own positions: where its middle is, and how far round it they lie.
    /// </summary>
    static object? clusters(List<GeoCoordinate> positions, GeoSpread spread) {
        if (positions.Count < 3 || !double.IsFinite(spread.StandardDistanceMeters)) return null;
        var cellMeters = Math.Clamp(spread.StandardDistanceMeters / 6, 30, 50_000);
        var level = GeoCell.LevelForMeters(cellMeters);
        var counts = new Dictionary<GeoCell, int>();
        foreach (var p in positions) {
            var cell = GeoCell.Of(p, level);
            counts[cell] = counts.TryGetValue(cell, out var n) ? n + 1 : 1;
        }
        var threshold = Math.Max(3, 2.0 * positions.Count / counts.Count);
        var dense = counts.Where(kv => kv.Value >= threshold).Select(kv => kv.Key).ToHashSet();
        var clusterOf = new Dictionary<GeoCell, int>();
        var next = 0;
        foreach (var start in dense) {
            if (clusterOf.ContainsKey(start)) continue;
            var id = next++;
            var queue = new Queue<GeoCell>();
            queue.Enqueue(start);
            clusterOf[start] = id;
            while (queue.Count > 0) {
                var cell = queue.Dequeue();
                for (var dr = -1; dr <= 1; dr++) {
                    for (var dc = -1; dc <= 1; dc++) {
                        if (dr == 0 && dc == 0) continue;
                        if (cell.Neighbour(dr, dc) is not GeoCell n || !dense.Contains(n) || clusterOf.ContainsKey(n)) continue;
                        clusterOf[n] = id;
                        queue.Enqueue(n);
                    }
                }
            }
        }
        var found = new GeoMoments[next];
        for (var i = 0; i < next; i++) found[i] = new GeoMoments();
        foreach (var p in positions) {
            if (clusterOf.TryGetValue(GeoCell.Of(p, level), out var id)) found[id].Add(p);
        }
        var inClusters = found.Sum(f => f.Count);
        return new {
            CellMeters = GeoCell.At(level, 0, 0).HeightMeters,
            InClusters = inClusters,
            Items = found.Select(f => f.Summarize()).OrderByDescending(s => s.Count).Take(maxClusters).Select(s => new {
                s.Center.Latitude,
                s.Center.Longitude,
                s.Count,
                Share = (double)s.Count / positions.Count,
                Radius = s.StandardDistanceMeters,
                s.South,
                s.North,
                s.West,
                s.East,
            }).ToArray(),
        };
    }

    /// <summary>
    /// The positions of each thing that moved, in time order, and what they add up to. A step faster
    /// than <see cref="maxSpeedMetersPerSecond"/> is a jump (a fix off by kilometres, a device
    /// switched on somewhere else) and is left out of the distance; a stay within
    /// <see cref="stopRadiusMeters"/> of one place for <see cref="stopAtLeast"/> or longer is a stop.
    /// </summary>
    static object tracks(Collected c, string trackBy) {
        var byTrack = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var skipped = 0;
        for (var i = 0; i < c.Count; i++) {
            var name = c.Tracks[i];
            if (string.IsNullOrEmpty(name)) {
                skipped++;
                continue;
            }
            if (!byTrack.TryGetValue(name, out var list)) {
                if (byTrack.Count >= maxTracks) {
                    skipped++;
                    continue;
                }
                byTrack[name] = list = [];
            }
            list.Add(i);
        }
        var results = new List<Track>(byTrack.Count);
        foreach (var (name, indexes) in byTrack) {
            indexes.Sort((a, b) => c.Times[a].CompareTo(c.Times[b]));
            results.Add(measure(name, indexes, c));
        }
        var ordered = results.OrderByDescending(t => t.Distance).ToArray();
        var perTrack = ordered.Select(t => t.Distance).Order().ToArray();
        return new {
            Column = trackBy,
            Tracks = results.Count,
            WithoutName = skipped,
            TotalDistance = results.Sum(t => t.Distance),
            TotalMoving = results.Sum(t => t.Moving.TotalSeconds),
            Stops = results.Sum(t => t.Stops),
            Jumps = results.Sum(t => t.Jumps),
            DistancePercentiles = perTrack.Length == 0 ? [] : new double[] { 10, 25, 50, 75, 90 }.Select(p => new { P = p, Value = percentile(perTrack, p) }).ToArray(),
            Top = ordered.Take(topTracks).Select(t => new {
                t.Name,
                t.Points,
                t.Distance,
                Duration = t.Duration.TotalSeconds,
                Moving = t.Moving.TotalSeconds,
                AverageSpeed = t.Moving.TotalSeconds > 0 ? t.MovingDistance / t.Moving.TotalSeconds : 0,
                t.TopSpeed,
                t.Stops,
                Stopped = t.Stopped.TotalSeconds,
                t.Jumps,
                FirstUtc = UILogReader.Utc(t.First),
                LastUtc = UILogReader.Utc(t.Last),
            }).ToArray(),
        };
    }
    sealed record Track(string Name, int Points, double Distance, double MovingDistance, TimeSpan Duration, TimeSpan Moving, double TopSpeed, int Stops, TimeSpan Stopped, int Jumps, DateTime First, DateTime Last);
    static Track measure(string name, List<int> indexes, Collected c) {
        double distance = 0, movingDistance = 0;
        var moving = TimeSpan.Zero;
        var jumps = 0;
        var speeds = new List<double>();
        GeoCoordinate? last = null;
        DateTime lastTime = default;
        for (var k = 0; k < indexes.Count; k++) {
            var p = c.Positions[indexes[k]];
            var t = c.Times[indexes[k]];
            if (last is GeoCoordinate previous) {
                var d = p.DistanceTo(previous);
                var dt = (t - lastTime).TotalSeconds;
                if (dt > 0) {
                    var speed = d / dt;
                    if (speed > maxSpeedMetersPerSecond) {
                        jumps++;
                    } else {
                        distance += d;
                        speeds.Add(speed);
                        if (speed >= movingMetersPerSecond) {
                            moving += TimeSpan.FromSeconds(dt);
                            movingDistance += d;
                        }
                    }
                } else if (d > 0) {
                    // two places at one moment: one of them is wrong, and neither is a distance travelled
                    jumps++;
                }
            }
            last = p;
            lastTime = t;
        }
        // stops: runs that stay near where they began, long enough
        var stops = 0;
        var stopped = TimeSpan.Zero;
        var runStart = 0;
        for (var k = 1; k <= indexes.Count; k++) {
            var outside = k == indexes.Count || c.Positions[indexes[k]].DistanceTo(c.Positions[indexes[runStart]]) > stopRadiusMeters;
            if (!outside) continue;
            var span = c.Times[indexes[k - 1]] - c.Times[indexes[runStart]];
            if (span >= stopAtLeast) {
                stops++;
                stopped += span;
            }
            runStart = k;
        }
        speeds.Sort();
        // the top speed is the 95th percentile of the steps: the fastest step alone is too often a bad fix
        var top = speeds.Count == 0 ? 0 : speeds[Math.Min(speeds.Count - 1, (int)Math.Floor(speeds.Count * 0.95))];
        var first = c.Times[indexes[0]];
        var lastSeen = c.Times[indexes[^1]];
        return new Track(name, indexes.Count, distance, movingDistance, lastSeen - first, moving, top, stops, stopped, jumps, first, lastSeen);
    }

    /// <summary>
    /// The positions for the map: int32 pairs at ten million to the degree (latitude, longitude),
    /// the seconds since the first as int32, and - when asked - a colour group per position from
    /// another column, its commonest values kept and the rest together as "(other)".
    /// </summary>
    static object points(Collected c, int limit, string? colourBy) {
        var step = Math.Max(1, (c.Count + limit - 1) / Math.Max(1, limit));
        var n = (c.Count + step - 1) / step;
        var coordinates = new byte[n * 8];
        var times = new byte[n * 4];
        var first = c.Count > 0 ? c.Times.Min() : DateTime.UtcNow;
        for (int i = 0, k = 0; i < c.Count && k < n; i += step, k++) {
            BitConverter.TryWriteBytes(coordinates.AsSpan(k * 8), (int)Math.Round(c.Positions[i].Latitude * 1e7));
            BitConverter.TryWriteBytes(coordinates.AsSpan(k * 8 + 4), (int)Math.Round(c.Positions[i].Longitude * 1e7));
            BitConverter.TryWriteBytes(times.AsSpan(k * 4), (int)Math.Clamp((c.Times[i] - first).TotalSeconds, 0, int.MaxValue));
        }
        object? colour = null;
        if (colourBy != null) {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0, k = 0; i < c.Count && k < n; i += step, k++) {
                var v = c.Colours[i];
                if (v == null) continue;
                counts[v] = counts.TryGetValue(v, out var had) ? had + 1 : 1;
            }
            var kept = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(maxColourGroups).Select(kv => kv.Key).ToList();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var g = 0; g < kept.Count; g++) index[kept[g]] = g;
            // which group each position is in, before the two catch-alls are given places: they are
            // groups of their own only when something is in them
            const int otherMark = -1, noneMark = -2;
            var groupOf = new int[n];
            int others = 0, nones = 0;
            for (int i = 0, k = 0; i < c.Count && k < n; i += step, k++) {
                var v = c.Colours[i];
                if (v == null) {
                    groupOf[k] = noneMark;
                    nones++;
                } else if (index.TryGetValue(v, out var g)) {
                    groupOf[k] = g;
                } else {
                    groupOf[k] = otherMark;
                    others++;
                }
            }
            var otherAt = kept.Count;
            var noneAt = kept.Count + (others > 0 ? 1 : 0);
            var assignment = new byte[n * 2];
            for (var k = 0; k < n; k++) {
                var g = groupOf[k] == otherMark ? otherAt : groupOf[k] == noneMark ? noneAt : groupOf[k];
                BitConverter.TryWriteBytes(assignment.AsSpan(k * 2), (ushort)g);
            }
            var groups = kept.Select(k => new { Label = k, Count = counts[k], Kind = "value" }).ToList();
            if (others > 0) groups.Add(new { Label = "(other)", Count = others, Kind = "other" });
            if (nones > 0) groups.Add(new { Label = "(none)", Count = nones, Kind = "none" });
            colour = new {
                Column = colourBy,
                Groups = groups.ToArray(),
                Assignment = assignment,
            };
        }
        return new {
            Count = n,
            Total = c.Count,
            FirstUtc = UILogReader.Utc(first),
            Coordinates = coordinates,
            Times = times,
            Colour = colour,
        };
    }

    /// <summary>A value of another column as the text it is grouped by.</summary>
    internal static string? TextOf(object? value) => value switch {
        null => null,
        string s => s.Length == 0 ? null : s,
        GeoCoordinate g => g.IsEmpty ? null : LogValues.PositionText(g),
        DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        byte[] => null,
        _ => value.ToString(),
    };
}
