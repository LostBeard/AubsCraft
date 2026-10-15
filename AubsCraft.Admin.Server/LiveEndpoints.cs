using System.Net;
using System.Net.WebSockets;
using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server;

/// <summary>
/// The live activity endpoints: the plugin's frames in (<c>POST /api/live/ingest/{serverId}</c>: loopback only, with the shared secret) and the
/// viewers' stream out (<c>/api/live/ws?server=</c>, logged-in users like the world socket). A frame is relayed as it came, once it parsed.
/// </summary>
public static class LiveEndpoints
{
    /// <summary>
    /// Whether a request is from a process on this machine (the plugin), not from the network: a loopback peer, and no X-Forwarded-For, which the
    /// reverse proxy in front of the panel adds to everything it relays (so the world cannot reach the endpoint by way of the proxy's own loopback).
    /// </summary>
    public static bool CameFromThisMachine(IPAddress? remote, IHeaderDictionary headers) =>
        remote != null && IPAddress.IsLoopback(remote) && !headers.ContainsKey("X-Forwarded-For");

    public static void MapLiveEndpoints(this WebApplication app)
    {
        // From the plugin on the same machine. Not behind the cookie login: it has none. The secret is the gate; "from this machine" is
        // the second one - a request that came through the reverse proxy carries X-Forwarded-For, and the plugin never does.
        app.MapPost("/api/live/ingest/{serverId}", async (string serverId, HttpContext ctx, LiveFeedService live, ServerManager servers) =>
        {
            if (!CameFromThisMachine(ctx.Connection.RemoteIpAddress, ctx.Request.Headers))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!live.VerifySecret(ctx.Request.Headers["X-Live-Secret"].ToString()))
                return Results.Unauthorized();
            if (servers.Get(serverId) is null) return Results.NotFound();
            if (ctx.Request.ContentLength is > LiveFeedService.MaxFrameBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            // Read at most MaxFrameBytes + 1: a body that is longer than allowed is refused without being buffered whole
            using var ms = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int n;
            while ((n = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
            {
                ms.Write(buffer, 0, n);
                if (ms.Length > LiveFeedService.MaxFrameBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            var error = live.Ingest(serverId, ms.ToArray());
            return error == null ? Results.NoContent() : Results.BadRequest(error);
        });

        var group = app.MapGroup("/api/live").RequireAuthorization();

        // Which servers have a live feed right now (the viewer shows its live layer only where there is one)
        group.MapGet("/status", (ServerManager servers, LiveFeedService live) =>
            Results.Ok(servers.All.ToDictionary(i => i.Id, i => live.StatusOf(i.Id))));

        // A player's skin (the plugin reports the texture server's address; the browser gets the PNG from the panel)
        group.MapGet("/skin", async (string? url, SkinProxyService skins, HttpContext ctx) =>
        {
            var (result, png) = await skins.GetAsync(url, ctx.RequestAborted);
            switch (result)
            {
                case SkinResult.Ok:
                    ctx.Response.Headers.CacheControl = "private, max-age=86400";
                    return Results.File(png!, "image/png");
                case SkinResult.NotFound: return Results.NotFound();
                case SkinResult.Refused: return Results.BadRequest();
                default: return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
        });

        group.MapGet("/ws", async (HttpContext ctx, string? server, ServerManager servers, LiveFeedService live) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            if (servers.Get(server) is not { } instance) { ctx.Response.StatusCode = 404; return; }

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            using var subscription = live.Subscribe(instance.Id);
            // Set when the client closes: stops the send loop. Never passed to a WebSocket call - cancelling a pending WebSocket send or
            // receive ABORTS the socket, which skips the close handshake (the browser sees 1006).
            using var clientClosed = new CancellationTokenSource();
            var aborted = ctx.RequestAborted;

            // The receive side only waits for the client's close (one ReceiveAsync at a time)
            var receiveLoop = Task.Run(async () =>
            {
                var buf = new byte[64];
                try
                {
                    while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
                    {
                        var result = await ws.ReceiveAsync(buf, aborted);
                        if (result.MessageType == WebSocketMessageType.Close) { clientClosed.Cancel(); break; }
                    }
                }
                catch (OperationCanceledException) { }
                catch (WebSocketException) { }
            });

            try
            {
                await foreach (var frame in subscription.Frames.ReadAllAsync(clientClosed.Token))
                {
                    if (ws.State != WebSocketState.Open) break;
                    await ws.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }

            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
                catch (WebSocketException) { }
            }
            await receiveLoop;
        });
    }
}
