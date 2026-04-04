namespace TransitTracker.Infrastructure.Redis;

public static class RedisKeys
{
    public static string Vehicle(string id) => $"vehicle:{id}";
    public static string Line(string id) => $"line:{id}";
    public static string Station(string id) => $"station:{id}";
    public static string Eta(string vehicleId, string stationId) => $"eta:{vehicleId}:{stationId}";
}
