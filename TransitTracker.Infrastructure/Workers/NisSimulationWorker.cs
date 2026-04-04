using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TransitTracker.Application.Interfaces;
using TransitTracker.Domain.Models;
using TransitTracker.Infrastructure.Redis;

namespace TransitTracker.Infrastructure.Workers;

public sealed class NisSimulationWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

        var tickMs = Math.Max(250, _configuration.GetValue<int?>("Simulation:TickMs") ?? 1000);
        var dwellMs = Math.Max(0, _configuration.GetValue<int?>("Simulation:DwellMs") ?? 3000);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var fleet = await LoadFleetAsync(stoppingToken);
                if (fleet.Count == 0)
                {
                    _logger.LogWarning("Nis simulation found no active vehicles with valid line station paths.");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                _logger.LogInformation("Nis simulation tick loop started for {Count} vehicles.", fleet.Count);
                await RunTickLoopAsync(fleet, tickMs, dwellMs, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nis simulation failed. Retrying in 5 seconds.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task RunTickLoopAsync(List<VehicleState> fleet, int tickMs, int dwellMs, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var state in fleet)
            {
                AdvanceState(state, tickMs, dwellMs);
                var current = InterpolateCurrent(state);

                var values = new Dictionary<string, string>
                {
                    ["vehicleId"] = state.VehicleId,
                    ["lat"] = current.Latitude.ToString(CultureInfo.InvariantCulture),
                    ["lng"] = current.Longitude.ToString(CultureInfo.InvariantCulture),
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
                };

                await _streamService.PublishAsync("gps-updates", values, cancellationToken);
            }

            await Task.Delay(tickMs, cancellationToken);
        }
    }

    private async Task<List<VehicleState>> LoadFleetAsync(CancellationToken cancellationToken)
    {
        var activeVehicleIds = await _redisService.SetMembersAsync("active-vehicles", cancellationToken);
        var states = new List<VehicleState>(activeVehicleIds.Count);
        var random = new Random();

        foreach (var vehicleId in activeVehicleIds)
        {
            var vehicleResult = await _redisService.GetAsync<Vehicle>(RedisKeys.Vehicle(vehicleId), cancellationToken);
            if (!vehicleResult.IsSuccess || !vehicleResult.Value.IsActive)
            {
                continue;
            }

            var lineResult = await _redisService.HashGetAsync(RedisKeys.Line(vehicleResult.Value.LineId), "data", cancellationToken);
            if (!lineResult.IsSuccess)
            {
                continue;
            }

            var line = JsonSerializer.Deserialize<Line>(lineResult.Value, JsonOptions);
            if (line is null || line.StationIds.Count < 2)
            {
                continue;
            }

            var points = await LoadRoutePointsAsync(line.StationIds, cancellationToken);
            if (points.Count < 2)
            {
                continue;
            }

            var startIndex = random.Next(0, points.Count - 1);
            var direction = random.Next(0, 2) == 0 ? 1 : -1;
            if (direction < 0 && startIndex == 0)
            {
                startIndex = points.Count - 1;
            }

            var fromIndex = direction > 0 ? startIndex : startIndex - 1;
            if (fromIndex < 0)
            {
                fromIndex = 0;
            }

            if (fromIndex >= points.Count - 1)
            {
                fromIndex = points.Count - 2;
            }

            var speedMetersPerSecond = vehicleResult.Value.Type == VehicleType.Tram ? 11.11 : 8.33;

            states.Add(new VehicleState(vehicleId, points)
            {
                FromIndex = fromIndex,
                Direction = direction,
                Progress = random.NextDouble(),
                DwellMsRemaining = 0,
                SpeedMetersPerSecond = speedMetersPerSecond
            });
        }

        return states;
    }

    private async Task<List<RoutePoint>> LoadRoutePointsAsync(IReadOnlyCollection<string> stationIds, CancellationToken cancellationToken)
    {
        var points = new List<RoutePoint>(stationIds.Count);

        foreach (var stationId in stationIds)
        {
            var stationResult = await _redisService.HashGetAsync(RedisKeys.Station(stationId), "data", cancellationToken);
            if (!stationResult.IsSuccess)
            {
                continue;
            }

            var station = JsonSerializer.Deserialize<Station>(stationResult.Value, JsonOptions);
            if (station is null)
            {
                continue;
            }

            if (points.Count > 0)
            {
                var last = points[^1];
                if (Math.Abs(last.Latitude - station.Latitude) < 0.000001 && Math.Abs(last.Longitude - station.Longitude) < 0.000001)
                {
                    continue;
                }
            }

            points.Add(new RoutePoint(station.Latitude, station.Longitude));
        }

        return points;
    }

    private static void AdvanceState(VehicleState state, int tickMs, int dwellMs)
    {
        if (state.DwellMsRemaining > 0)
        {
            state.DwellMsRemaining = Math.Max(0, state.DwellMsRemaining - tickMs);
            return;
        }

        var remainingMeters = state.SpeedMetersPerSecond * (tickMs / 1000d);

        while (remainingMeters > 0)
        {
            var from = state.Route[state.FromIndex];
            var toIndex = GetToIndex(state);
            var to = state.Route[toIndex];

            var segmentLength = DistanceMeters(from, to);
            if (segmentLength < 0.5)
            {
                state.Progress = 1;
            }
            else
            {
                var progressDelta = remainingMeters / segmentLength;
                var capacity = 1d - state.Progress;

                if (progressDelta < capacity)
                {
                    state.Progress += progressDelta;
                    remainingMeters = 0;
                    break;
                }

                remainingMeters -= capacity * segmentLength;
                state.Progress = 1;
            }

            AdvanceToNextSegment(state);
            state.DwellMsRemaining = dwellMs;
            if (state.DwellMsRemaining > 0)
            {
                break;
            }
        }
    }

    private static RoutePoint InterpolateCurrent(VehicleState state)
    {
        var from = state.Route[state.FromIndex];
        var to = state.Route[GetToIndex(state)];
        var t = Math.Clamp(state.Progress, 0d, 1d);

        return new RoutePoint(
            from.Latitude + ((to.Latitude - from.Latitude) * t),
            from.Longitude + ((to.Longitude - from.Longitude) * t));
    }

    private static void AdvanceToNextSegment(VehicleState state)
    {
        var arrivedIndex = GetToIndex(state);

        if (arrivedIndex == state.Route.Count - 1)
        {
            state.Direction = -1;
            state.FromIndex = state.Route.Count - 1;
            state.Progress = 0;
            return;
        }

        if (arrivedIndex == 0)
        {
            state.Direction = 1;
            state.FromIndex = 0;
            state.Progress = 0;
            return;
        }

        state.FromIndex = arrivedIndex;
        state.Progress = 0;
    }

    private static int GetToIndex(VehicleState state)
    {
        var toIndex = state.FromIndex + state.Direction;
        if (toIndex < 0)
        {
            return 0;
        }

        if (toIndex >= state.Route.Count)
        {
            return state.Route.Count - 1;
        }

        return toIndex;
    }

    private static double DistanceMeters(RoutePoint a, RoutePoint b)
    {
        const double earthRadius = 6371000d;
        var dLat = ToRadians(b.Latitude - a.Latitude);
        var dLon = ToRadians(b.Longitude - a.Longitude);
        var lat1 = ToRadians(a.Latitude);
        var lat2 = ToRadians(b.Latitude);

        var haversine =
            Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
            Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        return 2d * earthRadius * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(1d - haversine));
    }

    private static double ToRadians(double value)
    {
        return value * Math.PI / 180d;
    }

    private sealed class VehicleState
    {
        public VehicleState(string vehicleId, List<RoutePoint> route)
        {
            VehicleId = vehicleId;
            Route = route;
        }

        public string VehicleId { get; }
        public List<RoutePoint> Route { get; }
        public int FromIndex { get; set; }
        public int Direction { get; set; }
        public double Progress { get; set; }
        public int DwellMsRemaining { get; set; }
        public double SpeedMetersPerSecond { get; set; }
    }

    private sealed record RoutePoint(double Latitude, double Longitude);
}
