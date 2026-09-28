namespace Relatude.DB.Common;

/// <summary>
/// The centre and the spread of a set of positions, kept as running sums that can be added to one
/// position at a time and merged with another set exactly: two halves merged give what the whole
/// would have given, which is what lets a statistic keep one of these per interval and answer any
/// range from them.
///
/// The positions are taken as points on the unit sphere rather than as degrees. An average of
/// degrees is wrong across the antimeridian (a cluster round Fiji averages to Africa) and bends near
/// the poles; the mean of the unit vectors has neither problem, and its direction is the centre.
/// What is kept is the count, that mean, and the sums of the squared deviations from it (Welford's
/// running form, merged the way Chan et al. merge variances), so the spread stays exact down to a few
/// centimetres however many positions there are - raw sums of squares lose it at city scale.
/// Plus the latitude extent, and the longitude extent in two frames (-180..180 and 0..360), the
/// narrower of which is the bounding box whether or not it crosses the antimeridian.
///
/// Not thread safe: whoever holds one holds the lock that guards it.
/// </summary>
public sealed class GeoMoments {
    /// <summary>The mean radius of the Earth, the same one <see cref="GeoCoordinate.DistanceTo"/> uses.</summary>
    public const double EarthRadiusMeters = 6371000.0;
    const double degToRad = Math.PI / 180.0;
    const double radToDeg = 180.0 / Math.PI;

    public long Count { get; private set; }
    // the mean of the unit vectors
    double _mx, _my, _mz;
    // the sums of the products of the deviations from that mean: the scatter matrix, upper triangle
    double _cxx, _cxy, _cxz, _cyy, _cyz, _czz;
    double _south = double.PositiveInfinity, _north = double.NegativeInfinity;
    // longitudes in -180..180 (A) and in 0..360 (B): a set straddling the antimeridian is narrow in B
    double _westA = double.PositiveInfinity, _eastA = double.NegativeInfinity;
    double _westB = double.PositiveInfinity, _eastB = double.NegativeInfinity;

    /// <summary>Adds one position; an empty one is no position and is left out.</summary>
    public void Add(GeoCoordinate position) {
        if (position.IsEmpty) return;
        Add(position.Latitude, position.Longitude);
    }
    /// <summary>Adds one position given in degrees.</summary>
    public void Add(double latitude, double longitude) {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude)) return;
        UnitVector(latitude, longitude, out var x, out var y, out var z);
        Count++;
        var n = (double)Count;
        var dx = x - _mx;
        var dy = y - _my;
        var dz = z - _mz;
        _mx += dx / n;
        _my += dy / n;
        _mz += dz / n;
        // (u - old mean)(u - new mean)^T, which is (1 - 1/n) d d^T and so symmetric as it stands
        var f = (n - 1) / n;
        _cxx += f * dx * dx;
        _cxy += f * dx * dy;
        _cxz += f * dx * dz;
        _cyy += f * dy * dy;
        _cyz += f * dy * dz;
        _czz += f * dz * dz;
        extend(latitude, longitude);
    }
    void extend(double latitude, double longitude) {
        if (latitude < _south) _south = latitude;
        if (latitude > _north) _north = latitude;
        if (longitude < _westA) _westA = longitude;
        if (longitude > _eastA) _eastA = longitude;
        var b = longitude < 0 ? longitude + 360 : longitude;
        if (b < _westB) _westB = b;
        if (b > _eastB) _eastB = b;
    }

    /// <summary>Adds every position another set holds: exactly what adding them one by one would have given.</summary>
    public void Add(GeoMoments other) {
        if (other.Count == 0) return;
        if (Count == 0) {
            copyFrom(other);
            return;
        }
        var na = (double)Count;
        var nb = (double)other.Count;
        var n = na + nb;
        var dx = other._mx - _mx;
        var dy = other._my - _my;
        var dz = other._mz - _mz;
        var w = na * nb / n;
        _cxx += other._cxx + dx * dx * w;
        _cxy += other._cxy + dx * dy * w;
        _cxz += other._cxz + dx * dz * w;
        _cyy += other._cyy + dy * dy * w;
        _cyz += other._cyz + dy * dz * w;
        _czz += other._czz + dz * dz * w;
        _mx += dx * nb / n;
        _my += dy * nb / n;
        _mz += dz * nb / n;
        Count += other.Count;
        _south = Math.Min(_south, other._south);
        _north = Math.Max(_north, other._north);
        _westA = Math.Min(_westA, other._westA);
        _eastA = Math.Max(_eastA, other._eastA);
        _westB = Math.Min(_westB, other._westB);
        _eastB = Math.Max(_eastB, other._eastB);
    }
    void copyFrom(GeoMoments o) {
        Count = o.Count;
        (_mx, _my, _mz) = (o._mx, o._my, o._mz);
        (_cxx, _cxy, _cxz, _cyy, _cyz, _czz) = (o._cxx, o._cxy, o._cxz, o._cyy, o._cyz, o._czz);
        (_south, _north, _westA, _eastA, _westB, _eastB) = (o._south, o._north, o._westA, o._eastA, o._westB, o._eastB);
    }
    public GeoMoments Clone() {
        var copy = new GeoMoments();
        copy.copyFrom(this);
        return copy;
    }

    /// <summary>
    /// What the sums say: the centre, how far from it the positions lie, the shape of that spread,
    /// and the box they fit in. See <see cref="GeoSpread"/> for what each number is.
    /// </summary>
    public GeoSpread Summarize() {
        if (Count == 0) return GeoSpread.None;
        var length = Math.Sqrt(_mx * _mx + _my * _my + _mz * _mz);
        var (west, east) = longitudeExtent();
        // Positions spread evenly round the whole planet have no centre: their vectors cancel out
        if (length < 1e-12) return new GeoSpread(Count, GeoCoordinate.Empty, 0, double.NaN, double.NaN, double.NaN, double.NaN, _south, _north, west, east);
        var cx = _mx / length;
        var cy = _my / length;
        var cz = _mz / length;
        var lat = Math.Asin(Math.Clamp(cz, -1, 1));
        var lon = Math.Atan2(cy, cx);
        // east and north where the centre is: the plane the spread is measured in
        double ex = -Math.Sin(lon), ey = Math.Cos(lon), ez = 0;
        double nx = -Math.Sin(lat) * Math.Cos(lon), ny = -Math.Sin(lat) * Math.Sin(lon), nz = Math.Cos(lat);
        var n = (double)Count;
        // the covariance of the vectors, seen in that plane: since the mean points at the centre it
        // has no part in the plane, and this is the covariance of the positions as seen from above it
        var a = quad(ex, ey, ez, ex, ey, ez) / n;
        var b = quad(ex, ey, ez, nx, ny, nz) / n;
        var d = quad(nx, ny, nz, nx, ny, nz) / n;
        var half = (a + d) / 2;
        var disc = Math.Sqrt(Math.Max(0, (a - d) * (a - d) / 4 + b * b));
        var major = Math.Max(0, half + disc);
        var minor = Math.Max(0, half - disc);
        // the major axis at theta from east towards north; as a bearing, clockwise from north
        var theta = 0.5 * Math.Atan2(2 * b, a - d);
        var bearing = (90 - theta * radToDeg) % 180;
        if (bearing < 0) bearing += 180;
        return new GeoSpread(
            Count,
            new GeoCoordinate(lat * radToDeg, lon * radToDeg),
            length,
            EarthRadiusMeters * Math.Sqrt(Math.Max(0, a + d)),
            EarthRadiusMeters * Math.Sqrt(major),
            EarthRadiusMeters * Math.Sqrt(minor),
            disc > 1e-30 ? bearing : double.NaN,
            _south, _north, west, east);
    }
    // p^T C q for the scatter matrix C
    double quad(double px, double py, double pz, double qx, double qy, double qz) =>
        px * (_cxx * qx + _cxy * qy + _cxz * qz) +
        py * (_cxy * qx + _cyy * qy + _cyz * qz) +
        pz * (_cxz * qx + _cyz * qy + _czz * qz);
    // the narrower of the two frames; west greater than east means the box crosses the antimeridian
    (double West, double East) longitudeExtent() {
        if (Count == 0) return (double.NaN, double.NaN);
        if (_eastB - _westB < _eastA - _westA) {
            static double back(double x) => x > 180 ? x - 360 : x;
            return (back(_westB), back(_eastB));
        }
        return (_westA, _eastA);
    }

    /// <summary>A place as a point on the unit sphere: x through (0, 0), y through (0, 90 E), z through the north pole.</summary>
    public static void UnitVector(double latitude, double longitude, out double x, out double y, out double z) {
        var phi = latitude * degToRad;
        var lambda = longitude * degToRad;
        var c = Math.Cos(phi);
        x = c * Math.Cos(lambda);
        y = c * Math.Sin(lambda);
        z = Math.Sin(phi);
    }

    // ---- as bytes ----

    const byte formatVersion = 1;
    public void Write(BinaryWriter w) {
        w.Write(formatVersion);
        w.Write(Count);
        if (Count == 0) return;
        double[] values = [_mx, _my, _mz, _cxx, _cxy, _cxz, _cyy, _cyz, _czz, _south, _north, _westA, _eastA, _westB, _eastB];
        foreach (var v in values) w.Write(v);
    }
    public static GeoMoments Read(BinaryReader r) {
        var m = new GeoMoments();
        var version = r.ReadByte();
        if (version != formatVersion) throw new InvalidDataException("Unknown GeoMoments format " + version + ". ");
        m.Count = r.ReadInt64();
        if (m.Count == 0) return m;
        m._mx = r.ReadDouble(); m._my = r.ReadDouble(); m._mz = r.ReadDouble();
        m._cxx = r.ReadDouble(); m._cxy = r.ReadDouble(); m._cxz = r.ReadDouble();
        m._cyy = r.ReadDouble(); m._cyz = r.ReadDouble(); m._czz = r.ReadDouble();
        m._south = r.ReadDouble(); m._north = r.ReadDouble();
        m._westA = r.ReadDouble(); m._eastA = r.ReadDouble();
        m._westB = r.ReadDouble(); m._eastB = r.ReadDouble();
        return m;
    }
}

/// <summary>
/// The centre and the spread of a set of positions (see <see cref="GeoMoments"/>).
/// </summary>
/// <param name="Count">How many positions.</param>
/// <param name="Center">The spherical mean: the direction of the average of the positions as points
/// on the sphere. Empty when there is none (no positions, or positions evenly round the planet).</param>
/// <param name="Concentration">How long that average is, from 0 to 1: 1 when every position is the same
/// place, falling towards 0 as they spread round the planet.</param>
/// <param name="StandardDistanceMeters">The root mean square distance of the positions from the centre,
/// measured in the plane touching the Earth there: how far out a typical position lies.</param>
/// <param name="MajorAxisMeters">One standard deviation along the direction the positions spread most.</param>
/// <param name="MinorAxisMeters">...and across it. Together with the bearing, the standard deviational ellipse.</param>
/// <param name="MajorAxisBearingDegrees">Which way the major axis runs, in degrees clockwise from north, 0 to 180;
/// NaN when the spread is the same every way.</param>
/// <param name="South">The southernmost latitude.</param>
/// <param name="North">The northernmost latitude.</param>
/// <param name="West">The western edge of the smallest longitude span holding every position.</param>
/// <param name="East">Its eastern edge: less than <paramref name="West"/> when the box crosses the antimeridian.</param>
public readonly record struct GeoSpread(
    long Count, GeoCoordinate Center, double Concentration,
    double StandardDistanceMeters, double MajorAxisMeters, double MinorAxisMeters, double MajorAxisBearingDegrees,
    double South, double North, double West, double East) {
    public static readonly GeoSpread None = new(0, GeoCoordinate.Empty, 0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
    public bool HasCenter => !Center.IsEmpty;
}
