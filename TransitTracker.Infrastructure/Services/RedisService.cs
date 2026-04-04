using System.Globalization;
using System.Text.Json;
using StackExchange.Redis;
using TransitTracker.Application.Common;
using TransitTracker.Application.Interfaces;

namespace TransitTracker.Infrastructure.Services;

public sealed class RedisService : IRedisService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IConnectionMultiplexer _connectionMultiplexer;

    public RedisService(IConnectionMultiplexer connectionMultiplexer)
    {
        _connectionMultiplexer = connectionMultiplexer;
    }

    public async Task<Result<T>> GetAsync<T>(string key, CancellationToken cancellationToken)
    {
        var value = await ExecuteWithRetryAsync(async db => await db.StringGetAsync(key), cancellationToken);

        if (!value.HasValue)
        {
            return Result<T>.Failure($"Key '{key}' was not found.");
        }

        var parsed = JsonSerializer.Deserialize<T>(value!, JsonOptions)
            ?? throw new InvalidOperationException($"Cannot deserialize key '{key}'.");

        return Result<T>.Success(parsed);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(value, JsonOptions);
        await ExecuteWithRetryAsync(async db =>
        {
            await db.StringSetAsync(key, payload, ttl);
            return true;
        }, cancellationToken);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        await ExecuteWithRetryAsync(async db =>
        {
            await db.KeyDeleteAsync(key);
            return true;
        }, cancellationToken);
    }

    public async Task HashSetFieldAsync(string key, string field, string value, CancellationToken cancellationToken)
    {
        await ExecuteWithRetryAsync(async db =>
        {
            await db.HashSetAsync(key, field, value);
            return true;
        }, cancellationToken);
    }

    public async Task<Result<string>> HashGetAsync(string key, string field, CancellationToken cancellationToken)
    {
        var value = await ExecuteWithRetryAsync(async db => await db.HashGetAsync(key, field), cancellationToken);
        return value.HasValue
            ? Result<string>.Success(value!)
            : Result<string>.Failure($"Field '{field}' was not found for key '{key}'.");
    }

    public async Task HashSetAsync(string key, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        var entries = values.Select(x => new HashEntry(x.Key, x.Value)).ToArray();
        await ExecuteWithRetryAsync(async db =>
        {
            await db.HashSetAsync(key, entries);
            return true;
        }, cancellationToken);
    }

    public async Task<Result<IReadOnlyDictionary<string, string>>> HashGetAllAsync(string key, CancellationToken cancellationToken)
    {
        var entries = await ExecuteWithRetryAsync(async db => await db.HashGetAllAsync(key), cancellationToken);

        if (entries.Length == 0)
        {
            return Result<IReadOnlyDictionary<string, string>>.Failure($"Hash '{key}' was not found.");
        }

        var map = entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
        return Result<IReadOnlyDictionary<string, string>>.Success(map);
    }

    public async Task SetAddAsync(string key, string value, CancellationToken cancellationToken)
    {
        await ExecuteWithRetryAsync(async db =>
        {
            await db.SetAddAsync(key, value);
            return true;
        }, cancellationToken);
    }

    public async Task<IReadOnlyCollection<string>> SetMembersAsync(string key, CancellationToken cancellationToken)
    {
        var members = await ExecuteWithRetryAsync(async db => await db.SetMembersAsync(key), cancellationToken);
        return members.Select(x => x.ToString()).ToArray();
    }

    public async Task KeyExpireAsync(string key, TimeSpan ttl, CancellationToken cancellationToken)
    {
        await ExecuteWithRetryAsync(async db =>
        {
            await db.KeyExpireAsync(key, ttl);
            return true;
        }, cancellationToken);
    }

    public async Task GeoAddAsync(string key, string member, double latitude, double longitude, CancellationToken cancellationToken)
    {
        await ExecuteWithRetryAsync(async db =>
        {
            await db.GeoAddAsync(key, longitude, latitude, member);
            return true;
        }, cancellationToken);
    }

    public async Task<IReadOnlyCollection<(string Member, double DistanceMeters)>> GeoRadiusAsync(string key, double latitude, double longitude, double radiusMeters, CancellationToken cancellationToken)
    {
        var result = await ExecuteWithRetryAsync(
            async db => await db.ExecuteAsync(
                "GEORADIUS",
                key,
                longitude.ToString(CultureInfo.InvariantCulture),
                latitude.ToString(CultureInfo.InvariantCulture),
                radiusMeters.ToString(CultureInfo.InvariantCulture),
                "m",
                "WITHDIST"),
            cancellationToken);

        if (result.IsNull)
        {
            return Array.Empty<(string Member, double DistanceMeters)>();
        }

        var rows = (RedisResult[])result!;
        var items = new List<(string Member, double DistanceMeters)>(rows.Length);

        foreach (var row in rows)
        {
            var values = (RedisResult[])row!;
            var member = (string)values[0]!;
            var distance = double.Parse((string)values[1]!, CultureInfo.InvariantCulture);
            items.Add((member, distance));
        }

        return items;
    }

    private async Task<T> ExecuteWithRetryAsync<T>(Func<IDatabase, Task<T>> action, CancellationToken cancellationToken)
    {
        const int maxRetries = 3;
        var db = _connectionMultiplexer.GetDatabase();

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await action(db);
            }
            catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException)
            {
                if (attempt >= maxRetries)
                {
                    throw;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken);
            }
        }
    }
}
