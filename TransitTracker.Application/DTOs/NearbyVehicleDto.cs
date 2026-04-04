namespace TransitTracker.Application.DTOs;

public sealed class NearbyVehicleDto
{
    public required string VehicleId { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double DistanceMeters { get; set; }
}
