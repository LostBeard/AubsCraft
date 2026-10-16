using System.Net.WebSockets;
using AubsCraft.Admin.Server.Services;
using AubsCraft.Live.Model.Voice;

namespace AubsCraft.Admin.Server;

/// <summary>
/// The two sockets of voice (<see cref="VoiceHubService"/>): a logged-in viewer's browser (<c>/api/live/voice/ws?server=</c>) and the server's plugin (<c>/api/live/voice/plugin/{serverId}</c>: loopback and the
/// shared secret, like the live frames). Binary frames both ways (<see cref="VoiceFrames"/>).
/// </summary>
public static class VoiceEndpoints
{
    public static void MapVoiceEndpoints(this WebApplication app)
    {
        // The plugin's socket
        app.Map("/api/live/voice/plugin/{serverId}", async (string serverId, HttpContext ctx, LiveFeedService live, ServerManager servers, VoiceHubService hub) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            if (!LiveEndpoints.CameFromThisMachine(ctx.Connection.RemoteIpAddress, ctx.Request.Headers)) { ctx.Response.StatusCode = 403; return; }
            if (!live.VerifySecret(ctx.Request.Headers["X-Live-Secret"].ToString())) { ctx.Response.StatusCode = 401; return; }
            if (servers.Get(serverId) is not { } instance) { ctx.Response.StatusCode = 404; return; }

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var sender = new Sender(ws);
            await hub.SetPluginAsync(instance.Id, sender.SendAsync);
            try { await Receive(ws, ctx.RequestAborted, frame => hub.FromPluginAsync(instance.Id, frame)); }
            finally { await hub.SetPluginAsync(instance.Id, null); }
        });

        // A browser's socket
        app.Map("/api/live/voice/ws", async (HttpContext ctx, string? server, ServerManager servers, VoiceHubService hub) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            if (ctx.User.Identity?.IsAuthenticated != true) { ctx.Response.StatusCode = 401; return; }
            if (servers.Get(server) is not { } instance) { ctx.Response.StatusCode = 404; return; }

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var sender = new Sender(ws);
            var speaker = await hub.AddBrowserAsync(instance.Id, ctx.User.Identity.Name ?? "someone", sender.SendAsync);
            try { await Receive(ws, ctx.RequestAborted, frame => hub.FromBrowserAsync(speaker, frame)); }
            finally { await hub.RemoveBrowserAsync(speaker); }
        }).RequireAuthorization();
    }

    // One send at a time on a socket (the hub sends from whichever thread a frame arrives on)
    private sealed class Sender(WebSocket ws)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async ValueTask SendAsync(byte[] frame)
        {
            if (ws.State != WebSocketState.Open) return;
            await _gate.WaitAsync();
            try { await ws.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None); }
            finally { _gate.Release(); }
        }
    }

    private static async Task Receive(WebSocket ws, CancellationToken aborted, Func<byte[], Task> handle)
    {
        var buffer = new byte[VoiceFrames.MaxFrameBytes + 1];
        try
        {
            while (ws.State == WebSocketState.Open && !aborted.IsCancellationRequested)
            {
                int total = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), aborted);
                    total += result.Count;
                    if (total >= buffer.Length) { await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "too big", CancellationToken.None); return; }
                } while (!result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (ws.State == WebSocketState.CloseReceived) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    return;
                }
                if (result.MessageType == WebSocketMessageType.Binary && total > 0) await handle(buffer.AsSpan(0, total).ToArray());
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
    }
}
