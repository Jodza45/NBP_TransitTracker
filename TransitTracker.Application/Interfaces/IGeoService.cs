using TransitTracker.Application.Common;
using TransitTracker.Application.DTOs;
using TransitTracker.Domain.Models;

namespace TransitTracker.Application.Interfaces;

public interface IGeoService
{
    Task AddLocationAsync(string vehicleId, double latitude, double longitude, CancellationToken cancellationToken);
    Task<Result<VehicleLocation>> GetLocationAsync(string vehicleId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<NearbyVehicleDto>> GetNearbyVehiclesAsync(double latitude, double longitude, double radiusMeters, CancellationToken cancellationToken);
}
