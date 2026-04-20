using System.Globalization;

namespace TransitTracker.Infrastructure.Services;

public static class DemoFleetPlanner
{
    public const string FleetProfileVersion = "nis-city-fleet-v2";

    private static readonly HashSet<string> ThreeVehicleRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "1",
        "2",
        "5",
        "6",
        "34A",
        "34B"
    };

    private static readonly HashSet<string> TwoVehicleRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "3",
        "4",
        "10",
        "12"
    };

    public static int GetVehiclesPerDirection(string routeShortName)
    {
        var normalized = routeShortName.Trim();
        if (ThreeVehicleRoutes.Contains(normalized))
        {
            return 3;
        }

        if (TwoVehicleRoutes.Contains(normalized))
        {
            return 2;
        }

        return 1;
    }

    public static string BuildVehicleId(string lineId, int sequence)
    {
        return string.Create(CultureInfo.InvariantCulture, $"demo-{lineId.Replace(':', '-')}-{sequence}");
    }

    public static TimeSpan GetDepartureOffset(int sequence)
    {
        return TimeSpan.FromMinutes(Math.Max(0, sequence - 1) * 10);
    }

    public static bool TryGetVehicleSequence(string vehicleId, out int sequence)
    {
        sequence = 1;

        var lastDash = vehicleId.LastIndexOf('-');
        if (lastDash < 0 || lastDash == vehicleId.Length - 1)
        {
            return false;
        }

        return int.TryParse(vehicleId[(lastDash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out sequence)
            && sequence > 0;
    }
}
