using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TransitTracker.Application.Interfaces;
using TransitTracker.Domain.Models;
using TransitTracker.Infrastructure.Redis;

namespace TransitTracker.Infrastructure.Workers;

public sealed class NisSimulationWorker : BackgroundService
{
    private readonly IRedisService _redisService;
    private readonly IStreamService _streamService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NisSimulationWorker> _logger;

    public NisSimulationWorker(
        IRedisService redisService,
        IStreamService streamService,
        IConfiguration configuration,
        ILogger<NisSimulationWorker> logger)
    {
        _redisService = redisService;
        _streamService = streamService;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _configuration.GetValue<bool>("Simulation:Enabled");
        if (!enabled)
        {
            _logger.LogInformation("Nis simulation is disabled.");
            return;
        }

        var vehiclesPerLine = Math.Max(1, _configuration.GetValue<int?>("Simulation:VehiclesPerLine") ?? 6);
        var tickMs = Math.Max(300, _configuration.GetValue<int?>("Simulation:TickMs") ?? 1000);

        var fleet = await SeedFleetAsync(vehiclesPerLine, stoppingToken);
        if (fleet.Count == 0)
        {
            _logger.LogWarning("Nis simulation has no active routes (expected line ids like nis-1, nis-2, nis-6, nis-13)." );
            return;
        }

        _logger.LogInformation("Nis simulation started with {Count} vehicles.", fleet.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var state in fleet)
            {
                var points = state.Points;
                var start = points[state.SegmentIndex];
                var next = points[(state.SegmentIndex + 1) % points.Count];
                var current = Interpolate(start, next, state.Progress);

                var values = new Dictionary<string, string>
                {
                    ["vehicleId"] = state.VehicleId,
                    ["lat"] = current.Latitude.ToString(CultureInfo.InvariantCulture),
                    ["lng"] = current.Longitude.ToString(CultureInfo.InvariantCulture),
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
                };

                await _streamService.PublishAsync("gps-updates", values, stoppingToken);

                state.Progress += state.Speed;
                if (state.Progress >= 1d)
                {
                    state.Progress = 0d;
                    state.SegmentIndex = (state.SegmentIndex + 1) % points.Count;
                }
            }

            await Task.Delay(tickMs, stoppingToken);
        }
    }

    private async Task<List<VehicleState>> SeedFleetAsync(int vehiclesPerLine, CancellationToken cancellationToken)
    {
        var routeCatalog = BuildRouteCatalog();
        var fleet = new List<VehicleState>();
        var random = new Random(20260404);

        foreach (var (lineId, shape) in routeCatalog)
        {
            for (var i = 1; i <= vehiclesPerLine; i++)
            {
                var vehicleId = $"sim-{lineId}-{i:D2}";

                var vehicleResult = await _redisService.GetAsync<Vehicle>(RedisKeys.Vehicle(vehicleId), cancellationToken);
                if (!vehicleResult.IsSuccess)
                {
                    var vehicle = new Vehicle
                    {
                        Id = vehicleId,
                        LineId = lineId,
                        Type = VehicleType.Bus,
                        IsActive = true
                    };

                    await _redisService.SetAsync(RedisKeys.Vehicle(vehicleId), vehicle, null, cancellationToken);
                }

                await _redisService.SetAddAsync("active-vehicles", vehicleId, cancellationToken);

                var segment = random.Next(0, shape.Count - 1);
                var progress = random.NextDouble();
                var speed = 0.03 + (random.NextDouble() * 0.05);

                fleet.Add(new VehicleState(vehicleId, shape)
                {
                    SegmentIndex = segment,
                    Progress = progress,
                    Speed = speed
                });
            }
        }

        return fleet;
    }

    private static Dictionary<string, List<RoutePoint>> BuildRouteCatalog()
    {
        return new Dictionary<string, List<RoutePoint>>(StringComparer.OrdinalIgnoreCase)
        {
            ["nis-1"] =
            [
                new(43.2927, 21.9163),
                new(43.3015, 21.9195),
                new(43.3115, 21.9185),
                new(43.3175, 21.9145),
                new(43.3208, 21.9060),
                new(43.3209, 21.8958),
                new(43.3200, 21.9050),
                new(43.3190, 21.9243),
                new(43.3275, 21.9280),
                new(43.3367, 21.9301)
            ],
            ["nis-2"] =
            [
                new(43.3139, 21.8689),
                new(43.3145, 21.8790),
                new(43.3165, 21.8870),
                new(43.3209, 21.8958),
                new(43.3201, 21.9009),
                new(43.3186, 21.9127)
            ],
            ["nis-6"] =
            [
                new(43.3070, 21.9087),
                new(43.3040, 21.9160),
                new(43.3036, 21.9230),
                new(43.3110, 21.9215),
                new(43.3165, 21.9164),
                new(43.3190, 21.9090),
                new(43.3201, 21.9009),
                new(43.3209, 21.8958)
            ],
            ["nis-13"] =
            [
                new(43.3224, 21.8875),
                new(43.3240, 21.9003),
                new(43.3209, 21.8958),
                new(43.3233, 21.9038),
                new(43.3190, 21.9243)
            ]
        };
    }

    private static RoutePoint Interpolate(RoutePoint a, RoutePoint b, double t)
    {
        return new RoutePoint(
            a.Latitude + ((b.Latitude - a.Latitude) * t),
            a.Longitude + ((b.Longitude - a.Longitude) * t));
    }

    private sealed class VehicleState
    {
        public VehicleState(string vehicleId, List<RoutePoint> points)
        {
            VehicleId = vehicleId;
            Points = points;
        }

        public string VehicleId { get; }
        public List<RoutePoint> Points { get; }
        public int SegmentIndex { get; set; }
        public double Progress { get; set; }
        public double Speed { get; set; }
    }

    private sealed record RoutePoint(double Latitude, double Longitude);
}
