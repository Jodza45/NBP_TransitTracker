using System.Text.Json;
using TransitTracker.Application.Interfaces;
using TransitTracker.Domain.Models;
using TransitTracker.Infrastructure.Redis;

namespace TransitTracker.Infrastructure.Services;

public sealed class EtaService : IEtaService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IGeoService _geoService;
    private readonly IRedisService _redisService;
    private readonly EtaCalculationService _etaCalculationService;

    public EtaService(IGeoService geoService, IRedisService redisService, EtaCalculationService etaCalculationService)
    {
        _geoService = geoService;
        _redisService = redisService;
        _etaCalculationService = etaCalculationService;
    }

    public async Task<EtaResult> CalculateEtaAsync(string vehicleId, string stationId, CancellationToken cancellationToken)
    {
        var locationResult = await _geoService.GetLocationAsync(vehicleId, cancellationToken);
        if (!locationResult.IsSuccess)
        {
            throw new InvalidOperationException(locationResult.Error);
        }

        var vehicleResult = await _redisService.GetAsync<Vehicle>(RedisKeys.Vehicle(vehicleId), cancellationToken);
        if (!vehicleResult.IsSuccess)
        {
            throw new InvalidOperationException(vehicleResult.Error);
        }

        var stationHashResult = await _redisService.HashGetAsync(RedisKeys.Station(stationId), "data", cancellationToken);
        if (!stationHashResult.IsSuccess)
        {
            throw new InvalidOperationException(stationHashResult.Error);
        }

        var station = JsonSerializer.Deserialize<Station>(stationHashResult.Value, JsonOptions)
            ?? throw new InvalidOperationException($"Invalid station payload for {stationId}");

        var etaSeconds = _etaCalculationService.CalculateSeconds(locationResult.Value, station, vehicleResult.Value.Type);

        return new EtaResult
        {
            VehicleId = vehicleId,
            StationId = stationId,
            EstimatedSeconds = etaSeconds
        };
    }
}
