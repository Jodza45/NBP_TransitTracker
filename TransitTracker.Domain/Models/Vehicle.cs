namespace TransitTracker.Domain.Models;

public sealed class Vehicle
{
    public required string Id { get; set; }
    public required string LineId { get; set; }
    public VehicleType Type { get; set; }
    public bool IsActive { get; set; }
}
