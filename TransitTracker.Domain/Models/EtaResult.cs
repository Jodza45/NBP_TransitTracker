namespace TransitTracker.Domain.Models;

public sealed class EtaResult
{
    public required string VehicleId { get; set; }
    public required string StationId { get; set; }
    public int EstimatedSeconds { get; set; }
}
