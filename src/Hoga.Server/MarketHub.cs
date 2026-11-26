using Microsoft.AspNetCore.SignalR;

namespace Hoga.Server;

public sealed class MarketHub : Hub
{
    internal const string Group = "market";

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        try
        {
            var market = Context.GetHttpContext()!.RequestServices.GetRequiredService<MarketLoop>();
            await market.Unsubscribe(Context.ConnectionId);
        }
        catch (Exception ex) when (ex is MarketUnavailableException or TimeoutException or ObjectDisposedException) { }
    }

    public override async Task OnConnectedAsync()
    {
        try
        {
            var market = Context.GetHttpContext()!.RequestServices.GetRequiredService<MarketLoop>();
            if (!await market.Subscribe(Context.ConnectionId, Context.UserIdentifier, Context.User?.FindFirst("AspNet.Identity.SecurityStamp")?.Value, Context.ConnectionAborted)) Context.Abort();
        }
        catch (Exception ex) when (ex is MarketUnavailableException or TimeoutException)
        { Context.Abort(); }
    }
}
