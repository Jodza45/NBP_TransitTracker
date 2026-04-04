namespace TransitTracker.API.Contracts;

public sealed class StationArrivalDto
{
    public required string VehicleId { get; set; }
    public required string LineId { get; set; }
    public int EstimatedSeconds { get; set; }
}
