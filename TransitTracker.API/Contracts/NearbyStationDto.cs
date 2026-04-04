namespace TransitTracker.API.Contracts;

public sealed class NearbyStationDto
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double DistanceMeters { get; set; }
}
