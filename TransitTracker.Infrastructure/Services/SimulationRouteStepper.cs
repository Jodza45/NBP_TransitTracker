namespace TransitTracker.Infrastructure.Services;

public static class SimulationRouteStepper
{
    public static SimulationVehicleRouteState Advance(
        SimulationVehicleRouteState state,
        IReadOnlyList<RoutePoint> route,
        int tickMs,
        int dwellMs)
    {
        if (route.Count < 2)
        {
            return state;
        }

        if (state.DwellMsRemaining > 0)
        {
            return state with { DwellMsRemaining = Math.Max(0, state.DwellMsRemaining - tickMs) };
        }

        var current = Normalize(state, route.Count);
        var remainingMeters = current.SpeedMetersPerSecond * (tickMs / 1000d);

        while (remainingMeters > 0)
        {
            var from = route[current.FromIndex];
            var toIndex = GetToIndex(current, route.Count);
            var to = route[toIndex];

            var segmentLength = DistanceMeters(from, to);
            if (segmentLength < 0.5)
            {
                current = current with { Progress = 1 };
            }
            else
            {
                var progressDelta = remainingMeters / segmentLength;
                var capacity = 1d - current.Progress;

                if (progressDelta < capacity)
                {
                    return current with { Progress = current.Progress + progressDelta };
                }

                remainingMeters -= capacity * segmentLength;
                current = current with { Progress = 1 };
            }

            var advanced = AdvanceToNextSegment(current, route.Count, dwellMs);
            current = advanced.State;

            if (advanced.StopTick)
            {
                break;
            }
        }

        return current;
    }

    public static RoutePoint Interpolate(SimulationVehicleRouteState state, IReadOnlyList<RoutePoint> route)
    {
        if (route.Count == 0)
        {
            return new RoutePoint(0, 0);
        }

        if (route.Count == 1)
        {
            return route[0];
        }

        var current = Normalize(state, route.Count);
        var from = route[current.FromIndex];
        var to = route[GetToIndex(current, route.Count)];
        var t = Math.Clamp(current.Progress, 0d, 1d);

        return new RoutePoint(
            from.Latitude + ((to.Latitude - from.Latitude) * t),
            from.Longitude + ((to.Longitude - from.Longitude) * t));
    }

    private static SimulationVehicleRouteState Normalize(SimulationVehicleRouteState state, int routePointCount)
    {
        var direction = state.Direction < 0 ? -1 : 1;
        var fromIndex = Math.Clamp(state.FromIndex, 0, routePointCount - 1);

        if (direction > 0 && fromIndex >= routePointCount - 1)
        {
            fromIndex = routePointCount - 2;
        }

        if (direction < 0 && fromIndex <= 0)
        {
            direction = 1;
            fromIndex = 0;
        }

        return state with
        {
            FromIndex = fromIndex,
            Direction = direction,
            Progress = Math.Clamp(state.Progress, 0d, 1d),
            DwellMsRemaining = Math.Max(0, state.DwellMsRemaining)
        };
    }

    private static AdvanceResult AdvanceToNextSegment(SimulationVehicleRouteState state, int routePointCount, int dwellMs)
    {
        var arrivedIndex = GetToIndex(state, routePointCount);

        if (arrivedIndex == routePointCount - 1)
        {
            return new AdvanceResult(
                state with
                {
                    Direction = -1,
                    FromIndex = routePointCount - 1,
                    Progress = 0,
                    DwellMsRemaining = dwellMs
                },
                StopTick: true);
        }

        if (arrivedIndex == 0)
        {
            return new AdvanceResult(
                state with
                {
                    Direction = 1,
                    FromIndex = 0,
                    Progress = 0,
                    DwellMsRemaining = dwellMs
                },
                StopTick: true);
        }

        return new AdvanceResult(
            state with
            {
                FromIndex = arrivedIndex,
                Progress = 0,
                DwellMsRemaining = dwellMs
            },
            StopTick: dwellMs > 0);
    }

    private static int GetToIndex(SimulationVehicleRouteState state, int routePointCount)
    {
        var toIndex = state.FromIndex + state.Direction;
        if (toIndex < 0)
        {
            return 0;
        }

        if (toIndex >= routePointCount)
        {
            return routePointCount - 1;
        }

        return toIndex;
    }

    private static double DistanceMeters(RoutePoint a, RoutePoint b)
    {
        const double earthRadius = 6371000d;
        var dLat = ToRadians(b.Latitude - a.Latitude);
        var dLon = ToRadians(b.Longitude - a.Longitude);
        var lat1 = ToRadians(a.Latitude);
        var lat2 = ToRadians(b.Latitude);

        var haversine =
            Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
            Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        return 2d * earthRadius * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(1d - haversine));
    }

    private static double ToRadians(double value)
    {
        return value * Math.PI / 180d;
    }

    private sealed record AdvanceResult(SimulationVehicleRouteState State, bool StopTick);
}

public sealed record SimulationVehicleRouteState(
    int FromIndex,
    int Direction,
    double Progress,
    int DwellMsRemaining,
    double SpeedMetersPerSecond);
