using TransitTracker.Domain.Models;

namespace TransitTracker.Infrastructure.Services;

public sealed class EtaCalculationService
{
    private const double EarthRadiusKm = 6371;

    public int CalculateSeconds(VehicleLocation location, Station station, VehicleType type)
    {
        var distanceKm = Haversine(location.Latitude, location.Longitude, station.Latitude, station.Longitude);
        var speedKmh = type == VehicleType.Bus ? 30d : 40d;
        var hours = distanceKm / speedKmh;
        var seconds = (int)Math.Max(0, Math.Round(hours * 3600, MidpointRounding.AwayFromZero));
        return seconds;
    }

    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);

        var a = Math.Pow(Math.Sin(dLat / 2), 2)
                + Math.Cos(DegreesToRadians(lat1))
                * Math.Cos(DegreesToRadians(lat2))
                * Math.Pow(Math.Sin(dLon / 2), 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusKm * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
}
