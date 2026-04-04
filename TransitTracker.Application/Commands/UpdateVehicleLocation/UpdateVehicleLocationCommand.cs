using MediatR;
using TransitTracker.Application.Common;

namespace TransitTracker.Application.Commands.UpdateVehicleLocation;

public sealed record UpdateVehicleLocationCommand(
    string VehicleId,
    double Latitude,
    double Longitude,
    DateTimeOffset Timestamp) : IRequest<Result>;
