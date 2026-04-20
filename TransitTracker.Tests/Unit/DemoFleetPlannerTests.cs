using FluentAssertions;
using TransitTracker.Infrastructure.Services;

namespace TransitTracker.Tests.Unit;

public sealed class DemoFleetPlannerTests
{
    [Theory]
    [InlineData("1", 3)]
    [InlineData("2", 3)]
    [InlineData("5", 3)]
    [InlineData("6", 3)]
    [InlineData("34A", 3)]
    [InlineData("34B", 3)]
    [InlineData("3", 2)]
    [InlineData("4", 2)]
    [InlineData("10", 2)]
    [InlineData("12", 2)]
    [InlineData("11A", 1)]
    [InlineData("36", 1)]
    public void VehiclesPerDirection_ShouldMatchNisDemoFleetProfile(string routeShortName, int expected)
    {
        DemoFleetPlanner.GetVehiclesPerDirection(routeShortName).Should().Be(expected);
    }

    [Fact]
    public void GetDepartureOffset_ShouldStaggerVehiclesByTenMinutes()
    {
        DemoFleetPlanner.GetDepartureOffset(1).Should().Be(TimeSpan.Zero);
        DemoFleetPlanner.GetDepartureOffset(2).Should().Be(TimeSpan.FromMinutes(10));
        DemoFleetPlanner.GetDepartureOffset(3).Should().Be(TimeSpan.FromMinutes(20));
    }

    [Fact]
    public void TryGetVehicleSequence_ShouldReadSequenceFromDemoVehicleId()
    {
        DemoFleetPlanner.TryGetVehicleSequence("demo-gtfs-34A-1-3", out var sequence).Should().BeTrue();
        sequence.Should().Be(3);
    }
}
