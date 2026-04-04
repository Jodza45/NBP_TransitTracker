namespace TransitTracker.Application.Interfaces;

public interface IGtfsImportService
{
    Task<GtfsImportSummary> ImportNisAsync(bool force, CancellationToken cancellationToken);
}

public sealed record GtfsImportSummary(
    string Source,
    int Stations,
    int Lines,
    int DemoVehicles,
    string Note);
