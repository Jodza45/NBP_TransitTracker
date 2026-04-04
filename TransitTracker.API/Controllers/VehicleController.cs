using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using TransitTracker.API.Geo;
using TransitTracker.Application.Commands.UpdateVehicleLocation;
using TransitTracker.Application.DTOs;
using TransitTracker.Application.Queries.GetNearbyVehicles;
using TransitTracker.Application.Queries.GetVehicleLocation;

namespace TransitTracker.API.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/vehicles")]
[Route("api/v{version:apiVersion}/vehicles")]
public sealed class VehicleController : ControllerBase
{
    private readonly ISender _sender;

    public VehicleController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>
    /// Updates the latest location for a vehicle.
    /// </summary>
    [HttpPost("{id}/location")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateLocation(string id, [FromBody] VehicleLocationDto dto, CancellationToken cancellationToken)
    {
        if (!string.Equals(id, dto.VehicleId, StringComparison.OrdinalIgnoreCase))
        {
            return Problem(title: "Vehicle id mismatch", detail: "Route id must match body vehicleId.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!NisGeoFence.Contains(dto.Latitude, dto.Longitude))
        {
            return Problem(title: "Out of area", detail: "TransitTracker is currently limited to Nis city bounds.", statusCode: StatusCodes.Status400BadRequest);
        }

        var command = new UpdateVehicleLocationCommand(id, dto.Latitude, dto.Longitude, dto.Timestamp);
        var result = await _sender.Send(command, cancellationToken);

        if (!result.IsSuccess)
        {
            return Problem(title: "Location update failed", detail: result.Error, statusCode: StatusCodes.Status400BadRequest);
        }

        return Accepted();
    }

    /// <summary>
    /// Gets the current location for a vehicle.
    /// </summary>
    [HttpGet("{id}/location")]
    [ProducesResponseType(typeof(VehicleLocationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLocation(string id, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new GetVehicleLocationQuery(id), cancellationToken);

        if (!result.IsSuccess)
        {
            return Problem(title: "Vehicle location not found", detail: result.Error, statusCode: StatusCodes.Status404NotFound);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Gets nearby vehicles around the provided geo point.
    /// </summary>
    [HttpGet("nearby")]
    [ProducesResponseType(typeof(IReadOnlyCollection<NearbyVehicleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetNearby([FromQuery] double lat, [FromQuery] double lng, [FromQuery] double radius, CancellationToken cancellationToken)
    {
        if (!NisGeoFence.Contains(lat, lng))
        {
            return Problem(title: "Out of area", detail: "Search point must be inside Nis city bounds.", statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _sender.Send(new GetNearbyVehiclesQuery(lat, lng, radius), cancellationToken);

        if (!result.IsSuccess)
        {
            return Problem(title: "Nearby vehicle lookup failed", detail: result.Error, statusCode: StatusCodes.Status400BadRequest);
        }

        var filtered = result.Value.Where(v => NisGeoFence.Contains(v.Latitude, v.Longitude)).ToArray();
        return Ok(filtered);
    }
}
