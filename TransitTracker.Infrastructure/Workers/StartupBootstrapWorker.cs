using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TransitTracker.Application.Interfaces;

namespace TransitTracker.Infrastructure.Workers;

public sealed class StartupBootstrapWorker : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly IRedisService _redisService;
    private readonly IGtfsImportService _gtfsImportService;
    private readonly ILogger<StartupBootstrapWorker> _logger;

    public StartupBootstrapWorker(
        IConfiguration configuration,
        IRedisService redisService,
        IGtfsImportService gtfsImportService,
        ILogger<StartupBootstrapWorker> logger)
    {
        _configuration = configuration;
        _redisService = redisService;
        _gtfsImportService = gtfsImportService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _configuration.GetValue<bool?>("Bootstrap:Enabled") ?? true;
        if (!enabled)
        {
            _logger.LogInformation("Startup bootstrap is disabled.");
            return;
        }

        var force = _configuration.GetValue<bool?>("Bootstrap:ForceGtfsImport") ?? false;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

            var lines = await _redisService.SetMembersAsync("lines:all", stoppingToken);
            var activeVehicles = await _redisService.SetMembersAsync("active-vehicles", stoppingToken);

            var shouldImport = force || lines.Count == 0 || activeVehicles.Count == 0;
            if (!shouldImport)
            {
                _logger.LogInformation("Startup bootstrap skipped. Existing lines={Lines}, activeVehicles={Vehicles}.", lines.Count, activeVehicles.Count);
                return;
            }

            var result = await _gtfsImportService.ImportNisAsync(force: true, stoppingToken);
            _logger.LogInformation(
                "Startup bootstrap completed. Source={Source}, Stations={Stations}, Lines={Lines}, DemoVehicles={Vehicles}.",
                result.Source,
                result.Stations,
                result.Lines,
                result.DemoVehicles);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup bootstrap failed.");
        }
    }
}
