using FluentAssertions;
using TransitTracker.Domain.Models;
using TransitTracker.Infrastructure.Services;

namespace TransitTracker.Tests.Unit;

public sealed class EtaCalculationServiceTests
{
    private readonly EtaCalculationService _service = new();

    [Fact]
    public void CalculateSeconds_WhenVehicleOnStation_ReturnsZero()
    {
        var location = new VehicleLocation { VehicleId = "v1", Latitude = 44.8176, Longitude = 20.4633, Timestamp = DateTimeOffset.UtcNow };
        var station = new Station { Id = "s1", Name = "Central", Latitude = 44.8176, Longitude = 20.4633 };

        var result = _service.CalculateSeconds(location, station, VehicleType.Bus);

        result.Should().Be(0);
    }

    [Fact]
    public void CalculateSeconds_Tram_ShouldBeFasterThanBus_ForSameDistance()
    {
        var location = new VehicleLocation { VehicleId = "v1", Latitude = 44.8176, Longitude = 20.4633, Timestamp = DateTimeOffset.UtcNow };
        var station = new Station { Id = "s2", Name = "Republic", Latitude = 44.8055, Longitude = 20.4762 };

        var busSeconds = _service.CalculateSeconds(location, station, VehicleType.Bus);
        var tramSeconds = _service.CalculateSeconds(location, station, VehicleType.Tram);

        tramSeconds.Should().BeLessThan(busSeconds);
        busSeconds.Should().BeGreaterThan(0);
    }
}
