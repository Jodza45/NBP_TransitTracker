namespace TransitTracker.Application.Interfaces;

public interface IPubSubService
{
    Task PublishAsync(string channel, string payload, CancellationToken cancellationToken);
    IAsyncEnumerable<string> SubscribeAsync(string channel, CancellationToken cancellationToken);
}
