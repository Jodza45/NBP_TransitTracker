using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using TransitTracker.Application.Interfaces;
using TransitTracker.Domain.Models;
using TransitTracker.Infrastructure.Redis;

namespace TransitTracker.API.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/lines")]
[Route("api/v{version:apiVersion}/lines")]
public sealed class LineController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IRedisService _redisService;

    public LineController(IRedisService redisService)
    {
        _redisService = redisService;
    }

    /// <summary>
    /// Gets line details by ID.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Line), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLine(string id, CancellationToken cancellationToken)
    {
        var result = await _redisService.HashGetAsync(RedisKeys.Line(id), "data", cancellationToken);
        if (!result.IsSuccess)
        {
            return Problem(title: "Line not found", detail: result.Error, statusCode: StatusCodes.Status404NotFound);
        }

        var line = JsonSerializer.Deserialize<Line>(result.Value, JsonOptions)
            ?? throw new InvalidOperationException("Invalid line payload.");

        return Ok(line);
    }

    /// <summary>
    /// Creates or updates a line and its station set.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SaveLine([FromBody] Line line, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(line.Id) || string.IsNullOrWhiteSpace(line.Name))
        {
            return Problem(title: "Invalid line", detail: "Line id and name are required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (line.StationIds is null || line.StationIds.Count == 0)
        {
            return Problem(title: "Invalid line", detail: "At least one station id is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (line.StationIds.Count != line.StationIds.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            return Problem(title: "Invalid line", detail: "Station ids must be unique.", statusCode: StatusCodes.Status400BadRequest);
        }

        foreach (var stationId in line.StationIds)
        {
            var stationExists = await _redisService.HashGetAsync(RedisKeys.Station(stationId), "data", cancellationToken);
            if (!stationExists.IsSuccess)
            {
                return Problem(title: "Invalid line", detail: $"Station '{stationId}' does not exist.", statusCode: StatusCodes.Status400BadRequest);
            }
        }

        await _redisService.HashSetFieldAsync(RedisKeys.Line(line.Id), "data", JsonSerializer.Serialize(line, JsonOptions), cancellationToken);
        await _redisService.SetAddAsync("lines:all", line.Id, cancellationToken);

        foreach (var stationId in line.StationIds)
        {
            await _redisService.SetAddAsync($"line:{line.Id}:stations", stationId, cancellationToken);
        }

        return CreatedAtAction(nameof(GetLine), new { id = line.Id }, line);
    }
}
