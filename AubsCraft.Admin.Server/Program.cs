using System.Security.Claims;
using AubsCraft.Admin.Server;
using AubsCraft.Admin.Server.Hubs;
using AubsCraft.Admin.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.WebAssembly.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<RconService>();
builder.Services.AddSingleton<ActivityLogService>();
builder.Services.AddSingleton<ServerMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerMonitorService>());
builder.Services.AddHostedService<LogTailService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<PluginService>();
builder.Services.AddSingleton<PlayerStatsService>();
builder.Services.AddSingleton<ModrinthService>();
builder.Services.AddSingleton<ServerControlService>();
builder.Services.AddSingleton<WorldDataService>();
builder.Services.AddSignalR();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        // Return 401 for API calls instead of redirecting
        options.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/hubs"))
            {
                ctx.Response.StatusCode = 401;
                return Task.CompletedTask;
            }
            ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseWebSockets();
app.MapStaticAssets();
app.UseAuthentication();
app.UseAuthorization();

app.MapHub<ServerHub>("/hubs/server").RequireAuthorization();

// -- Auth Endpoints (anonymous) --
var auth = app.MapGroup("/api/auth");

auth.MapGet("/status", (AuthService authService, HttpContext ctx) =>
{
    return Results.Ok(new
    {
        authenticated = ctx.User.Identity?.IsAuthenticated == true,
        needsSetup = authService.NeedsSetup,
        username = ctx.User.Identity?.Name,
    });
});

auth.MapPost("/setup", async (LoginRequest req, AuthService authService) =>
{
    if (!authService.NeedsSetup)
        return Results.BadRequest(new { error = "Admin account already exists" });

    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { error = "Username and password are required" });

    await authService.CreateAdminAsync(req.Username.Trim(), req.Password);
    return Results.Ok(new { message = "Admin account created" });
});

auth.MapPost("/login", async (LoginRequest req, AuthService authService, HttpContext ctx) =>
{
    if (authService.NeedsSetup)
        return Results.BadRequest(new { error = "Run setup first" });

    var valid = await authService.ValidateAsync(req.Username, req.Password);
    if (!valid)
        return Results.Unauthorized();

    var claims = new List<Claim>
    {
        new(ClaimTypes.Name, req.Username),
        new(ClaimTypes.Role, "Admin"),
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    var principal = new ClaimsPrincipal(identity);

    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    return Results.Ok(new { message = "Logged in", username = req.Username });
});

auth.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { message = "Logged out" });
});

// -- Protected API endpoints (for initial data loads) --
var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet("/status", async (RconService rcon, ILogger<Program> log, CancellationToken ct) =>
{
    if (!rcon.IsConnected)
    {
        var connected = await rcon.ConnectAsync(ct);
        if (!connected) return Results.Ok(new { connected = false });
    }
    try
    {
        var players = await rcon.GetPlayersAsync(ct);
        var tps = await rcon.GetTpsAsync(ct);
        return Results.Ok(new
        {
            connected = true,
            players = new { players.Online, players.Max, players.Players },
            tps = new { tps.Tps1Min, tps.Tps5Min, tps.Tps15Min },
        });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Status fetch failed");
        return Results.Ok(new { connected = false });
    }
});

api.MapGet("/whitelist", async (RconService rcon, ILogger<Program> log, CancellationToken ct) =>
{
    try
    {
        var list = await rcon.GetWhitelistAsync(ct);
        return Results.Ok(list);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Whitelist fetch failed");
        return Results.Ok(new List<string>());
    }
});

api.MapGet("/banlist", async (RconService rcon, ILogger<Program> log, CancellationToken ct) =>
{
    try
    {
        var list = await rcon.GetBanListAsync(ct);
        return Results.Ok(list);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Banlist fetch failed");
        return Results.Ok(new List<string>());
    }
});

// -- World Data API (for 3D viewer) --
var world = app.MapGroup("/api/world").RequireAuthorization();

world.MapGet("/regions", (WorldDataService worldData) =>
{
    return Results.Ok(worldData.GetRegions());
});

world.MapGet("/chunks", (WorldDataService worldData) =>
{
    return Results.Ok(worldData.GetPopulatedChunks());
});

// Binary chunk endpoint - raw bytes, no base64, no JSON.
// Format: [int32 paletteCount][palette strings: int32 len + utf8 bytes each][ushort[] blocks]
world.MapGet("/chunk/{x:int}/{z:int}", (int x, int z, WorldDataService worldData, HttpContext ctx) =>
{
    var chunk = worldData.GetChunk(x, z);
    if (chunk == null) return Results.NotFound();

    ctx.Response.ContentType = "application/octet-stream";
    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);
    bw.Write(chunk.Palette.Count);
    foreach (var name in chunk.Palette)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        bw.Write(bytes.Length);
        bw.Write(bytes);
    }
    // Raw block data as ushort[]
    var blockBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes<ushort>(chunk.Blocks).ToArray();
    bw.Write(blockBytes);

    return Results.Bytes(ms.ToArray(), "application/octet-stream");
});

// atlas.rgba served as static file from wwwroot

// Binary WebSocket endpoint for camera-prioritized chunk streaming.
// Data flows as raw binary frames - no JSON, no base64 for bulk data.
// Client sends camera position (text JSON), server streams chunks sorted by distance.
world.MapGet("/ws", async (HttpContext ctx, WorldDataService worldData) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var chunks = worldData.GetPopulatedChunks();
    var camX = 0f;
    var camZ = 0f;
    var sendQueue = new List<ChunkCoord>(chunks);
    var sent = new HashSet<(int, int)>();
    var cts = new CancellationTokenSource();

    // Sort initial queue by distance from origin
    sendQueue.Sort((a, b) =>
    {
        var da = a.X * a.X + a.Z * a.Z;
        var db = b.X * b.X + b.Z * b.Z;
        return da.CompareTo(db);
    });

    // Background task: listen for camera position updates from client
    _ = Task.Run(async () =>
    {
        var buf = new byte[256];
        try
        {
            while (ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buf, cts.Token);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    cts.Cancel();
                    break;
                }
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Text)
                {
                    var json = System.Text.Encoding.UTF8.GetString(buf, 0, result.Count);
                    try
                    {
                        var msg = System.Text.Json.JsonDocument.Parse(json);
                        if (msg.RootElement.TryGetProperty("x", out var xp) &&
                            msg.RootElement.TryGetProperty("z", out var zp))
                        {
                            camX = xp.GetSingle();
                            camZ = zp.GetSingle();
                            // Re-sort unsent chunks by new camera position
                            lock (sendQueue)
                            {
                                sendQueue.RemoveAll(c => sent.Contains((c.X, c.Z)));
                                sendQueue.Sort((a, b) =>
                                {
                                    var da = (a.X * 16 - camX) * (a.X * 16 - camX) + (a.Z * 16 - camZ) * (a.Z * 16 - camZ);
                                    var db = (b.X * 16 - camX) * (b.X * 16 - camX) + (b.Z * 16 - camZ) * (b.Z * 16 - camZ);
                                    return da.CompareTo(db);
                                });
                            }
                        }
                    }
                    catch { } // ignore malformed messages
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    });

    // Send chunks as binary frames, closest to camera first
    while (ws.State == System.Net.WebSockets.WebSocketState.Open && !cts.IsCancellationRequested)
    {
        ChunkCoord? next = null;
        lock (sendQueue)
        {
            if (sendQueue.Count == 0) break;
            next = sendQueue[0];
            sendQueue.RemoveAt(0);
        }
        if (next == null) break;

        var hm = worldData.GetHeightmap(next.X, next.Z);
        if (hm == null)
        {
            sent.Add((next.X, next.Z));
            continue;
        }

        // Binary frame format:
        // [int32 cx][int32 cz][int32 paletteCount]
        // For each palette string: [int32 byteLength][utf8 bytes]
        // [int32[256] heights][ushort[256] blockIds]
        // [int32[256] seabedHeights][ushort[256] seabedBlockIds]
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(next.X);
        bw.Write(next.Z);
        bw.Write(hm.Palette.Count);
        foreach (var name in hm.Palette)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(name);
            bw.Write(bytes.Length);
            bw.Write(bytes);
        }
        foreach (var h in hm.Heights) bw.Write(h);
        foreach (var b in hm.BlockIds) bw.Write(b);
        foreach (var h in hm.SeabedHeights) bw.Write(h);
        foreach (var b in hm.SeabedBlockIds) bw.Write(b);

        var frame = ms.ToArray();
        try
        {
            await ws.SendAsync(frame, System.Net.WebSockets.WebSocketMessageType.Binary, true, cts.Token);
        }
        catch { break; }

        sent.Add((next.X, next.Z));
    }

    // Clean close
    if (ws.State == System.Net.WebSockets.WebSocketState.Open)
    {
        try { await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
        catch { }
    }
});

world.MapPost("/cache/clear", (WorldDataService worldData) =>
{
    worldData.ClearCache();
    return Results.Ok(new { message = "Cache cleared" });
});

// Fallback to WASM index.html for client-side routing
app.MapFallbackToFile("index.html");

app.Run();

// Request DTOs
public record LoginRequest(string Username, string Password);
