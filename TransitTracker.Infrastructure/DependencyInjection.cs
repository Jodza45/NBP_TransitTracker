using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using TransitTracker.Application.Interfaces;
using TransitTracker.Infrastructure.Services;
using TransitTracker.Infrastructure.Workers;

namespace TransitTracker.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var redisConnectionString = configuration["Redis:ConnectionString"]
            ?? throw new InvalidOperationException("Redis connection string is not configured.");

        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnectionString));

        services.AddSingleton<IRedisService, RedisService>();
        services.AddSingleton<IGeoService, GeoService>();
        services.AddSingleton<IStreamService, StreamService>();
        services.AddSingleton<IPubSubService, PubSubService>();
        services.AddSingleton<EtaCalculationService>();
        services.AddSingleton<RouteGeometryService>();
        services.AddSingleton<IEtaService, EtaService>();
        services.AddSingleton<IGtfsImportService, GtfsImportService>();

        var disableBackgroundWorkers = configuration.GetValue<bool>("Infrastructure:DisableBackgroundWorkers");
        if (!disableBackgroundWorkers)
        {
            services.AddHostedService<StartupBootstrapWorker>();
            services.AddHostedService<LocationWorker>();
            services.AddHostedService<EtaWorker>();
            services.AddHostedService<NisSimulationWorker>();
        }

        return services;
    }
}
