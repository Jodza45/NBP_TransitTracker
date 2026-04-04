namespace TransitTracker.Domain.Models;

public sealed class Line
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required List<string> StationIds { get; set; }
}
