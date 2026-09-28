using Relatude.DB.Common;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// Made-up positions for trying a log out (custom-logs-sample): places drawn round the towns people
/// live in, weighted the way a Norwegian log would be, and - for a log that also names what moved - a
/// fleet driving round its home towns by day, stopping now and then, parked at night, with the odd GPS
/// fix kilometres off to give the analysis a jump to find.
/// </summary>
sealed class SampleFleet {
    // where the entries of a made-up log happen: a town, how much of it, and how far it reaches (km)
    static readonly (double Lat, double Lon, double Weight, double SpreadKm)[] towns = [
        (59.9139, 10.7522, 30, 6), // Oslo
        (60.3913, 5.3221, 12, 4), // Bergen
        (63.4305, 10.3951, 9, 4), // Trondheim
        (58.9700, 5.7331, 8, 4), // Stavanger
        (69.6496, 18.9560, 4, 2.5), // Tromsø
        (58.1467, 7.9956, 4, 3), // Kristiansand
        (59.7441, 10.2045, 3, 2), // Drammen
        (59.2181, 10.9298, 3, 2), // Fredrikstad
        (67.2804, 14.4049, 2, 2), // Bodø
        (62.4722, 6.1549, 2, 2), // Ålesund
        (61.1153, 10.4662, 2, 1.5), // Lillehammer
        (60.7945, 11.0680, 2, 1.5), // Hamar
        (59.3293, 18.0686, 5, 6), // Stockholm
        (55.6761, 12.5683, 5, 5), // Copenhagen
        (60.1699, 24.9384, 2, 4), // Helsinki
        (51.5072, -0.1276, 3, 8), // London
        (52.5200, 13.4050, 2, 6), // Berlin
    ];
    static readonly double totalWeight = towns.Sum(t => t.Weight);

    /// <summary>A place in or round one of the towns, now and then anywhere in Europe.</summary>
    public static GeoCoordinate Place(Random r) {
        if (r.NextDouble() < 0.02) return new GeoCoordinate(36 + r.NextDouble() * 34, -10 + r.NextDouble() * 40);
        var town = pick(r);
        return scatter(r, town.Lat, town.Lon, town.SpreadKm * 1000);
    }
    static (double Lat, double Lon, double Weight, double SpreadKm) pick(Random r) {
        var at = r.NextDouble() * totalWeight;
        foreach (var t in towns) {
            at -= t.Weight;
            if (at <= 0) return t;
        }
        return towns[0];
    }
    static GeoCoordinate scatter(Random r, double lat, double lon, double sigmaMeters) {
        var north = gaussian(r) * sigmaMeters;
        var east = gaussian(r) * sigmaMeters;
        return move(lat, lon, north, east);
    }
    static GeoCoordinate move(double lat, double lon, double northMeters, double eastMeters) {
        const double r = GeoMoments.EarthRadiusMeters;
        var newLat = Math.Clamp(lat + northMeters / r * 180 / Math.PI, -89.9, 89.9);
        var newLon = lon + eastMeters / (r * Math.Cos(lat * Math.PI / 180)) * 180 / Math.PI;
        newLon = ((newLon + 180) % 360 + 360) % 360 - 180;
        return new GeoCoordinate(newLat, newLon);
    }
    static double gaussian(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

    sealed class Vehicle {
        public required string Name;
        public required double HomeLat, HomeLon, ReachMeters;
        public double Lat, Lon, Heading, Speed;
        public DateTime Clock;
        public DateTime StoppedUntil;
    }
    readonly List<Vehicle> _vehicles = [];

    public SampleFleet(Random r) {
        var count = 8 + r.Next(9);
        for (var i = 0; i < count; i++) {
            var home = pick(r);
            var start = scatter(r, home.Lat, home.Lon, home.SpreadKm * 1000);
            _vehicles.Add(new Vehicle {
                Name = "unit-" + (i + 1).ToString("00", System.Globalization.CultureInfo.InvariantCulture),
                HomeLat = home.Lat,
                HomeLon = home.Lon,
                ReachMeters = home.SpreadKm * 1000 * 6,
                Lat = start.Latitude,
                Lon = start.Longitude,
                Heading = r.NextDouble() * 2 * Math.PI,
                Speed = 6 + r.NextDouble() * 10,
                Clock = DateTime.MinValue,
            });
        }
    }

    /// <summary>The next position recorded at time <paramref name="t"/> (times come in order): which vehicle, and where it is.</summary>
    public (string Mover, GeoCoordinate Position) Next(Random r, DateTime t) {
        var v = _vehicles[r.Next(_vehicles.Count)];
        if (v.Clock == DateTime.MinValue) v.Clock = t;
        var seconds = Math.Max(0, (t - v.Clock).TotalSeconds);
        v.Clock = t;
        var hour = t.Hour + t.Minute / 60.0;
        var night = hour < 6 || hour >= 22;
        if (!night && t >= v.StoppedUntil) {
            // driving: the heading wanders, and turns for home once it has come too far out
            var distance = v.Speed * Math.Min(seconds, 3600);
            var fromHome = new GeoCoordinate(v.Lat, v.Lon).DistanceTo(new GeoCoordinate(v.HomeLat, v.HomeLon));
            if (fromHome > v.ReachMeters) {
                v.Heading = Math.Atan2(v.HomeLon - v.Lon, v.HomeLat - v.Lat) + gaussian(r) * 0.3;
            } else {
                v.Heading += gaussian(r) * 0.5;
            }
            var moved = move(v.Lat, v.Lon, Math.Cos(v.Heading) * distance, Math.Sin(v.Heading) * distance);
            v.Lat = moved.Latitude;
            v.Lon = moved.Longitude;
            v.Speed = Math.Clamp(v.Speed + gaussian(r) * 1.5, 2, 30);
            // now and then it stops: a delivery, a lunch
            if (r.NextDouble() < 0.06) v.StoppedUntil = t.AddMinutes(5 + r.NextDouble() * 40);
        }
        // a fix is a few metres off, and one in three hundred is off by hundreds of kilometres: a jump
        // no vehicle makes, however far apart its fixes are
        var position = r.NextDouble() < 0.003
            ? move(v.Lat, v.Lon, gaussian(r) * 400_000, gaussian(r) * 400_000)
            : move(v.Lat, v.Lon, gaussian(r) * 5, gaussian(r) * 5);
        return (v.Name, position);
    }
}
