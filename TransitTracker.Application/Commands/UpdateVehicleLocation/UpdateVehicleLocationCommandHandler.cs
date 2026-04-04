using MediatR;
using TransitTracker.Application.Common;
using TransitTracker.Application.Interfaces;

namespace TransitTracker.Application.Commands.UpdateVehicleLocation;

public sealed class UpdateVehicleLocationCommandHandler : IRequestHandler<UpdateVehicleLocationCommand, Result>
{
    private readonly IStreamService _streamService;
    private readonly IRedisService _redisService;

    public UpdateVehicleLocationCommandHandler(IStreamService streamService, IRedisService redisService)
    {
        _streamService = streamService;
        _redisService = redisService;
    }

    public async Task<Result> Handle(UpdateVehicleLocationCommand request, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>
        {
            ["vehicleId"] = request.VehicleId,
            ["lat"] = request.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["lng"] = request.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["timestamp"] = request.Timestamp.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        await _streamService.PublishAsync("gps-updates", values, cancellationToken);
        await _redisService.SetAddAsync("active-vehicles", request.VehicleId, cancellationToken);

        return Result.Success();
    }
}
