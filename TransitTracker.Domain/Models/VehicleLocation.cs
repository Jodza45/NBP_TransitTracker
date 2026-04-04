namespace TransitTracker.Domain.Models;

public sealed class VehicleLocation
{
    public required string VehicleId { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
