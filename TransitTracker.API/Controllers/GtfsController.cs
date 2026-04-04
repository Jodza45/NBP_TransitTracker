using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using TransitTracker.Application.Interfaces;

namespace TransitTracker.API.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/gtfs")]
[Route("api/v{version:apiVersion}/gtfs")]
public sealed class GtfsController : ControllerBase
{
    private readonly IGtfsImportService _gtfsImportService;

    public GtfsController(IGtfsImportService gtfsImportService)
    {
        _gtfsImportService = gtfsImportService;
    }

    /// <summary>
    /// Imports Nis GTFS feed (stations, line stop sequences, and demo vehicles bound to lines).
    /// </summary>
    [HttpPost("import-nis")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ImportNisGtfs([FromQuery] bool force = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _gtfsImportService.ImportNisAsync(force, cancellationToken);
            return Ok(new
            {
                source = result.Source,
                stations = result.Stations,
                lines = result.Lines,
                demoVehicles = result.DemoVehicles,
                note = result.Note
            });
        }
        catch (Exception ex)
        {
            return Problem(
                title: "GTFS import failed",
                detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
