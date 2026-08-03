using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relatude.DB.Common;

/// <summary>
/// A WGS84 GPS coordinate. Coordinates snap to a ~1 cm grid on construction (31 bits per axis),
/// so equality, hashing and ordering coincide exactly and every value round-trips losslessly
/// through its 62-bit storage code. The default value is Empty, meaning "no location": it is
/// excluded from spatial indexes, IsWithin never matches it and any distance to it is infinite.
/// Values order along a Morton (Z-order) space filling curve, which keeps value ranges spatially
/// coherent for index scans; that order is not meaningful for user-facing sorting - order by
/// DistanceTo instead.
/// </summary>
[JsonConverter(typeof(GeoCoordinateJsonConverter))]
public readonly struct GeoCoordinate : IEquatable<GeoCoordinate>, IComparable<GeoCoordinate> {
    // the 62-bit Morton code + 1; 0 = Empty, so that default(GeoCoordinate) is the empty value
    readonly ulong _codePlusOne;
    GeoCoordinate(ulong codePlusOne) => _codePlusOne = codePlusOne;
    public GeoCoordinate(double latitude, double longitude) {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude))
            throw new ArgumentException("Latitude and longitude must be finite numbers. ");
        _codePlusOne = GeoCode.Encode(latitude, longitude) + 1;
    }
    public static readonly GeoCoordinate Empty = default;
    public bool IsEmpty => _codePlusOne == 0;
    /// <summary>Latitude in decimal degrees (WGS84), snapped to the grid. NaN when empty.</summary>
    public double Latitude => IsEmpty ? double.NaN : GeoCode.DecodeLatitude(_codePlusOne - 1);
    /// <summary>Longitude in decimal degrees (WGS84), snapped to the grid. NaN when empty.</summary>
    public double Longitude => IsEmpty ? double.NaN : GeoCode.DecodeLongitude(_codePlusOne - 1);
    /// <summary>
    /// Lossless round-trip token and index sort key: the 62-bit Morton code + 1, or 0 for Empty.
    /// </summary>
    public ulong StorageValue => _codePlusOne;
    public static GeoCoordinate FromStorageValue(ulong storageValue) {
        if (storageValue > GeoCode.MaxCode + 1) throw new ArgumentOutOfRangeException(nameof(storageValue), "Not a valid GeoCoordinate storage value. ");
        return new(storageValue);
    }
    public static bool TryFromStorageValue(ulong storageValue, out GeoCoordinate value) {
        if (storageValue > GeoCode.MaxCode + 1) { value = Empty; return false; }
        value = new(storageValue);
        return true;
    }
    /// <summary>Great-circle distance in meters (haversine over the mean Earth radius). Infinite when either value is empty.</summary>
    public double DistanceTo(GeoCoordinate other) {
        if (IsEmpty || other.IsEmpty) return double.PositiveInfinity;
        return GeoCode.DistanceMeters(_codePlusOne - 1, other._codePlusOne - 1);
    }
    /// <summary>True when within the given great-circle distance in meters of center. Recognized in query lambdas, where it is index accelerated.</summary>
    public bool IsWithin(GeoCoordinate center, double meters) => DistanceTo(center) <= meters;
    public int CompareTo(GeoCoordinate other) => _codePlusOne.CompareTo(other._codePlusOne);
    public bool Equals(GeoCoordinate other) => _codePlusOne == other._codePlusOne;
    public override bool Equals(object? obj) => obj is GeoCoordinate g && Equals(g);
    public override int GetHashCode() => _codePlusOne.GetHashCode();
    public static bool operator ==(GeoCoordinate a, GeoCoordinate b) => a.Equals(b);
    public static bool operator !=(GeoCoordinate a, GeoCoordinate b) => !a.Equals(b);
    // 9 decimals is enough for ToString -> TryParse to land in the same grid cell
    public override string ToString() => IsEmpty ? "" :
        Latitude.ToString("0.#########", CultureInfo.InvariantCulture) + ", " + Longitude.ToString("0.#########", CultureInfo.InvariantCulture);
    public static bool TryParse(string? s, out GeoCoordinate value) {
        value = Empty;
        if (string.IsNullOrWhiteSpace(s)) return true; // empty string round-trips the Empty value
        var parts = s.Split(',');
        if (parts.Length != 2) return false;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) return false;
        if (!double.IsFinite(lat) || !double.IsFinite(lon)) return false;
        value = new GeoCoordinate(lat, lon);
        return true;
    }
}

/// <summary>
/// JSON shape: {"latitude": 59.91, "longitude": 10.75} (accepts lat/lon/lng aliases and a
/// "lat, lon" string); Empty serializes as null so it survives a JSON round trip.
/// </summary>
public sealed class GeoCoordinateJsonConverter : JsonConverter<GeoCoordinate> {
    public override bool HandleNull => true;
    public override GeoCoordinate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType == JsonTokenType.Null) return GeoCoordinate.Empty;
        if (reader.TokenType == JsonTokenType.String) {
            if (GeoCoordinate.TryParse(reader.GetString(), out var parsed)) return parsed;
            throw new JsonException("Invalid GeoCoordinate string, expected \"latitude, longitude\". ");
        }
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected an object, string or null for GeoCoordinate. ");
        double? lat = null, lon = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject) {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var name = reader.GetString();
            reader.Read();
            if (string.Equals(name, "latitude", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "lat", StringComparison.OrdinalIgnoreCase)) lat = reader.GetDouble();
            else if (string.Equals(name, "longitude", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "lon", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "lng", StringComparison.OrdinalIgnoreCase)) lon = reader.GetDouble();
            else reader.Skip();
        }
        if (lat == null || lon == null) throw new JsonException("GeoCoordinate requires latitude and longitude. ");
        return new GeoCoordinate(lat.Value, lon.Value);
    }
    public override void Write(Utf8JsonWriter writer, GeoCoordinate value, JsonSerializerOptions options) {
        if (value.IsEmpty) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        writer.WriteNumber("latitude", value.Latitude);
        writer.WriteNumber("longitude", value.Longitude);
        writer.WriteEndObject();
    }
}
