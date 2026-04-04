using MediatR;
using TransitTracker.Application.Common;
using TransitTracker.Application.DTOs;

namespace TransitTracker.Application.Queries.GetVehicleLocation;

public sealed record GetVehicleLocationQuery(string VehicleId) : IRequest<Result<VehicleLocationDto>>;
