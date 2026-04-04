using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TransitTracker.Application.Common;
using TransitTracker.Application.Interfaces;
using TransitTracker.Domain.Models;

namespace TransitTracker.Tests.Integration;

public sealed class ApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        var appFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Infrastructure:DisableBackgroundWorkers"] = "true"
                });
            });

            builder.ConfigureServices(services =>
            {
                var redisDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IRedisService));
                if (redisDescriptor is not null)
                {
                    services.Remove(redisDescriptor);
                }

                var etaDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IEtaService));
                if (etaDescriptor is not null)
                {
                    services.Remove(etaDescriptor);
                }

                services.AddSingleton<IRedisService, InMemoryRedisService>();
                services.AddSingleton<IEtaService, FakeEtaService>();
            });
        });

        _client = appFactory.CreateClient();
    }

    [Fact]
    public async Task Station_Post_Then_Get_ShouldSucceed()
    {
        var payload = new Station
        {
            Id = "s-test",
            Name = "Test Station",
            Latitude = 43.3209,
            Longitude = 21.8958
        };

        var post = await _client.PostAsync("/api/stations", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        post.EnsureSuccessStatusCode();

        var get = await _client.GetAsync("/api/stations/s-test");
        get.EnsureSuccessStatusCode();

        var content = await get.Content.ReadAsStringAsync();
        content.Should().Contain("s-test");
    }

    [Fact]
    public async Task Health_Endpoint_ShouldRespond()
    {
        var response = await _client.GetAsync("/health");
        response.StatusCode.Should().BeOneOf(System.Net.HttpStatusCode.OK, System.Net.HttpStatusCode.ServiceUnavailable);
    }

    private sealed class FakeEtaService : IEtaService
    {
        public Task<EtaResult> CalculateEtaAsync(string vehicleId, string stationId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new EtaResult
            {
                VehicleId = vehicleId,
                StationId = stationId,
                EstimatedSeconds = 300
            });
        }
    }

    private sealed class InMemoryRedisService : IRedisService
    {
        private readonly Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, string>> _hashes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _sets = new(StringComparer.OrdinalIgnoreCase);

        public Task<Result<T>> GetAsync<T>(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_strings.TryGetValue(key, out var value))
            {
                return Task.FromResult(Result<T>.Failure("Not found"));
            }

            var parsed = JsonSerializer.Deserialize<T>(value) ?? throw new InvalidOperationException("Invalid payload");
            return Task.FromResult(Result<T>.Success(parsed));
        }

        public Task SetAsync<T>(string key, T value, TimeSpan? ttl, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _strings[key] = JsonSerializer.Serialize(value);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _strings.Remove(key);
            _hashes.Remove(key);
            _sets.Remove(key);
            return Task.CompletedTask;
        }

        public Task HashSetFieldAsync(string key, string field, string value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_hashes.TryGetValue(key, out var hash))
            {
                hash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _hashes[key] = hash;
            }

            hash[field] = value;
            return Task.CompletedTask;
        }

        public Task<Result<string>> HashGetAsync(string key, string field, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_hashes.TryGetValue(key, out var hash) && hash.TryGetValue(field, out var value))
            {
                return Task.FromResult(Result<string>.Success(value));
            }

            return Task.FromResult(Result<string>.Failure("Not found"));
        }

        public Task HashSetAsync(string key, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _hashes[key] = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
            return Task.CompletedTask;
        }

        public Task<Result<IReadOnlyDictionary<string, string>>> HashGetAllAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_hashes.TryGetValue(key, out var value))
            {
                return Task.FromResult(Result<IReadOnlyDictionary<string, string>>.Success(value));
            }

            return Task.FromResult(Result<IReadOnlyDictionary<string, string>>.Failure("Not found"));
        }

        public Task SetAddAsync(string key, string value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_sets.TryGetValue(key, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _sets[key] = set;
            }

            set.Add(value);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyCollection<string>> SetMembersAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var members = _sets.TryGetValue(key, out var set) ? set.ToArray() : Array.Empty<string>();
            return Task.FromResult<IReadOnlyCollection<string>>(members);
        }

        public Task KeyExpireAsync(string key, TimeSpan ttl, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task GeoAddAsync(string key, string member, double latitude, double longitude, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SetAddAsync(key, member, cancellationToken);
        }

        public Task<IReadOnlyCollection<(string Member, double DistanceMeters)>> GeoRadiusAsync(string key, double latitude, double longitude, double radiusMeters, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_sets.TryGetValue(key, out var set))
            {
                return Task.FromResult<IReadOnlyCollection<(string Member, double DistanceMeters)>>(Array.Empty<(string Member, double DistanceMeters)>());
            }

            var values = set.Select(x => (Member: x, DistanceMeters: 0d)).ToArray();
            return Task.FromResult<IReadOnlyCollection<(string Member, double DistanceMeters)>>(values);
        }
    }
}
