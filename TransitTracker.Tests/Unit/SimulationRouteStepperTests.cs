using FluentAssertions;
using TransitTracker.Infrastructure.Services;

namespace TransitTracker.Tests.Unit;

public sealed class SimulationRouteStepperTests
{
    [Fact]
    public void Advance_WhenVehicleReachesLastPoint_ShouldContinueBackTowardPreviousPoint()
    {
        var route = new[]
        {
            new RoutePoint(43.3200, 21.9000),
            new RoutePoint(43.3210, 21.9010),
            new RoutePoint(43.3220, 21.9020)
        };

        var state = new SimulationVehicleRouteState(
            FromIndex: 1,
            Direction: 1,
            Progress: 0.99,
            DwellMsRemaining: 0,
            SpeedMetersPerSecond: 20);

        var atTerminal = SimulationRouteStepper.Advance(state, route, tickMs: 1000, dwellMs: 0);
        var movingBack = SimulationRouteStepper.Advance(atTerminal, route, tickMs: 1000, dwellMs: 0);
        var position = SimulationRouteStepper.Interpolate(movingBack, route);

        atTerminal.Direction.Should().Be(-1);
        atTerminal.FromIndex.Should().Be(2);
        movingBack.Direction.Should().Be(-1);
        movingBack.FromIndex.Should().Be(2);
        position.Latitude.Should().BeLessThan(route[2].Latitude);
        position.Latitude.Should().BeGreaterThan(route[0].Latitude);
    }
}
