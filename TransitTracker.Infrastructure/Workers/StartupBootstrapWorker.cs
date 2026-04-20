using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TransitTracker.Application.Interfaces;
using TransitTracker.Domain.Models;
using TransitTracker.Infrastructure.Redis;
using TransitTracker.Infrastructure.Services;

namespace TransitTracker.Infrastructure.Workers;

public sealed class StartupBootstrapWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IConfiguration _configuration;
    private readonly IRedisService _redisService;
    private readonly IGtfsImportService _gtfsImportService;
    private readonly RouteGeometryService _routeGeometryService;
    private readonly ILogger<StartupBootstrapWorker> _logger;

    public StartupBootstrapWorker(
        IConfiguration configuration,
        IRedisService redisService,
        IGtfsImportService gtfsImportService,
        RouteGeometryService routeGeometryService,
        ILogger<StartupBootstrapWorker> logger)
    {
        _configuration = configuration;
        _redisService = redisService;
        _gtfsImportService = gtfsImportService;
        _routeGeometryService = routeGeometryService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _configuration.GetValue<bool?>("Bootstrap:Enabled") ?? true;
        if (!enabled)
        {
            _logger.LogInformation("Startup bootstrap is disabled.");
            return;
        }

        var force = _configuration.GetValue<bool?>("Bootstrap:ForceGtfsImport") ?? false;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

            var lines = await _redisService.SetMembersAsync("lines:all", stoppingToken);
            var activeVehicles = await _redisService.SetMembersAsync("active-vehicles", stoppingToken);
            var fleetProfile = await _redisService.GetAsync<string>("bootstrap:fleet-profile-version", stoppingToken);
            var hasCurrentFleetProfile = fleetProfile.IsSuccess
                && string.Equals(fleetProfile.Value, DemoFleetPlanner.FleetProfileVersion, StringComparison.OrdinalIgnoreCase);

            var shouldImport = force || lines.Count == 0 || activeVehicles.Count == 0 || !hasCurrentFleetProfile;
            if (!shouldImport)
            {
                _logger.LogInformation("Startup bootstrap skipped. Existing lines={Lines}, activeVehicles={Vehicles}.", lines.Count, activeVehicles.Count);
            }
            else
            {
                var result = await _gtfsImportService.ImportNisAsync(force: true, stoppingToken);
                _logger.LogInformation(
                    "Startup bootstrap completed. Source={Source}, Stations={Stations}, Lines={Lines}, DemoVehicles={Vehicles}.",
                    result.Source,
                    result.Stations,
                    result.Lines,
                    result.DemoVehicles);
            }

            if (_configuration.GetValue<bool?>("Routing:WarmupOnStartup") ?? true)
            {
                await WarmupRouteGeometryAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup bootstrap failed.");
        }
    }

    private async Task WarmupRouteGeometryAsync(CancellationToken cancellationToken)
    {
        var lineIds = await _redisService.SetMembersAsync("lines:all", cancellationToken);
        if (lineIds.Count == 0)
        {
            _logger.LogInformation("Route geometry warmup skipped. No lines found.");
            return;
        }

        var warmed = 0;
        var skipped = 0;

        _logger.LogInformation("Route geometry warmup started for {Count} lines.", lineIds.Count);

        foreach (var lineId in lineIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var lineResult = await _redisService.HashGetAsync(RedisKeys.Line(lineId), "data", cancellationToken);
                if (!lineResult.IsSuccess)
                {
                    skipped++;
                    continue;
                }

                var line = JsonSerializer.Deserialize<Line>(lineResult.Value, JsonOptions);
                if (line is null || line.StationIds.Count < 2)
                {
                    skipped++;
                    continue;
                }

                var geometry = await _routeGeometryService.GetRouteGeometryAsync(line, cancellationToken);
                warmed++;

                _logger.LogInformation(
                    "Route geometry warmed for line {LineId}. Points={Points}, Source={Source}.",
                    line.Id,
                    geometry.Points.Count,
                    geometry.Source);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                skipped++;
                _logger.LogWarning(ex, "Route geometry warmup failed for line {LineId}.", lineId);
            }
        }

        _logger.LogInformation("Route geometry warmup completed. Warmed={Warmed}, Skipped={Skipped}.", warmed, skipped);
    }
}
