using AubsCraft.Admin.Server.Models;

namespace AubsCraft.Admin.Server.Hubs;

/// <summary>
/// Strongly-typed SignalR client interface.
/// Defines all server-to-client push methods.
/// </summary>
public interface IServerHubClient
{
    Task ReceiveServerStatus(ServerStatusDto status);
    Task ReceiveActivityEvent(ActivityEventDto evt);
    Task ReceiveChatMessage(ChatMessageDto msg);
    Task ReceiveTpsReading(TpsReadingDto reading);
}
