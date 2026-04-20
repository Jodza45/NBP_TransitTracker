using TransitTracker.Domain.Models;

namespace TransitTracker.Infrastructure.Services;

public sealed class EtaCalculationService
{
    private const double EarthRadiusKm = 6371;

    public int CalculateSeconds(VehicleLocation location, Station station, VehicleType type)
    {
        var distanceMeters = CalculateDistanceMeters(location.Latitude, location.Longitude, station.Latitude, station.Longitude);
        return CalculateSecondsFromMeters(distanceMeters, type);
    }

    public int CalculateSecondsFromMeters(double distanceMeters, VehicleType type)
    {
        var speedMetersPerSecond = type == VehicleType.Bus ? 8.33d : 11.11d;
        return (int)Math.Max(0, Math.Round(distanceMeters / speedMetersPerSecond, MidpointRounding.AwayFromZero));
    }

    public double CalculateDistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);

        var a = Math.Pow(Math.Sin(dLat / 2), 2)
                + Math.Cos(DegreesToRadians(lat1))
                * Math.Cos(DegreesToRadians(lat2))
                * Math.Pow(Math.Sin(dLon / 2), 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusKm * c * 1000d;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
}
