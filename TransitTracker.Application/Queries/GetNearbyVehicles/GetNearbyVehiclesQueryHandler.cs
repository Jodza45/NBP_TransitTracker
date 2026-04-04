using MediatR;
using TransitTracker.Application.Common;
using TransitTracker.Application.DTOs;
using TransitTracker.Application.Interfaces;

namespace TransitTracker.Application.Queries.GetNearbyVehicles;

public sealed class GetNearbyVehiclesQueryHandler : IRequestHandler<GetNearbyVehiclesQuery, Result<IReadOnlyCollection<NearbyVehicleDto>>>
{
    private readonly IGeoService _geoService;

    public GetNearbyVehiclesQueryHandler(IGeoService geoService)
    {
        _geoService = geoService;
    }

    public async Task<Result<IReadOnlyCollection<NearbyVehicleDto>>> Handle(GetNearbyVehiclesQuery request, CancellationToken cancellationToken)
    {
        var vehicles = await _geoService.GetNearbyVehiclesAsync(request.Latitude, request.Longitude, request.RadiusMeters, cancellationToken);
        return Result<IReadOnlyCollection<NearbyVehicleDto>>.Success(vehicles);
    }
}
