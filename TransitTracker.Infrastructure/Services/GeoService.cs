using System.Globalization;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TransitTracker.Application.Common;
using TransitTracker.Application.DTOs;
using TransitTracker.Application.Interfaces;
using TransitTracker.Domain.Models;
using TransitTracker.Infrastructure.Redis;

namespace TransitTracker.Infrastructure.Services;

public sealed class GeoService : IGeoService
{
    private readonly IConnectionMultiplexer _connectionMultiplexer;
    private readonly ILogger<GeoService> _logger;

    public GeoService(IConnectionMultiplexer connectionMultiplexer, ILogger<GeoService> logger)
    {
        _connectionMultiplexer = connectionMultiplexer;
        _logger = logger;
    }

    public async Task AddLocationAsync(string vehicleId, double latitude, double longitude, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var db = _connectionMultiplexer.GetDatabase();

        await db.GeoAddAsync("vehicles", longitude, latitude, vehicleId);
        await db.HashSetAsync(RedisKeys.VehicleLocation(vehicleId),
        [
            new HashEntry("lat", latitude.ToString(CultureInfo.InvariantCulture)),
            new HashEntry("lng", longitude.ToString(CultureInfo.InvariantCulture)),
            new HashEntry("ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture))
        ]);

        _logger.LogInformation("Geo update for vehicle {VehicleId}: {Latitude}, {Longitude}", vehicleId, latitude, longitude);
    }

    public async Task<Result<VehicleLocation>> GetLocationAsync(string vehicleId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var db = _connectionMultiplexer.GetDatabase();
        var position = await db.GeoPositionAsync("vehicles", vehicleId);

        if (position is null)
        {
            return Result<VehicleLocation>.Failure($"No geo position for vehicle '{vehicleId}'.");
        }

        var details = await db.HashGetAllAsync(RedisKeys.VehicleLocation(vehicleId));
        var timestamp = DateTimeOffset.UtcNow;
        if (details.Length > 0)
        {
            var tsValue = details.FirstOrDefault(x => x.Name == "ts").Value;
            if (tsValue.HasValue && long.TryParse(tsValue!, NumberStyles.Any, CultureInfo.InvariantCulture, out var unixMs))
            {
                timestamp = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
            }
        }

        return Result<VehicleLocation>.Success(new VehicleLocation
        {
            VehicleId = vehicleId,
            Latitude = position.Value.Latitude,
            Longitude = position.Value.Longitude,
            Timestamp = timestamp
        });
    }

    public async Task<IReadOnlyCollection<NearbyVehicleDto>> GetNearbyVehiclesAsync(double latitude, double longitude, double radiusMeters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var db = _connectionMultiplexer.GetDatabase();

        var result = await db.ExecuteAsync(
            "GEORADIUS",
            "vehicles",
            longitude.ToString(CultureInfo.InvariantCulture),
            latitude.ToString(CultureInfo.InvariantCulture),
            radiusMeters.ToString(CultureInfo.InvariantCulture),
            "m",
            "WITHDIST",
            "WITHCOORD");

        if (result.IsNull)
        {
            return Array.Empty<NearbyVehicleDto>();
        }

        var rows = (RedisResult[])result!;
        var vehicles = new List<NearbyVehicleDto>(rows.Length);

        foreach (var row in rows)
        {
            var rowValues = (RedisResult[])row!;
            var vehicleId = (string)rowValues[0]!;
            var distanceMeters = double.Parse((string)rowValues[1]!, CultureInfo.InvariantCulture);
            var coords = (RedisResult[])rowValues[2]!;
            var itemLongitude = double.Parse((string)coords[0]!, CultureInfo.InvariantCulture);
            var itemLatitude = double.Parse((string)coords[1]!, CultureInfo.InvariantCulture);

            vehicles.Add(new NearbyVehicleDto
            {
                VehicleId = vehicleId,
                Latitude = itemLatitude,
                Longitude = itemLongitude,
                DistanceMeters = distanceMeters
            });
        }

        return vehicles;
    }
}
