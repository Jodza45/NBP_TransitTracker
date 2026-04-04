namespace TransitTracker.Application.DTOs;

public sealed class VehicleLocationDto
{
    public required string VehicleId { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
