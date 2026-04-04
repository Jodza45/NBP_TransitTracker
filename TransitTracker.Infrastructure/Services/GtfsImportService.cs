using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TransitTracker.Application.Interfaces;
using TransitTracker.Domain.Models;
using TransitTracker.Infrastructure.Redis;

namespace TransitTracker.Infrastructure.Services;

public sealed class GtfsImportService : IGtfsImportService
{
    private const string DefaultNisGtfsUrl = "https://data.gov.rs/sr/datasets/r/b8fdbafe-10af-4b1a-a647-5f50e1dd33fb";

    private readonly IRedisService _redisService;
    private readonly IGeoService _geoService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GtfsImportService> _logger;

    public GtfsImportService(
        IRedisService redisService,
        IGeoService geoService,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<GtfsImportService> logger)
    {
        _redisService = redisService;
        _geoService = geoService;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<GtfsImportSummary> ImportNisAsync(bool force, CancellationToken cancellationToken)
    {
        var url = _configuration["Gtfs:NisUrl"] ?? DefaultNisGtfsUrl;

        var client = _httpClientFactory.CreateClient();
        using var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GTFS download failed with HTTP {(int)response.StatusCode}.");
        }

        await using var zipStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false);

        var stopsText = ReadRequiredEntry(archive, "stops.txt");
        var routesText = ReadRequiredEntry(archive, "routes.txt");
        var tripsText = ReadRequiredEntry(archive, "trips.txt");
        var stopTimesText = ReadRequiredEntry(archive, "stop_times.txt");

        var stops = ParseStops(stopsText);
        var routes = ParseRoutes(routesText).ToDictionary(x => x.RouteId, StringComparer.Ordinal);
        var trips = ParseTrips(tripsText);
        var stopTimesByTrip = ParseStopTimes(stopTimesText)
            .GroupBy(x => x.TripId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(x => x.StopSequence).Select(x => x.StopId).Distinct(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        var importedStations = 0;
        foreach (var stop in stops.Values)
        {
            if (!IsInNis(stop.Latitude, stop.Longitude))
            {
                continue;
            }

            await _redisService.HashSetFieldAsync(RedisKeys.Station(stop.Id), "data",
                JsonSerializer.Serialize(new Station
                {
                    Id = stop.Id,
                    Name = stop.Name,
                    Latitude = stop.Latitude,
                    Longitude = stop.Longitude
                }), cancellationToken);

            await _redisService.SetAddAsync("stations:all", stop.Id, cancellationToken);
            await _redisService.GeoAddAsync("stations:geo", stop.Id, stop.Latitude, stop.Longitude, cancellationToken);
            importedStations++;
        }

        var groupedTrips = trips
            .Where(t => stopTimesByTrip.ContainsKey(t.TripId))
            .GroupBy(t => (t.RouteId, t.DirectionId), t => t.TripId)
            .ToList();

        var importedLines = 0;
        var importedVehicles = 0;

        foreach (var group in groupedTrips)
        {
            if (!routes.TryGetValue(group.Key.RouteId, out var route))
            {
                continue;
            }

            var selectedTripId = group
                .OrderByDescending(tripId => stopTimesByTrip[tripId].Count)
                .FirstOrDefault();

            if (selectedTripId is null)
            {
                continue;
            }

            var stationIds = stopTimesByTrip[selectedTripId]
                .Where(stopId => stops.ContainsKey(stopId) && IsInNis(stops[stopId].Latitude, stops[stopId].Longitude))
                .ToList();

            if (stationIds.Count < 2)
            {
                continue;
            }

            var lineId = $"gtfs:{route.RouteId}:{group.Key.DirectionId}";
            var lineName = string.IsNullOrWhiteSpace(route.RouteShortName)
                ? $"{route.RouteLongName} (dir {group.Key.DirectionId})"
                : $"{route.RouteShortName} - {route.RouteLongName} (dir {group.Key.DirectionId})";

            var line = new Line
            {
                Id = lineId,
                Name = lineName,
                StationIds = stationIds
            };

            await _redisService.HashSetFieldAsync(RedisKeys.Line(line.Id), "data", JsonSerializer.Serialize(line), cancellationToken);
            await _redisService.SetAddAsync("lines:all", line.Id, cancellationToken);

            foreach (var stationId in line.StationIds)
            {
                await _redisService.SetAddAsync($"line:{line.Id}:stations", stationId, cancellationToken);
            }

            importedLines++;

            if (force)
            {
                var vehicleId = $"demo-{lineId.Replace(':', '-')}";
                var vehicle = new Vehicle
                {
                    Id = vehicleId,
                    LineId = line.Id,
                    Type = route.RouteType == 0 ? VehicleType.Tram : VehicleType.Bus,
                    IsActive = true
                };

                await _redisService.SetAsync(RedisKeys.Vehicle(vehicleId), vehicle, null, cancellationToken);
                await _redisService.SetAddAsync("active-vehicles", vehicleId, cancellationToken);

                var firstStation = stops[line.StationIds[0]];
                await _geoService.AddLocationAsync(vehicleId, firstStation.Latitude, firstStation.Longitude, cancellationToken);
                importedVehicles++;
            }
        }

        _logger.LogInformation("GTFS import done. Stations={Stations}, Lines={Lines}, Vehicles={Vehicles}, Url={Url}", importedStations, importedLines, importedVehicles, url);

        return new GtfsImportSummary(
            Source: url,
            Stations: importedStations,
            Lines: importedLines,
            DemoVehicles: importedVehicles,
            Note: "Use force=true to also seed demo vehicles bound to imported lines.");
    }

    private static bool IsInNis(double latitude, double longitude)
    {
        return latitude >= 43.255 && latitude <= 43.39 && longitude >= 21.78 && longitude <= 22.02;
    }

    private static string ReadRequiredEntry(ZipArchive archive, string fileName)
    {
        var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.Name, fileName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            throw new InvalidOperationException($"GTFS file '{fileName}' was not found in archive.");
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static Dictionary<string, GtfsStop> ParseStops(string csv)
    {
        var rows = ParseCsv(csv);
        return rows
            .Where(r => r.ContainsKey("stop_id") && r.ContainsKey("stop_name") && r.ContainsKey("stop_lat") && r.ContainsKey("stop_lon"))
            .Select(r => new GtfsStop(
                r["stop_id"],
                r["stop_name"],
                double.Parse(r["stop_lat"], CultureInfo.InvariantCulture),
                double.Parse(r["stop_lon"], CultureInfo.InvariantCulture)))
            .GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    private static IEnumerable<GtfsRoute> ParseRoutes(string csv)
    {
        var rows = ParseCsv(csv);
        foreach (var row in rows)
        {
            if (!row.TryGetValue("route_id", out var routeId))
            {
                continue;
            }

            row.TryGetValue("route_short_name", out var routeShort);
            row.TryGetValue("route_long_name", out var routeLong);
            row.TryGetValue("route_type", out var routeTypeRaw);

            yield return new GtfsRoute(
                routeId,
                routeShort ?? string.Empty,
                routeLong ?? string.Empty,
                int.TryParse(routeTypeRaw, out var routeType) ? routeType : 3);
        }
    }

    private static IEnumerable<GtfsTrip> ParseTrips(string csv)
    {
        var rows = ParseCsv(csv);
        foreach (var row in rows)
        {
            if (!row.TryGetValue("trip_id", out var tripId) || !row.TryGetValue("route_id", out var routeId))
            {
                continue;
            }

            row.TryGetValue("direction_id", out var directionRaw);
            var direction = int.TryParse(directionRaw, out var parsed) ? parsed : 0;
            yield return new GtfsTrip(tripId, routeId, direction);
        }
    }

    private static IEnumerable<GtfsStopTime> ParseStopTimes(string csv)
    {
        var rows = ParseCsv(csv);
        foreach (var row in rows)
        {
            if (!row.TryGetValue("trip_id", out var tripId) || !row.TryGetValue("stop_id", out var stopId) || !row.TryGetValue("stop_sequence", out var seqRaw))
            {
                continue;
            }

            if (!int.TryParse(seqRaw, out var seq))
            {
                continue;
            }

            yield return new GtfsStopTime(tripId, stopId, seq);
        }
    }

    private static List<Dictionary<string, string>> ParseCsv(string csv)
    {
        var lines = csv
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        if (lines.Count == 0)
        {
            return [];
        }

        var headers = ParseCsvLine(lines[0]);
        var result = new List<Dictionary<string, string>>();

        for (var i = 1; i < lines.Count; i++)
        {
            var values = ParseCsvLine(lines[i]);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < headers.Count; index++)
            {
                var key = headers[index];
                var value = index < values.Count ? values[index] : string.Empty;
                row[key] = value;
            }

            result.Add(row);
        }

        return result;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];

            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                values.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(ch);
        }

        values.Add(sb.ToString());
        return values;
    }

    private sealed record GtfsStop(string Id, string Name, double Latitude, double Longitude);
    private sealed record GtfsRoute(string RouteId, string RouteShortName, string RouteLongName, int RouteType);
    private sealed record GtfsTrip(string TripId, string RouteId, int DirectionId);
    private sealed record GtfsStopTime(string TripId, string StopId, int StopSequence);
}
