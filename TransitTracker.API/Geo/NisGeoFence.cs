namespace TransitTracker.API.Geo;

public static class NisGeoFence
{
    public const double MinLatitude = 43.255;
    public const double MaxLatitude = 43.390;
    public const double MinLongitude = 21.780;
    public const double MaxLongitude = 22.020;

    public static bool Contains(double latitude, double longitude)
    {
        return latitude >= MinLatitude
               && latitude <= MaxLatitude
               && longitude >= MinLongitude
               && longitude <= MaxLongitude;
    }
}
