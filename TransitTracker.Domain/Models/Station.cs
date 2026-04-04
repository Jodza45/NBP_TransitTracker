namespace TransitTracker.Domain.Models;

public sealed class Station
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}
