using TransitTracker.Domain.Models;

namespace TransitTracker.Application.Interfaces;

public interface IEtaService
{
    Task<EtaResult> CalculateEtaAsync(string vehicleId, string stationId, CancellationToken cancellationToken);
}
