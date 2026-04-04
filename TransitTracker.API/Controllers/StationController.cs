using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using TransitTracker.API.Contracts;
using TransitTracker.API.Geo;
using TransitTracker.Application.Interfaces;
using TransitTracker.Domain.Models;
using TransitTracker.Infrastructure.Redis;

namespace TransitTracker.API.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/stations")]
[Route("api/v{version:apiVersion}/stations")]
public sealed class StationController : ControllerBase
{
    private static readonly string[] OverpassUrls =
    [
        "https://overpass-api.de/api/interpreter",
        "https://overpass.kumi.systems/api/interpreter"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IRedisService _redisService;
    private readonly IEtaService _etaService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StationController> _logger;

    public StationController(
        IRedisService redisService,
        IEtaService etaService,
        IHttpClientFactory httpClientFactory,
        ILogger<StationController> logger)
    {
        _redisService = redisService;
        _etaService = etaService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Station), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStation(string id, CancellationToken cancellationToken)
    {
        var result = await _redisService.HashGetAsync(RedisKeys.Station(id), "data", cancellationToken);
        if (!result.IsSuccess)
        {
            return Problem(title: "Station not found", detail: result.Error, statusCode: StatusCodes.Status404NotFound);
        }

        var station = JsonSerializer.Deserialize<Station>(result.Value, JsonOptions)
            ?? throw new InvalidOperationException("Invalid station payload.");

        return Ok(station);
    }

    [HttpGet("all")]
    [ProducesResponseType(typeof(IReadOnlyCollection<Station>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllStations(CancellationToken cancellationToken)
    {
        var stationIds = await _redisService.SetMembersAsync("stations:all", cancellationToken);
        var stations = new List<Station>(stationIds.Count);

        foreach (var stationId in stationIds)
        {
            var stationResult = await _redisService.HashGetAsync(RedisKeys.Station(stationId), "data", cancellationToken);
            if (!stationResult.IsSuccess)
            {
                continue;
            }

            var station = JsonSerializer.Deserialize<Station>(stationResult.Value, JsonOptions);
            if (station is null || !NisGeoFence.Contains(station.Latitude, station.Longitude))
            {
                continue;
            }

            stations.Add(station);
        }

        return Ok(stations.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [HttpGet("nearby")]
    [ProducesResponseType(typeof(IReadOnlyCollection<NearbyStationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetNearbyStations([FromQuery] double lat, [FromQuery] double lng, [FromQuery] double radius = 2000, CancellationToken cancellationToken = default)
    {
        if (lat < -90 || lat > 90 || lng < -180 || lng > 180 || radius <= 0)
        {
            return Problem(title: "Invalid query", detail: "Latitude/longitude/radius are invalid.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!NisGeoFence.Contains(lat, lng))
        {
            return Problem(title: "Out of area", detail: "Search point must be inside Nis city bounds.", statusCode: StatusCodes.Status400BadRequest);
        }

        var nearbyGeo = await _redisService.GeoRadiusAsync("stations:geo", lat, lng, radius, cancellationToken);
        var nearby = new List<NearbyStationDto>(nearbyGeo.Count);

        foreach (var (member, distance) in nearbyGeo.OrderBy(x => x.DistanceMeters))
        {
            var stationResult = await _redisService.HashGetAsync(RedisKeys.Station(member), "data", cancellationToken);
            if (!stationResult.IsSuccess)
            {
                continue;
            }

            var station = JsonSerializer.Deserialize<Station>(stationResult.Value, JsonOptions);
            if (station is null || !NisGeoFence.Contains(station.Latitude, station.Longitude))
            {
                continue;
            }

            nearby.Add(new NearbyStationDto
            {
                Id = station.Id,
                Name = station.Name,
                Latitude = station.Latitude,
                Longitude = station.Longitude,
                DistanceMeters = Math.Round(distance, 1)
            });
        }

        return Ok(nearby);
    }

    [HttpGet("{id}/arrivals")]
    [ProducesResponseType(typeof(StationArrivalsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetArrivals(string id, CancellationToken cancellationToken)
    {
        var stationResult = await _redisService.HashGetAsync(RedisKeys.Station(id), "data", cancellationToken);
        if (!stationResult.IsSuccess)
        {
            return Problem(title: "Station not found", detail: stationResult.Error, statusCode: StatusCodes.Status404NotFound);
        }

        var station = JsonSerializer.Deserialize<Station>(stationResult.Value, JsonOptions)
            ?? throw new InvalidOperationException("Invalid station payload.");

        if (!NisGeoFence.Contains(station.Latitude, station.Longitude))
        {
            return Problem(title: "Out of area", detail: "Station is outside Nis city bounds.", statusCode: StatusCodes.Status404NotFound);
        }

        var lineIds = await _redisService.SetMembersAsync("lines:all", cancellationToken);
        var linesWithStation = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var lineId in lineIds)
        {
            var lineResult = await _redisService.HashGetAsync(RedisKeys.Line(lineId), "data", cancellationToken);
            if (!lineResult.IsSuccess)
            {
                continue;
            }

            var line = JsonSerializer.Deserialize<Line>(lineResult.Value, JsonOptions);
            if (line is null)
            {
                continue;
            }

            if (line.StationIds.Any(s => string.Equals(s, id, StringComparison.OrdinalIgnoreCase)))
            {
                linesWithStation.Add(lineId);
            }
        }

        var activeVehicles = await _redisService.SetMembersAsync("active-vehicles", cancellationToken);
        var arrivals = new List<StationArrivalDto>();

        foreach (var vehicleId in activeVehicles)
        {
            var vehicleResult = await _redisService.GetAsync<Vehicle>(RedisKeys.Vehicle(vehicleId), cancellationToken);
            if (!vehicleResult.IsSuccess || !vehicleResult.Value.IsActive || !linesWithStation.Contains(vehicleResult.Value.LineId))
            {
                continue;
            }

            try
            {
                var eta = await _etaService.CalculateEtaAsync(vehicleId, id, cancellationToken);
                arrivals.Add(new StationArrivalDto
                {
                    VehicleId = vehicleId,
                    LineId = vehicleResult.Value.LineId,
                    EstimatedSeconds = eta.EstimatedSeconds
                });
            }
            catch
            {
            }
        }

        var response = new StationArrivalsResponse
        {
            StationId = station.Id,
            StationName = station.Name,
            Arrivals = arrivals.OrderBy(x => x.EstimatedSeconds).ToArray()
        };

        return Ok(response);
    }

    [HttpPost("reindex-geo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ReindexStationsGeo(CancellationToken cancellationToken)
    {
        var stationIds = await _redisService.SetMembersAsync("stations:all", cancellationToken);
        var count = 0;

        foreach (var stationId in stationIds)
        {
            var stationResult = await _redisService.HashGetAsync(RedisKeys.Station(stationId), "data", cancellationToken);
            if (!stationResult.IsSuccess)
            {
                continue;
            }

            var station = JsonSerializer.Deserialize<Station>(stationResult.Value, JsonOptions);
            if (station is null || !NisGeoFence.Contains(station.Latitude, station.Longitude))
            {
                continue;
            }

            await _redisService.GeoAddAsync("stations:geo", station.Id, station.Latitude, station.Longitude, cancellationToken);
            count++;
        }

        return Ok(new { indexed = count });
    }

    [HttpPost("import-osm-nis")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ImportFromOsm([FromQuery] bool force = false, CancellationToken cancellationToken = default)
    {
        var existing = await _redisService.SetMembersAsync("stations:all", cancellationToken);
        if (existing.Count > 0 && !force)
        {
            return Ok(new { imported = 0, skipped = true, totalStations = existing.Count });
        }

        var overpassQuery = """
            [out:json][timeout:25];
            (
              node["highway"="bus_stop"](43.255,21.78,43.39,22.02);
              node["public_transport"="platform"]["bus"="yes"](43.255,21.78,43.39,22.02);
              node["railway"="tram_stop"](43.255,21.78,43.39,22.02);
            );
            out body;
            """;

        OverpassResponse? payload = null;
        var client = _httpClientFactory.CreateClient();

        foreach (var url in OverpassUrls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["data"] = overpassQuery
                    })
                };

                using var response = await client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Overpass endpoint {Url} returned {StatusCode}.", url, (int)response.StatusCode);
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                payload = await JsonSerializer.DeserializeAsync<OverpassResponse>(stream, JsonOptions, cancellationToken);
                if (payload?.Elements?.Count > 0)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Overpass endpoint {Url} failed.", url);
            }
        }

        if (payload?.Elements is null)
        {
            return Problem(
                title: "Overpass import failed",
                detail: "Could not fetch station data from Overpass endpoints.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        var imported = 0;

        foreach (var element in payload.Elements)
        {
            if (element.Lat is null || element.Lon is null)
            {
                continue;
            }

            var latitude = element.Lat.Value;
            var longitude = element.Lon.Value;

            if (!NisGeoFence.Contains(latitude, longitude))
            {
                continue;
            }

            var station = new Station
            {
                Id = $"osm-{element.Type}-{element.Id}",
                Name = ResolveStationName(element),
                Latitude = latitude,
                Longitude = longitude
            };

            await _redisService.HashSetFieldAsync(RedisKeys.Station(station.Id), "data", JsonSerializer.Serialize(station, JsonOptions), cancellationToken);
            await _redisService.SetAddAsync("stations:all", station.Id, cancellationToken);
            await _redisService.GeoAddAsync("stations:geo", station.Id, station.Latitude, station.Longitude, cancellationToken);
            imported++;
        }

        _logger.LogInformation("Imported {ImportedCount} Nis stations from OSM.", imported);
        var total = await _redisService.SetMembersAsync("stations:all", cancellationToken);
        return Ok(new { imported, skipped = false, totalStations = total.Count });
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SaveStation([FromBody] Station station, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(station.Id) || string.IsNullOrWhiteSpace(station.Name))
        {
            return Problem(title: "Invalid station", detail: "Station id and name are required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (station.Latitude < -90 || station.Latitude > 90 || station.Longitude < -180 || station.Longitude > 180)
        {
            return Problem(title: "Invalid station", detail: "Latitude/Longitude are out of range.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!NisGeoFence.Contains(station.Latitude, station.Longitude))
        {
            return Problem(title: "Out of area", detail: "Only Nis stations are accepted.", statusCode: StatusCodes.Status400BadRequest);
        }

        await _redisService.HashSetFieldAsync(RedisKeys.Station(station.Id), "data", JsonSerializer.Serialize(station, JsonOptions), cancellationToken);
        await _redisService.SetAddAsync("stations:all", station.Id, cancellationToken);
        await _redisService.GeoAddAsync("stations:geo", station.Id, station.Latitude, station.Longitude, cancellationToken);

        return CreatedAtAction(nameof(GetStation), new { id = station.Id }, station);
    }

    private static string ResolveStationName(OverpassElement element)
    {
        if (element.Tags is not null && element.Tags.TryGetValue("name", out var named) && !string.IsNullOrWhiteSpace(named))
        {
            return named.Trim();
        }

        if (element.Tags is not null && element.Tags.TryGetValue("ref", out var reference) && !string.IsNullOrWhiteSpace(reference))
        {
            return $"Stop {reference.Trim()}";
        }

        return $"Nis stop {element.Id}";
    }

    private sealed class OverpassResponse
    {
        public List<OverpassElement> Elements { get; set; } = new();
    }

    private sealed class OverpassElement
    {
        public string Type { get; set; } = string.Empty;
        public long Id { get; set; }
        public double? Lat { get; set; }
        public double? Lon { get; set; }
        public Dictionary<string, string>? Tags { get; set; }
    }
}








