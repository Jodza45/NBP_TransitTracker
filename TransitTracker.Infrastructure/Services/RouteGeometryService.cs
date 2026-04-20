using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TransitTracker.Application.Interfaces;
using TransitTracker.Domain.Models;
using TransitTracker.Infrastructure.Redis;

namespace TransitTracker.Infrastructure.Services;

public sealed class RouteGeometryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IRedisService _redisService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RouteGeometryService> _logger;

    public RouteGeometryService(
        IRedisService redisService,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<RouteGeometryService> logger)
    {
        _redisService = redisService;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<RouteGeometry> GetRouteGeometryAsync(Line line, CancellationToken cancellationToken)
    {
        var cached = await _redisService.HashGetAsync(RedisKeys.RouteGeometry(line.Id), "data", cancellationToken);
        if (cached.IsSuccess)
        {
            var geometry = JsonSerializer.Deserialize<RouteGeometry>(cached.Value, JsonOptions);
            if (IsValidForLine(geometry, line))
            {
                return geometry!;
            }
        }

        var stationPoints = await LoadStationPointsAsync(line.StationIds, cancellationToken);
        if (stationPoints.Count != line.StationIds.Count)
        {
            return BuildDirectGeometry(line, stationPoints);
        }

        var useOsrm = _configuration.GetValue<bool?>("Routing:UseOsrm") ?? true;
        if (!useOsrm)
        {
            return BuildDirectGeometry(line, stationPoints);
        }

        try
        {
            var geometry = await BuildOsrmGeometryAsync(line, stationPoints, cancellationToken);
            await _redisService.HashSetFieldAsync(RedisKeys.RouteGeometry(line.Id), "data", JsonSerializer.Serialize(geometry, JsonOptions), cancellationToken);
            return geometry;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "OSRM route geometry failed for line {LineId}. Falling back to station-to-station geometry.", line.Id);
            return BuildDirectGeometry(line, stationPoints);
        }
    }

    private async Task<List<RouteStationPoint>> LoadStationPointsAsync(IReadOnlyCollection<string> stationIds, CancellationToken cancellationToken)
    {
        var points = new List<RouteStationPoint>(stationIds.Count);

        foreach (var stationId in stationIds)
        {
            var stationResult = await _redisService.HashGetAsync(RedisKeys.Station(stationId), "data", cancellationToken);
            if (!stationResult.IsSuccess)
            {
                return [];
            }

            var station = JsonSerializer.Deserialize<Station>(stationResult.Value, JsonOptions);
            if (station is null)
            {
                return [];
            }

            points.Add(new RouteStationPoint(station.Id, station.Latitude, station.Longitude));
        }

        return points;
    }

    private async Task<RouteGeometry> BuildOsrmGeometryAsync(Line line, IReadOnlyList<RouteStationPoint> stationPoints, CancellationToken cancellationToken)
    {
        var baseUrl = (_configuration["Routing:OsrmBaseUrl"] ?? "http://localhost:5001").TrimEnd('/');
        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(Math.Max(2, _configuration.GetValue<int?>("Routing:OsrmTimeoutSeconds") ?? 8));

        var routePoints = new List<RoutePoint>();
        var stationPointIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        AddPoint(routePoints, new RoutePoint(stationPoints[0].Latitude, stationPoints[0].Longitude));
        stationPointIndices[stationPoints[0].Id] = 0;

        for (var index = 0; index < stationPoints.Count - 1; index++)
        {
            var from = stationPoints[index];
            var to = stationPoints[index + 1];
            var coordinates = string.Create(
                CultureInfo.InvariantCulture,
                $"{from.Longitude},{from.Latitude};{to.Longitude},{to.Latitude}");
            var url = $"{baseUrl}/route/v1/driving/{coordinates}?overview=full&geometries=geojson&steps=false";

            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"OSRM returned HTTP {(int)response.StatusCode} for line {line.Id}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<OsrmRouteResponse>(stream, JsonOptions, cancellationToken)
                ?? throw new JsonException("Invalid OSRM response.");

            var route = payload.Routes.FirstOrDefault()
                ?? throw new InvalidOperationException($"OSRM returned no route for line {line.Id}.");

            foreach (var coordinate in route.Geometry.Coordinates)
            {
                if (coordinate.Count < 2)
                {
                    continue;
                }

                AddPoint(routePoints, new RoutePoint(coordinate[1], coordinate[0]));
            }

            AddPoint(routePoints, new RoutePoint(to.Latitude, to.Longitude));
            stationPointIndices[to.Id] = routePoints.Count - 1;
        }

        if (routePoints.Count < 2)
        {
            throw new InvalidOperationException($"OSRM returned too few points for line {line.Id}.");
        }

        return new RouteGeometry(line.Id, line.StationIds, routePoints, stationPointIndices, "osrm");
    }

    private static RouteGeometry BuildDirectGeometry(Line line, IReadOnlyList<RouteStationPoint> stationPoints)
    {
        var routePoints = stationPoints.Select(x => new RoutePoint(x.Latitude, x.Longitude)).ToArray();
        var stationPointIndices = stationPoints
            .Select((point, index) => (point.Id, index))
            .ToDictionary(x => x.Id, x => x.index, StringComparer.OrdinalIgnoreCase);

        return new RouteGeometry(line.Id, line.StationIds, routePoints, stationPointIndices, "direct");
    }

    private static void AddPoint(List<RoutePoint> points, RoutePoint point)
    {
        if (points.Count > 0)
        {
            var previous = points[^1];
            if (Math.Abs(previous.Latitude - point.Latitude) < 0.000001 && Math.Abs(previous.Longitude - point.Longitude) < 0.000001)
            {
                return;
            }
        }

        points.Add(point);
    }

    private static bool IsValidForLine(RouteGeometry? geometry, Line line)
    {
        return geometry is not null
            && string.Equals(geometry.LineId, line.Id, StringComparison.OrdinalIgnoreCase)
            && geometry.Points.Count > 1
            && geometry.StationIds.SequenceEqual(line.StationIds, StringComparer.OrdinalIgnoreCase)
            && line.StationIds.All(geometry.StationPointIndices.ContainsKey);
    }

    private sealed record RouteStationPoint(string Id, double Latitude, double Longitude);
    private sealed record OsrmRouteResponse(IReadOnlyList<OsrmRoute> Routes);
    private sealed record OsrmRoute(OsrmGeometry Geometry);
    private sealed record OsrmGeometry(IReadOnlyList<IReadOnlyList<double>> Coordinates);
}

public sealed record RouteGeometry(
    string LineId,
    IReadOnlyList<string> StationIds,
    IReadOnlyList<RoutePoint> Points,
    IReadOnlyDictionary<string, int> StationPointIndices,
    string Source);

public sealed record RoutePoint(double Latitude, double Longitude);
