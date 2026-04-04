using Microsoft.AspNetCore.SignalR;
using TransitTracker.Application.DTOs;

namespace TransitTracker.Infrastructure.Hubs;

public sealed class VehicleLocationHub : Hub
{
    public Task SubscribeToLine(string lineId)
    {
        return Groups.AddToGroupAsync(Context.ConnectionId, $"line:{lineId}");
    }

    public Task UnsubscribeFromLine(string lineId)
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, $"line:{lineId}");
    }

    public Task ReceiveVehicleLocation(VehicleLocationDto dto)
    {
        return Task.CompletedTask;
    }
}
