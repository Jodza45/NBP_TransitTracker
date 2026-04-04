using MediatR;
using TransitTracker.Application.Common;
using TransitTracker.Application.DTOs;

namespace TransitTracker.Application.Queries.GetNearbyVehicles;

public sealed record GetNearbyVehiclesQuery(double Latitude, double Longitude, double RadiusMeters)
    : IRequest<Result<IReadOnlyCollection<NearbyVehicleDto>>>;
