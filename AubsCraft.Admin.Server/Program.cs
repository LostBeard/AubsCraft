using System.Security.Claims;
using AubsCraft.Admin.Server;
using AubsCraft.Admin.Server.Hubs;
using AubsCraft.Admin.Server.Models;
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
builder.Services.AddSingleton<InviteCodeService>();
builder.Services.AddSingleton<WhitelistAuditService>();
builder.Services.AddSingleton<EmailNotificationService>();
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
        options.Events.OnRedirectToAccessDenied = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/hubs"))
            {
                ctx.Response.StatusCode = 403;
                return Task.CompletedTask;
            }
            ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// Eagerly resolve EmailNotificationService so it subscribes to ActivityLog events at startup.
app.Services.GetRequiredService<EmailNotificationService>();

app.UseWebSockets();
app.MapStaticAssets();
app.UseAuthentication();
app.UseAuthorization();

app.MapHub<ServerHub>("/hubs/server").RequireAuthorization();

// -- Public endpoints (anonymous) --
var publicApi = app.MapGroup("/api/public");

publicApi.MapGet("/status", (ServerMonitorService monitor) =>
{
    var s = monitor.LastStatus;
    if (s == null)
        return Results.Ok(new PublicStatusDto(false, 0, 0, new List<string>()));
    return Results.Ok(new PublicStatusDto(s.Connected, s.Online, s.Max, s.Players));
});

// -- Auth endpoints (anonymous) --
var auth = app.MapGroup("/api/auth");

auth.MapGet("/status", (AuthService authService, HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name;
    string? role = null;
    if (ctx.User.Identity?.IsAuthenticated == true && username != null)
        role = authService.GetUser(username)?.Role;

    return Results.Ok(new AuthStatusDto(
        Authenticated: ctx.User.Identity?.IsAuthenticated == true,
        NeedsSetup: authService.NeedsSetup,
        Username: username,
        Role: role));
});

auth.MapPost("/setup", async (LoginRequest req, AuthService authService) =>
{
    if (!authService.NeedsSetup)
        return Results.BadRequest(new { error = "Owner account already exists" });
    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { error = "Username and password are required" });

    await authService.CreateOwnerAsync(req.Username.Trim(), req.Password);
    return Results.Ok(new { message = "Owner account created" });
});

auth.MapPost("/login", async (LoginRequest req, AuthService authService, HttpContext ctx) =>
{
    if (authService.NeedsSetup)
        return Results.BadRequest(new { error = "Run setup first" });

    var user = await authService.ValidateAsync(req.Username, req.Password);
    if (user == null)
        return Results.Unauthorized();

    await SignInAsync(ctx, user);
    return Results.Ok(new { message = "Logged in", username = user.Username, role = user.Role });
});

auth.MapPost("/redeem", async (RedeemRequest req, AuthService authService, InviteCodeService invites, EmailNotificationService email, HttpContext ctx) =>
{
    if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { error = "Code, username, and password are all required." });
    if (req.Password.Length < 4)
        return Results.BadRequest(new { error = "Password must be at least 4 characters." });

    var trimmedCode = req.Code.Trim().ToUpperInvariant();
    var match = invites.Peek(trimmedCode);
    if (match == null)
        return Results.BadRequest(new { error = "Invite code is invalid, expired, or fully used." });

    User user;
    try
    {
        user = await authService.CreateFriendAsync(req.Username.Trim(), req.Password, trimmedCode);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    await invites.ConsumeAsync(trimmedCode, user.Username);
    await SignInAsync(ctx, user);
    _ = email.NotifySignupAsync(user.Username, trimmedCode);
    return Results.Ok(new { message = "Account created", username = user.Username, role = user.Role });
});

auth.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { message = "Logged out" });
});

// -- Protected API endpoints (any logged-in user) --
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
    var blockBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes<ushort>(chunk.Blocks).ToArray();
    bw.Write(blockBytes);

    return Results.Bytes(ms.ToArray(), "application/octet-stream");
});

// atlas.rgba served as static file from wwwroot

// Binary WebSocket endpoint for camera-prioritized chunk streaming.
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

    sendQueue.Sort((a, b) =>
    {
        var da = a.X * a.X + a.Z * a.Z;
        var db = b.X * b.X + b.Z * b.Z;
        return da.CompareTo(db);
    });

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
                    catch { }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    });

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

static async Task SignInAsync(HttpContext ctx, User user)
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.Name, user.Username),
        new(ClaimTypes.Role, user.Role),
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    var principal = new ClaimsPrincipal(identity);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
}
