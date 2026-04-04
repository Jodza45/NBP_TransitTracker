using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TransitTracker.Application.DTOs;
using TransitTracker.Application.Interfaces;
using TransitTracker.Domain.Models;
using TransitTracker.Infrastructure.Hubs;
using TransitTracker.Infrastructure.Redis;

namespace TransitTracker.Infrastructure.Workers;

public sealed class LocationWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IStreamService _streamService;
    private readonly IGeoService _geoService;
    private readonly IPubSubService _pubSubService;
    private readonly IRedisService _redisService;
    private readonly IHubContext<VehicleLocationHub> _hubContext;
    private readonly ILogger<LocationWorker> _logger;

    public LocationWorker(
        IStreamService streamService,
        IGeoService geoService,
        IPubSubService pubSubService,
        IRedisService redisService,
        IHubContext<VehicleLocationHub> hubContext,
        ILogger<LocationWorker> logger)
    {
        _streamService = streamService;
        _geoService = geoService;
        _pubSubService = pubSubService;
        _redisService = redisService;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _streamService.EnsureConsumerGroupAsync("gps-updates", "transit-workers", stoppingToken);
        await _streamService.SetGroupToLatestAsync("gps-updates", "transit-workers", stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var entries = await _streamService.ReadGroupAsync("gps-updates", "transit-workers", "consumer1", 10, stoppingToken);
                _logger.LogInformation("Location worker read {Count} stream entries.", entries.Count);

                foreach (var entry in entries)
                {
                    var vehicleId = entry.Values["vehicleId"];
                    var lat = double.Parse(entry.Values["lat"], CultureInfo.InvariantCulture);
                    var lng = double.Parse(entry.Values["lng"], CultureInfo.InvariantCulture);
                    var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(entry.Values["timestamp"], CultureInfo.InvariantCulture));

                    await _geoService.AddLocationAsync(vehicleId, lat, lng, stoppingToken);

                    var vehicleResult = await _redisService.GetAsync<Vehicle>(RedisKeys.Vehicle(vehicleId), stoppingToken);
                    if (vehicleResult.IsSuccess)
                    {
                        var dto = new VehicleLocationDto
                        {
                            VehicleId = vehicleId,
                            Latitude = lat,
                            Longitude = lng,
                            Timestamp = timestamp
                        };

                        var payload = JsonSerializer.Serialize(dto, JsonOptions);
                        var channel = $"line:{vehicleResult.Value.LineId}";

                        await _pubSubService.PublishAsync(channel, payload, stoppingToken);
                        await _hubContext.Clients.Group(channel).SendAsync("ReceiveVehicleLocation", dto, stoppingToken);
                    }

                    await _streamService.AckAsync("gps-updates", "transit-workers", entry.Id, stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Location worker iteration failed.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
        }
    }
}


