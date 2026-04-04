using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TransitTracker.Application.DTOs;
using TransitTracker.Application.Interfaces;

namespace TransitTracker.Infrastructure.Services;

public sealed class StreamService : IStreamService
{
    private readonly IConnectionMultiplexer _connectionMultiplexer;
    private readonly ILogger<StreamService> _logger;

    public StreamService(IConnectionMultiplexer connectionMultiplexer, ILogger<StreamService> logger)
    {
        _connectionMultiplexer = connectionMultiplexer;
        _logger = logger;
    }

    public async Task EnsureConsumerGroupAsync(string stream, string group, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var db = _connectionMultiplexer.GetDatabase();
        try
        {
            await db.StreamCreateConsumerGroupAsync(stream, group, "$", true);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase))
        {
            // group already exists
        }
    }

    public async Task<string> PublishAsync(string stream, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var db = _connectionMultiplexer.GetDatabase();
        var entries = values.Select(x => new NameValueEntry(x.Key, x.Value)).ToArray();
        var id = await db.StreamAddAsync(stream, entries);
        return id.ToString();
    }

    public async Task<IReadOnlyCollection<StreamEntryDto>> ReadGroupAsync(string stream, string group, string consumer, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var db = _connectionMultiplexer.GetDatabase();
        var messages = await db.StreamReadGroupAsync(stream, group, consumer, ">", count);

        _logger.LogInformation("Stream read: {Count} items from {Stream} for group {Group}", messages.Length, stream, group);

        return messages.Select(m => new StreamEntryDto
        {
            Id = m.Id.ToString(),
            Values = m.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString() ?? string.Empty)
        }).ToArray();
    }

    public async Task AckAsync(string stream, string group, string entryId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var db = _connectionMultiplexer.GetDatabase();
        await db.StreamAcknowledgeAsync(stream, group, entryId);
    }
}
