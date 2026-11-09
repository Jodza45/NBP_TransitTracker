using System.Globalization;
using System.Text;
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

    [HttpPost("cleanup")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CleanupStations([FromQuery] double dedupRadiusMeters = 65, [FromQuery] bool removeUnassigned = true, CancellationToken cancellationToken = default)
    {
        if (dedupRadiusMeters <= 0 || dedupRadiusMeters > 500)
        {
            return Problem(title: "Invalid radius", detail: "Dedup radius must be between 1 and 500 meters.", statusCode: StatusCodes.Status400BadRequest);
        }

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

        var duplicateToCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var groups = stations
            .GroupBy(x => NormalizeName(x.Name), StringComparer.Ordinal)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .ToList();

        foreach (var group in groups)
        {
            var ordered = group.OrderByDescending(StationPriority).ToList();
            var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var baseStation in ordered)
            {
                if (!consumed.Add(baseStation.Id))
                {
                    continue;
                }

                var cluster = new List<Station> { baseStation };

                foreach (var candidate in ordered)
                {
                    if (consumed.Contains(candidate.Id))
                    {
                        continue;
                    }

                    var distance = DistanceMeters(baseStation.Latitude, baseStation.Longitude, candidate.Latitude, candidate.Longitude);
                    if (distance <= dedupRadiusMeters)
                    {
                        consumed.Add(candidate.Id);
                        cluster.Add(candidate);
                    }
                }

                if (cluster.Count <= 1)
                {
                    continue;
                }

                var canonical = cluster.OrderByDescending(StationPriority).First();
                foreach (var duplicate in cluster.Where(x => !string.Equals(x.Id, canonical.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    duplicateToCanonical[duplicate.Id] = canonical.Id;
                }
            }
        }

        var lineIds = await _redisService.SetMembersAsync("lines:all", cancellationToken);
        var usedStationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var updatedLines = 0;

        foreach (var lineId in lineIds)
        {
            var lineResult = await _redisService.HashGetAsync(RedisKeys.Line(lineId), "data", cancellationToken);
            if (!lineResult.IsSuccess)
            {
                continue;
            }

            var line = JsonSerializer.Deserialize<Line>(lineResult.Value, JsonOptions);
            if (line is null || line.StationIds.Count == 0)
            {
                continue;
            }

            var rewritten = new List<string>(line.StationIds.Count);
            string? previous = null;

            foreach (var stationId in line.StationIds)
            {
                var mapped = duplicateToCanonical.TryGetValue(stationId, out var canonical) ? canonical : stationId;
                if (string.Equals(previous, mapped, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                rewritten.Add(mapped);
                usedStationIds.Add(mapped);
                previous = mapped;
            }

            if (!line.StationIds.SequenceEqual(rewritten, StringComparer.OrdinalIgnoreCase))
            {
                line.StationIds = rewritten;
                await _redisService.HashSetFieldAsync(RedisKeys.Line(line.Id), "data", JsonSerializer.Serialize(line, JsonOptions), cancellationToken);

                await _redisService.DeleteAsync($"line:{line.Id}:stations", cancellationToken);
                foreach (var stationId in rewritten)
                {
                    await _redisService.SetAddAsync($"line:{line.Id}:stations", stationId, cancellationToken);
                }

                updatedLines++;
            }
        }

        var removedStations = 0;
        foreach (var duplicateId in duplicateToCanonical.Keys)
        {
            await _redisService.DeleteAsync(RedisKeys.Station(duplicateId), cancellationToken);
            removedStations++;
        }

        if (removeUnassigned)
        {
            var allAfterDedup = await _redisService.SetMembersAsync("stations:all", cancellationToken);
            foreach (var stationId in allAfterDedup)
            {
                if (usedStationIds.Contains(stationId))
                {
                    continue;
                }

                await _redisService.DeleteAsync(RedisKeys.Station(stationId), cancellationToken);
                removedStations++;
            }
        }

        var allStationsAfterCleanup = await _redisService.SetMembersAsync("stations:all", cancellationToken);

        await _redisService.DeleteAsync("stations:all", cancellationToken);
        await _redisService.DeleteAsync("stations:geo", cancellationToken);

        var reindexed = 0;
        foreach (var stationId in allStationsAfterCleanup)
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

            await _redisService.SetAddAsync("stations:all", station.Id, cancellationToken);
            await _redisService.GeoAddAsync("stations:geo", station.Id, station.Latitude, station.Longitude, cancellationToken);
            reindexed++;
        }

        _logger.LogInformation(
            "Station cleanup completed. DuplicateMappings={Mappings}, RemovedStations={Removed}, UpdatedLines={UpdatedLines}, RemainingStations={Remaining}.",
            duplicateToCanonical.Count,
            removedStations,
            updatedLines,
            reindexed);

        return Ok(new
        {
            duplicateMappings = duplicateToCanonical.Count,
            removedStations,
            updatedLines,
            remainingStations = reindexed
        });
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
                var etaKey = RedisKeys.Eta(vehicleId, id);
                var cachedEtaResult = await _redisService.HashGetAsync(etaKey, "data", cancellationToken);

                if (cachedEtaResult.IsSuccess)
                {
                    var cachedEta = JsonSerializer.Deserialize<EtaResult>(cachedEtaResult.Value, JsonOptions);
                    if (cachedEta != null)
                    {
                        arrivals.Add(new StationArrivalDto
                        {
                            VehicleId = vehicleId,
                            LineId = vehicleResult.Value.LineId,
                            EstimatedSeconds = cachedEta.EstimatedSeconds
                        });
                        continue;
                    }
                }

                // Fallback na živo računanje ukoliko keš ne postoji ili je istekao
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

    private static int StationPriority(Station station)
    {
        if (station.Id.StartsWith("gtfs:", StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (!station.Id.StartsWith("osm-", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (!station.Name.StartsWith("Nis stop", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 1;
    }

    private static string NormalizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var normalized = raw.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToUpperInvariant(ch));
            }
            else
            {
                sb.Append(' ');
            }
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static double DistanceMeters(double lat1, double lng1, double lat2, double lng2)
    {
        const double earthRadius = 6371000;
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLng = DegreesToRadians(lng2 - lng1);
        var a =
            Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
            Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
            Math.Sin(dLng / 2) * Math.Sin(dLng / 2);

        return 2 * earthRadius * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double DegreesToRadians(double degrees)
    {
        return degrees * (Math.PI / 180);
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
