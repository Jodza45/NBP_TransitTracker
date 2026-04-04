using System.Text.Json;
using MediatR;
using TransitTracker.Application.Common;
using TransitTracker.Application.DTOs;
using TransitTracker.Application.Interfaces;
using TransitTracker.Domain.Models;

namespace TransitTracker.Application.Queries.GetEta;

public sealed class GetEtaQueryHandler : IRequestHandler<GetEtaQuery, Result<EtaDto>>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IRedisService _redisService;
    private readonly IEtaService _etaService;

    public GetEtaQueryHandler(IRedisService redisService, IEtaService etaService)
    {
        _redisService = redisService;
        _etaService = etaService;
    }

    public async Task<Result<EtaDto>> Handle(GetEtaQuery request, CancellationToken cancellationToken)
    {
        var key = $"eta:{request.VehicleId}:{request.StationId}";
        var cached = await _redisService.HashGetAsync(key, "data", cancellationToken);

        if (cached.IsSuccess)
        {
            var cachedResult = JsonSerializer.Deserialize<EtaResult>(cached.Value, JsonOptions)
                ?? throw new InvalidOperationException("Invalid ETA cache payload.");

            return Result<EtaDto>.Success(new EtaDto
            {
                VehicleId = cachedResult.VehicleId,
                StationId = cachedResult.StationId,
                EstimatedSeconds = cachedResult.EstimatedSeconds
            });
        }

        var eta = await _etaService.CalculateEtaAsync(request.VehicleId, request.StationId, cancellationToken);
        var etaJson = JsonSerializer.Serialize(eta, JsonOptions);

        await _redisService.HashSetFieldAsync(key, "data", etaJson, cancellationToken);
        await _redisService.KeyExpireAsync(key, TimeSpan.FromSeconds(30), cancellationToken);

        return Result<EtaDto>.Success(new EtaDto
        {
            VehicleId = eta.VehicleId,
            StationId = eta.StationId,
            EstimatedSeconds = eta.EstimatedSeconds
        });
    }
}
