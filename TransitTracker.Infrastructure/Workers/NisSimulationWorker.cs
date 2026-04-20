using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TransitTracker.Application.Interfaces;
using TransitTracker.Domain.Models;
using TransitTracker.Infrastructure.Redis;
using TransitTracker.Infrastructure.Services;

namespace TransitTracker.Infrastructure.Workers;

public sealed class NisSimulationWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IRedisService _redisService;
    private readonly IGeoService _geoService;
    private readonly IStreamService _streamService;
    private readonly RouteGeometryService _routeGeometryService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NisSimulationWorker> _logger;

    public NisSimulationWorker(
        IRedisService redisService,
        IGeoService geoService,
        IStreamService streamService,
        RouteGeometryService routeGeometryService,
        IConfiguration configuration,
        ILogger<NisSimulationWorker> logger)
    {
        _redisService = redisService;
        _geoService = geoService;
        _streamService = streamService;
        _routeGeometryService = routeGeometryService;
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
        var maxCatchUpMinutes = Math.Max(0, _configuration.GetValue<int?>("Simulation:MaxCatchUpMinutes") ?? 240);
        var maxCatchUpMs = TimeSpan.FromMinutes(maxCatchUpMinutes).TotalMilliseconds;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var fleet = await LoadFleetAsync(dwellMs, maxCatchUpMs, stoppingToken);
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
                await PersistStateAsync(state, cancellationToken);
            }

            await Task.Delay(tickMs, cancellationToken);
        }
    }

    private async Task<List<VehicleState>> LoadFleetAsync(int dwellMs, double maxCatchUpMs, CancellationToken cancellationToken)
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

            var geometry = await _routeGeometryService.GetRouteGeometryAsync(line, cancellationToken);
            var points = geometry.Points.ToList();
            if (points.Count < 2)
            {
                continue;
            }

            var speedMetersPerSecond = vehicleResult.Value.Type == VehicleType.Tram ? 11.11 : 8.33;
            var state = await CreateInitialStateAsync(vehicleId, points, random, speedMetersPerSecond, dwellMs, maxCatchUpMs, cancellationToken);

            states.Add(state);
        }

        return states;
    }

    private async Task<VehicleState> CreateInitialStateAsync(
        string vehicleId,
        List<RoutePoint> route,
        Random random,
        double speedMetersPerSecond,
        int dwellMs,
        double maxCatchUpMs,
        CancellationToken cancellationToken)
    {
        var savedResult = await _redisService.GetAsync<PersistedVehicleState>(RedisKeys.SimulationVehicle(vehicleId), cancellationToken);
        if (savedResult.IsSuccess)
        {
            var restored = RestoreState(vehicleId, route, savedResult.Value, speedMetersPerSecond, dwellMs, maxCatchUpMs);
            if (restored is not null)
            {
                return restored;
            }
        }

        if (DemoFleetPlanner.TryGetVehicleSequence(vehicleId, out var sequence))
        {
            var scheduled = new VehicleState(vehicleId, route)
            {
                FromIndex = 0,
                Direction = 1,
                Progress = 0,
                DwellMsRemaining = 0,
                SpeedMetersPerSecond = speedMetersPerSecond
            };

            CatchUpState(scheduled, DemoFleetPlanner.GetDepartureOffset(sequence).TotalMilliseconds, dwellMs);
            return scheduled;
        }

        var locationResult = await _geoService.GetLocationAsync(vehicleId, cancellationToken);
        if (locationResult.IsSuccess)
        {
            var nearest = FindNearestSegment(route, new RoutePoint(locationResult.Value.Latitude, locationResult.Value.Longitude));
            return new VehicleState(vehicleId, route)
            {
                FromIndex = nearest.FromIndex,
                Direction = 1,
                Progress = nearest.Progress,
                DwellMsRemaining = 0,
                SpeedMetersPerSecond = speedMetersPerSecond
            };
        }

        var startIndex = random.Next(0, route.Count - 1);
        return new VehicleState(vehicleId, route)
        {
            FromIndex = startIndex,
            Direction = 1,
            Progress = random.NextDouble(),
            DwellMsRemaining = 0,
            SpeedMetersPerSecond = speedMetersPerSecond
        };
    }

    private static VehicleState? RestoreState(
        string vehicleId,
        List<RoutePoint> route,
        PersistedVehicleState saved,
        double speedMetersPerSecond,
        int dwellMs,
        double maxCatchUpMs)
    {
        if (route.Count < 2 || saved.RoutePointCount != route.Count)
        {
            return null;
        }

        var direction = saved.Direction < 0 ? -1 : 1;
        var fromIndex = Math.Clamp(saved.FromIndex, 0, route.Count - 1);

        if (direction > 0 && fromIndex >= route.Count - 1)
        {
            fromIndex = route.Count - 2;
        }

        if (direction < 0 && fromIndex <= 0)
        {
            direction = 1;
            fromIndex = 0;
        }

        var restored = new VehicleState(vehicleId, route)
        {
            FromIndex = fromIndex,
            Direction = direction,
            Progress = Math.Clamp(saved.Progress, 0d, 1d),
            DwellMsRemaining = Math.Max(0, saved.DwellMsRemaining),
            SpeedMetersPerSecond = speedMetersPerSecond
        };

        var elapsedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - saved.LastUpdatedUnixMs;
        var catchUpMs = Math.Clamp(elapsedMs, 0d, maxCatchUpMs);
        CatchUpState(restored, catchUpMs, dwellMs);

        return restored;
    }

    private async Task PersistStateAsync(VehicleState state, CancellationToken cancellationToken)
    {
        await _redisService.SetAsync(
            RedisKeys.SimulationVehicle(state.VehicleId),
            new PersistedVehicleState(
                state.FromIndex,
                state.Direction,
                state.Progress,
                state.DwellMsRemaining,
                state.SpeedMetersPerSecond,
                state.Route.Count,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            null,
            cancellationToken);
    }

    private static NearestSegment FindNearestSegment(IReadOnlyList<RoutePoint> route, RoutePoint point)
    {
        var bestFromIndex = 0;
        var bestProgress = 0d;
        var bestDistance = double.MaxValue;

        for (var index = 0; index < route.Count - 1; index++)
        {
            var projection = ProjectToSegment(route[index], route[index + 1], point);
            if (projection.DistanceScore < bestDistance)
            {
                bestFromIndex = index;
                bestProgress = projection.Progress;
                bestDistance = projection.DistanceScore;
            }
        }

        return new NearestSegment(bestFromIndex, bestProgress);
    }

    private static SegmentProjection ProjectToSegment(RoutePoint from, RoutePoint to, RoutePoint point)
    {
        var meanLat = ToRadians((from.Latitude + to.Latitude) / 2d);
        var toX = (to.Longitude - from.Longitude) * Math.Cos(meanLat);
        var toY = to.Latitude - from.Latitude;
        var pointX = (point.Longitude - from.Longitude) * Math.Cos(meanLat);
        var pointY = point.Latitude - from.Latitude;

        var lengthSquared = (toX * toX) + (toY * toY);
        if (lengthSquared <= 0)
        {
            return new SegmentProjection(0, (pointX * pointX) + (pointY * pointY));
        }

        var progress = Math.Clamp(((pointX * toX) + (pointY * toY)) / lengthSquared, 0d, 1d);
        var projectedX = toX * progress;
        var projectedY = toY * progress;
        var distanceX = pointX - projectedX;
        var distanceY = pointY - projectedY;

        return new SegmentProjection(progress, (distanceX * distanceX) + (distanceY * distanceY));
    }

    private static void AdvanceState(VehicleState state, int tickMs, int dwellMs)
    {
        state.Apply(SimulationRouteStepper.Advance(state.ToRouteState(), state.Route, tickMs, dwellMs));
    }

    private static void CatchUpState(VehicleState state, double catchUpMs, int dwellMs)
    {
        const int catchUpStepMs = 5000;

        while (catchUpMs > 0)
        {
            var stepMs = (int)Math.Min(catchUpStepMs, catchUpMs);
            AdvanceState(state, stepMs, dwellMs);
            catchUpMs -= stepMs;
        }
    }

    private static RoutePoint InterpolateCurrent(VehicleState state)
    {
        return SimulationRouteStepper.Interpolate(state.ToRouteState(), state.Route);
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

        public SimulationVehicleRouteState ToRouteState()
        {
            return new SimulationVehicleRouteState(
                FromIndex,
                Direction,
                Progress,
                DwellMsRemaining,
                SpeedMetersPerSecond);
        }

        public void Apply(SimulationVehicleRouteState state)
        {
            FromIndex = state.FromIndex;
            Direction = state.Direction;
            Progress = state.Progress;
            DwellMsRemaining = state.DwellMsRemaining;
            SpeedMetersPerSecond = state.SpeedMetersPerSecond;
        }
    }

    private sealed record NearestSegment(int FromIndex, double Progress);
    private sealed record SegmentProjection(double Progress, double DistanceScore);
    private sealed record PersistedVehicleState(
        int FromIndex,
        int Direction,
        double Progress,
        int DwellMsRemaining,
        double SpeedMetersPerSecond,
        int RoutePointCount,
        long LastUpdatedUnixMs);
}
