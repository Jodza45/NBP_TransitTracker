using TransitTracker.Application.Common;

namespace TransitTracker.Application.Interfaces;

public interface IRedisService
{
    Task<Result<T>> GetAsync<T>(string key, CancellationToken cancellationToken);
    Task SetAsync<T>(string key, T value, TimeSpan? ttl, CancellationToken cancellationToken);
    Task DeleteAsync(string key, CancellationToken cancellationToken);
    Task HashSetFieldAsync(string key, string field, string value, CancellationToken cancellationToken);
    Task<Result<string>> HashGetAsync(string key, string field, CancellationToken cancellationToken);
    Task HashSetAsync(string key, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken);
    Task<Result<IReadOnlyDictionary<string, string>>> HashGetAllAsync(string key, CancellationToken cancellationToken);
    Task SetAddAsync(string key, string value, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<string>> SetMembersAsync(string key, CancellationToken cancellationToken);
    Task KeyExpireAsync(string key, TimeSpan ttl, CancellationToken cancellationToken);
    Task GeoAddAsync(string key, string member, double latitude, double longitude, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<(string Member, double DistanceMeters)>> GeoRadiusAsync(string key, double latitude, double longitude, double radiusMeters, CancellationToken cancellationToken);
}
