namespace TransitTracker.API.Contracts;

public sealed class StationArrivalsResponse
{
    public required string StationId { get; set; }
    public required string StationName { get; set; }
    public required IReadOnlyCollection<StationArrivalDto> Arrivals { get; set; }
}
