using TransitTracker.Application.DTOs;

namespace TransitTracker.Application.Interfaces;

public interface IStreamService
{
    Task EnsureConsumerGroupAsync(string stream, string group, CancellationToken cancellationToken);
    Task SetGroupToLatestAsync(string stream, string group, CancellationToken cancellationToken);
    Task<string> PublishAsync(string stream, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<StreamEntryDto>> ReadGroupAsync(string stream, string group, string consumer, int count, CancellationToken cancellationToken);
    Task AckAsync(string stream, string group, string entryId, CancellationToken cancellationToken);
}
