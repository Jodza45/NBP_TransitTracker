using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TransitTracker.Application.Interfaces;
using TransitTracker.Domain.Models;
using TransitTracker.Infrastructure.Redis;
using TransitTracker.Infrastructure.Services;

namespace TransitTracker.Infrastructure.Workers;

public sealed class EtaWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IRedisService _redisService;
    private readonly IGeoService _geoService;
    private readonly EtaCalculationService _etaCalculationService;
    private readonly ILogger<EtaWorker> _logger;

    public EtaWorker(
        IRedisService redisService,
        IGeoService geoService,
        EtaCalculationService etaCalculationService,
        ILogger<EtaWorker> logger)
    {
        _redisService = redisService;
        _geoService = geoService;
        _etaCalculationService = etaCalculationService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var activeVehicleIds = await _redisService.SetMembersAsync("active-vehicles", stoppingToken);

                foreach (var vehicleId in activeVehicleIds)
                {
                    var vehicleResult = await _redisService.GetAsync<Vehicle>(RedisKeys.Vehicle(vehicleId), stoppingToken);
                    if (!vehicleResult.IsSuccess || !vehicleResult.Value.IsActive)
                    {
                        continue;
                    }

                    var locationResult = await _geoService.GetLocationAsync(vehicleId, stoppingToken);
                    if (!locationResult.IsSuccess)
                    {
                        continue;
                    }

                    var lineResult = await _redisService.HashGetAsync(RedisKeys.Line(vehicleResult.Value.LineId), "data", stoppingToken);
                    if (!lineResult.IsSuccess)
                    {
                        continue;
                    }

                    var line = JsonSerializer.Deserialize<Line>(lineResult.Value, JsonOptions);
                    if (line is null)
                    {
                        continue;
                    }

                    foreach (var stationId in line.StationIds)
                    {
                        var stationResult = await _redisService.HashGetAsync(RedisKeys.Station(stationId), "data", stoppingToken);
                        if (!stationResult.IsSuccess)
                        {
                            continue;
                        }

                        var station = JsonSerializer.Deserialize<Station>(stationResult.Value, JsonOptions);
                        if (station is null)
                        {
                            continue;
                        }

                        var etaSeconds = _etaCalculationService.CalculateSeconds(locationResult.Value, station, vehicleResult.Value.Type);
                        var eta = new EtaResult
                        {
                            VehicleId = vehicleId,
                            StationId = stationId,
                            EstimatedSeconds = etaSeconds
                        };

                        var etaKey = RedisKeys.Eta(vehicleId, stationId);
                        await _redisService.HashSetFieldAsync(etaKey, "data", JsonSerializer.Serialize(eta, JsonOptions), stoppingToken);
                        await _redisService.KeyExpireAsync(etaKey, TimeSpan.FromSeconds(30), stoppingToken);

                        _logger.LogInformation("ETA calculated for vehicle {VehicleId} and station {StationId}: {Seconds}s", vehicleId, stationId, etaSeconds);
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "ETA worker iteration failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
