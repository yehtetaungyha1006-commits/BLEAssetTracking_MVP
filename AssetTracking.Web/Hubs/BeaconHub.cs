using Microsoft.AspNetCore.SignalR;

namespace AssetTracking.Web.Hubs
{
    public class BeaconHub : Hub
    {
        // No client-to-server methods are required for this MVP,
        // as the hub is used exclusively for broadcasting updates to clients.
    }
}
