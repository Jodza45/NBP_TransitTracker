using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using TransitTracker.Application.Queries.GetEta;

namespace TransitTracker.API.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/eta")]
[Route("api/v{version:apiVersion}/eta")]
public sealed class EtaController : ControllerBase
{
    private readonly ISender _sender;

    public EtaController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>
    /// Gets ETA in seconds for a vehicle to a station.
    /// </summary>
    [HttpGet("{vehicleId}/{stationId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEta(string vehicleId, string stationId, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new GetEtaQuery(vehicleId, stationId), cancellationToken);

        if (!result.IsSuccess)
        {
            return Problem(title: "ETA not found", detail: result.Error, statusCode: StatusCodes.Status404NotFound);
        }

        return Ok(result.Value);
    }
}
