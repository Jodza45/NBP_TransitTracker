using MediatR;
using TransitTracker.Application.Common;
using TransitTracker.Application.DTOs;

namespace TransitTracker.Application.Queries.GetEta;

public sealed record GetEtaQuery(string VehicleId, string StationId) : IRequest<Result<EtaDto>>;
