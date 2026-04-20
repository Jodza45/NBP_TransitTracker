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
    private readonly RouteGeometryService _routeGeometryService;

    public EtaService(
        IGeoService geoService,
        IRedisService redisService,
        EtaCalculationService etaCalculationService,
        RouteGeometryService routeGeometryService)
    {
        _geoService = geoService;
        _redisService = redisService;
        _etaCalculationService = etaCalculationService;
        _routeGeometryService = routeGeometryService;
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

        var etaSeconds = await CalculateRouteAwareEtaAsync(vehicleResult.Value, stationId, cancellationToken)
            ?? _etaCalculationService.CalculateSeconds(locationResult.Value, station, vehicleResult.Value.Type);

        return new EtaResult
        {
            VehicleId = vehicleId,
            StationId = stationId,
            EstimatedSeconds = etaSeconds
        };
    }

    private async Task<int?> CalculateRouteAwareEtaAsync(Vehicle vehicle, string stationId, CancellationToken cancellationToken)
    {
        var stateResult = await _redisService.GetAsync<PersistedVehicleState>(RedisKeys.SimulationVehicle(vehicle.Id), cancellationToken);
        if (!stateResult.IsSuccess)
        {
            return null;
        }

        var lineResult = await _redisService.HashGetAsync(RedisKeys.Line(vehicle.LineId), "data", cancellationToken);
        if (!lineResult.IsSuccess)
        {
            return null;
        }

        var line = JsonSerializer.Deserialize<Line>(lineResult.Value, JsonOptions);
        if (line is null || line.StationIds.Count < 2)
        {
            return null;
        }

        var geometry = await _routeGeometryService.GetRouteGeometryAsync(line, cancellationToken);
        if (!geometry.StationPointIndices.TryGetValue(stationId, out var targetIndex)
            || geometry.Points.Count < 2
            || stateResult.Value.RoutePointCount != geometry.Points.Count)
        {
            return null;
        }

        var distanceMeters = CalculateDistanceAlongRoute(geometry.Points, stateResult.Value, targetIndex);
        return _etaCalculationService.CalculateSecondsFromMeters(distanceMeters, vehicle.Type);
    }

    private double CalculateDistanceAlongRoute(IReadOnlyList<RoutePoint> route, PersistedVehicleState state, int targetIndex)
    {
        var direction = state.Direction < 0 ? -1 : 1;
        var fromIndex = Math.Clamp(state.FromIndex, 0, route.Count - 1);
        var progress = Math.Clamp(state.Progress, 0d, 1d);

        if (direction > 0 && fromIndex >= route.Count - 1)
        {
            fromIndex = route.Count - 1;
            direction = -1;
            progress = 0d;
        }

        if (direction < 0 && fromIndex <= 0)
        {
            fromIndex = 0;
            direction = 1;
            progress = 0d;
        }

        if (direction > 0)
        {
            return targetIndex >= fromIndex
                ? DistanceForward(route, fromIndex, progress, targetIndex)
                : DistanceForward(route, fromIndex, progress, route.Count - 1) + DistanceBackward(route, route.Count - 1, 0d, targetIndex);
        }

        return targetIndex <= fromIndex
            ? DistanceBackward(route, fromIndex, progress, targetIndex)
            : DistanceBackward(route, fromIndex, progress, 0) + DistanceForward(route, 0, 0d, targetIndex);
    }

    private double DistanceForward(IReadOnlyList<RoutePoint> route, int fromIndex, double progress, int targetIndex)
    {
        if (targetIndex == fromIndex)
        {
            return progress <= 0.03d ? 0d : DistanceForward(route, fromIndex, progress, route.Count - 1) + DistanceBackward(route, route.Count - 1, 0d, targetIndex);
        }

        var distance = SegmentDistance(route, fromIndex, fromIndex + 1) * (1d - progress);
        for (var index = fromIndex + 1; index < targetIndex; index++)
        {
            distance += SegmentDistance(route, index, index + 1);
        }

        return distance;
    }

    private double DistanceBackward(IReadOnlyList<RoutePoint> route, int fromIndex, double progress, int targetIndex)
    {
        if (targetIndex == fromIndex)
        {
            return progress <= 0.03d ? 0d : DistanceBackward(route, fromIndex, progress, 0) + DistanceForward(route, 0, 0d, targetIndex);
        }

        var distance = SegmentDistance(route, fromIndex, fromIndex - 1) * (1d - progress);
        for (var index = fromIndex - 1; index > targetIndex; index--)
        {
            distance += SegmentDistance(route, index, index - 1);
        }

        return distance;
    }

    private double SegmentDistance(IReadOnlyList<RoutePoint> route, int fromIndex, int toIndex)
    {
        var from = route[fromIndex];
        var to = route[toIndex];
        return _etaCalculationService.CalculateDistanceMeters(from.Latitude, from.Longitude, to.Latitude, to.Longitude);
    }

    private sealed record PersistedVehicleState(
        int FromIndex,
        int Direction,
        double Progress,
        int DwellMsRemaining,
        double SpeedMetersPerSecond,
        int RoutePointCount,
        long LastUpdatedUnixMs);
}
