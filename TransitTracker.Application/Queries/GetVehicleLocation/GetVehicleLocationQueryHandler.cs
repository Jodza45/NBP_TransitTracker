using MediatR;
using TransitTracker.Application.Common;
using TransitTracker.Application.DTOs;
using TransitTracker.Application.Interfaces;

namespace TransitTracker.Application.Queries.GetVehicleLocation;

public sealed class GetVehicleLocationQueryHandler : IRequestHandler<GetVehicleLocationQuery, Result<VehicleLocationDto>>
{
    private readonly IGeoService _geoService;

    public GetVehicleLocationQueryHandler(IGeoService geoService)
    {
        _geoService = geoService;
    }

    public async Task<Result<VehicleLocationDto>> Handle(GetVehicleLocationQuery request, CancellationToken cancellationToken)
    {
        var location = await _geoService.GetLocationAsync(request.VehicleId, cancellationToken);
        if (!location.IsSuccess)
        {
            return Result<VehicleLocationDto>.Failure(location.Error);
        }

        return Result<VehicleLocationDto>.Success(new VehicleLocationDto
        {
            VehicleId = location.Value.VehicleId,
            Latitude = location.Value.Latitude,
            Longitude = location.Value.Longitude,
            Timestamp = location.Value.Timestamp
        });
    }
}
