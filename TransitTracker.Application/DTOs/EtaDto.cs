namespace TransitTracker.Application.DTOs;

public sealed class EtaDto
{
    public required string VehicleId { get; set; }
    public required string StationId { get; set; }
    public int EstimatedSeconds { get; set; }
}
