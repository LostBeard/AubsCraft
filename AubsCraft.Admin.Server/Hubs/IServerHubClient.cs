using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server.Hubs;

/// <summary>
/// Strongly-typed SignalR client interface.
/// Defines all server-to-client push methods. Per-server pushes carry the server's id (ServerId).
/// </summary>
public interface IServerHubClient
{
    Task ReceiveServerStatus(ServerStatusDto status);
    Task ReceiveActivityEvent(ActivityEventDto evt);
    Task ReceiveChatMessage(ChatMessageDto msg);
    Task ReceiveTpsReading(TpsReadingDto reading);
    Task ReceiveServerList(List<ServerSummaryDto> servers);
    /// <summary>A line of progress from a long operation (the proxy cutover).</summary>
    Task ReceiveOperationProgress(OperationProgressDto progress);
}
