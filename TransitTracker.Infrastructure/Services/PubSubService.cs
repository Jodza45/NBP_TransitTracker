using System.Runtime.CompilerServices;
using System.Threading.Channels;
using StackExchange.Redis;
using TransitTracker.Application.Interfaces;

namespace TransitTracker.Infrastructure.Services;

public sealed class PubSubService : IPubSubService
{
    private readonly IConnectionMultiplexer _connectionMultiplexer;

    public PubSubService(IConnectionMultiplexer connectionMultiplexer)
    {
        _connectionMultiplexer = connectionMultiplexer;
    }

    public async Task PublishAsync(string channel, string payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sub = _connectionMultiplexer.GetSubscriber();
        await sub.PublishAsync(RedisChannel.Literal(channel), payload);
    }

    public async IAsyncEnumerable<string> SubscribeAsync(string channel, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sub = _connectionMultiplexer.GetSubscriber();
        var queue = Channel.CreateUnbounded<string>();

        await sub.SubscribeAsync(RedisChannel.Literal(channel), (_, message) => queue.Writer.TryWrite(message!));

        try
        {
            while (await queue.Reader.WaitToReadAsync(cancellationToken))
            {
                while (queue.Reader.TryRead(out var msg))
                {
                    yield return msg;
                }
            }
        }
        finally
        {
            await sub.UnsubscribeAsync(RedisChannel.Literal(channel));
        }
    }
}
