using System.Security.Claims;
using AubsCraft.Admin.Server;
using AubsCraft.Admin.Server.Hubs;
using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.WebAssembly.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ActivityLogService>();
// The managed Minecraft servers (servers.json) and one ServerInstance per server: RCON, systemd control,
// add-ons, stats, world data and log tailing all live on the instance.
builder.Services.AddSingleton<ServerRegistry>();
builder.Services.AddSingleton<ServerManager>();
builder.Services.AddSingleton<NetworkModerationService>();
builder.Services.AddSingleton<HostCapacityService>();
// The Velocity proxy: verified downloads, its config, backups, and the one-time cutover (systemd on the VM).
builder.Services.AddSingleton(_ => new AddonDownloader());
builder.Services.AddSingleton<ProxyService>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<IServiceRunner, SystemdServiceRunner>();
builder.Services.AddSingleton<ProxyCutoverService>();
builder.Services.AddSingleton<ProxyOperationsService>();
// Creating servers: loader installs (verified), provisioning behind the proxy, background runs with live progress.
builder.Services.AddSingleton<ServerSoftwareService>();
builder.Services.AddSingleton<ServerProvisioningService>();
builder.Services.AddSingleton<ServerMaintenanceService>();
builder.Services.AddSingleton<SpawnPortalService>();
builder.Services.AddSingleton<ModpackService>();
builder.Services.AddSingleton<ServerSettingsService>();
builder.Services.AddSingleton<ServerOperationsService>();
builder.Services.AddSingleton<ServerMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerMonitorService>());
builder.Services.AddHostedService<LogTailService>();
builder.Services.AddSingleton<AutoBackupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AutoBackupService>());
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<InviteCodeService>();
builder.Services.AddSingleton<WhitelistAuditService>();
builder.Services.AddSingleton<EmailNotificationService>();
builder.Services.AddSingleton<ModrinthService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<QuestAssetService>();
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

// A proxy set up before the gate plugin existed gets it (and its Java-only list) here; the list is also rewritten
// whenever the proxy is configured. A newly installed jar loads on the proxy's next restart.
try
{
    var registry = app.Services.GetRequiredService<ServerRegistry>();
    if (registry.Proxy is { } proxyDef && Directory.Exists(proxyDef.Path))
    {
        var proxyService = app.Services.GetRequiredService<ProxyService>();
        if (proxyService.InstallGate(proxyDef))
            app.Logger.LogWarning("The AubsCraft Gate proxy plugin was installed: restart the proxy (velocity) to load it");
        ProxyService.WriteJavaOnly(proxyDef, registry.All, proxyService.PublicUrl);
    }
}
catch (Exception ex) { app.Logger.LogError(ex, "Could not set up the AubsCraft Gate proxy plugin"); }

// Eagerly resolve EmailNotificationService so it subscribes to ActivityLog events at startup.
app.Services.GetRequiredService<EmailNotificationService>();

app.UseWebSockets();
app.MapStaticAssets();
app.UseAuthentication();
app.UseAuthorization();

app.MapHub<ServerHub>("/hubs/server").RequireAuthorization();

// -- Public endpoints (anonymous) --
var publicApi = app.MapGroup("/api/public");

// Whole-network status: Connected if any server is up, players summed over every server, plus each server's line.
publicApi.MapGet("/status", (ServerManager servers) =>
{
    var lines = servers.All.Select(i => new PublicServerStatusDto(
        i.Id, i.Definition.Name, i.LastStatus?.Connected == true,
        i.LastStatus?.Online ?? 0, i.LastStatus?.Max ?? 0, i.LastStatus?.Players ?? [])).ToList();
    var up = lines.Where(l => l.Connected).ToList();
    return Results.Ok(new PublicStatusDto(
        up.Count > 0,
        up.Sum(l => l.Online),
        up.Sum(l => l.Max),
        up.SelectMany(l => l.Players).Distinct().ToList(),
        lines));
});

// -- Quest installer endpoints (anonymous - the /quest page is a public family setup page) --
var quest = app.MapGroup("/api/quest");

quest.MapGet("/manifest", async (QuestAssetService svc, CancellationToken ct) =>
{
    var m = await svc.GetManifestAsync(ct);
    // Client-facing shape - upstream download URLs stay server-side; the browser fetches /asset/{id}.
    return Results.Ok(new
    {
        questCraft = new { id = m.QuestCraft.Id, version = m.QuestCraft.Version, filename = m.QuestCraft.Filename },
        mods = m.Mods.Select(x => new { id = x.Id, name = x.Name, version = x.Version, filename = x.Filename, targetFilename = x.TargetFilename, size = x.Size, sha512 = x.Sha512, @for = x.For }),
        modsDirTemplate = m.ModsDirTemplate,
        packageHints = m.PackageHints,
        questCraftManaged = m.QuestCraftManaged,
    });
});

quest.MapGet("/asset/{id}", async (string id, QuestAssetService svc, CancellationToken ct) =>
{
    var asset = await svc.OpenAssetAsync(id, ct);
    if (asset == null) return Results.NotFound();
    // Seekable FileStream -> Content-Length is set automatically (drives browser download progress).
    return Results.File(asset.Value.Stream, asset.Value.ContentType, enableRangeProcessing: true);
});

// -- PC mod pack (anonymous - the /pc page is a public family setup page) --
var pc = app.MapGroup("/api/pc");
pc.MapGet("/modpacks", (ModpackService packs) => Results.Ok(packs.Available()));
pc.MapGet("/modpack/{gameVersion}", async (string gameVersion, ModpackService packs, CancellationToken ct) =>
{
    var pack = await packs.GetPackAsync(gameVersion, ct);
    return pack == null ? Results.NotFound() : Results.File(pack, "application/x-modrinth-modpack+zip", ModpackService.FileName(gameVersion));
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

api.MapGet("/status", async (string? server, ServerManager servers, ILogger<Program> log, CancellationToken ct) =>
{
    if (servers.Get(server) is not { } instance) return Results.NotFound();
    var rcon = instance.Rcon;
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

api.MapGet("/whitelist", (string? server, ServerManager servers, ILogger<Program> log) =>
{
    if (servers.Get(server) is not { } instance) return Results.NotFound();
    try
    {
        var list = instance.ReadWhitelist();
        return Results.Ok(list);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Whitelist fetch failed");
        return Results.Ok(new List<string>());
    }
});

api.MapGet("/banlist", (string? server, ServerManager servers, ILogger<Program> log) =>
{
    if (servers.Get(server) is not { } instance) return Results.NotFound();
    try
    {
        var list = instance.ReadBannedPlayers();
        return Results.Ok(list);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Banlist fetch failed");
        return Results.Ok(new List<string>());
    }
});

// -- Backup downloads (owner): streamed from disk with range support (backups are hundreds of MB) --
api.MapGet("/servers/{id}/backups/{file}", (string id, string file, BackupService backups) =>
{
    var b = backups.List(id).FirstOrDefault(x => x.FileName == file);
    if (b == null) return Results.NotFound();
    return Results.File(b.Path, "application/gzip", b.FileName, enableRangeProcessing: true);
}).RequireAuthorization(p => p.RequireRole(AubsCraft.Admin.Server.Models.Roles.Owner));

// -- World Data API (for 3D viewer) --
// Every world endpoint takes ?server=<id> (default: the primary server).
var world = app.MapGroup("/api/world").RequireAuthorization();

world.MapGet("/regions", (string? server, ServerManager servers) =>
{
    if (servers.Get(server) is not { } instance) return Results.NotFound();
    return Results.Ok(instance.World.GetRegions());
});

world.MapGet("/chunks", (string? server, ServerManager servers) =>
{
    if (servers.Get(server) is not { } instance) return Results.NotFound();
    return Results.Ok(instance.World.GetPopulatedChunks());
});

// Binary chunk endpoint - raw bytes, no base64, no JSON.
// Format: [int32 paletteCount][palette strings: int32 len + utf8 bytes each][ushort[] blocks]
world.MapGet("/chunk/{x:int}/{z:int}", (int x, int z, string? server, ServerManager servers, HttpContext ctx) =>
{
    if (servers.Get(server) is not { } instance) return Results.NotFound();
    var chunk = instance.World.GetChunk(x, z);
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
world.MapGet("/ws", async (HttpContext ctx, string? server, ServerManager servers) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }
    if (servers.Get(server) is not { } instance)
    {
        ctx.Response.StatusCode = 404;
        return;
    }
    var worldData = instance.World;

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var chunks = worldData.GetPopulatedChunks();
    var camX = 0f;
    var camZ = 0f;
    var sendQueue = new List<ChunkCoord>(chunks);
    var sent = new HashSet<(int, int)>();
    // Set when the client closes: stops the send loop. Never passed to a WebSocket call - cancelling a pending
    // WebSocket send or receive ABORTS the socket, which skips the close handshake (the browser sees 1006).
    using var clientClosed = new CancellationTokenSource();
    var aborted = ctx.RequestAborted;

    sendQueue.Sort((a, b) =>
    {
        var da = a.X * a.X + a.Z * a.Z;
        var db = b.X * b.X + b.Z * b.Z;
        return da.CompareTo(db);
    });

    // The receive side: camera updates, and the client's close. Only this loop calls ReceiveAsync (one at a time).
    var receiveLoop = Task.Run(async () =>
    {
        var buf = new byte[256];
        try
        {
            while (ws.State is System.Net.WebSockets.WebSocketState.Open or System.Net.WebSockets.WebSocketState.CloseSent)
            {
                var result = await ws.ReceiveAsync(buf, aborted);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    clientClosed.Cancel();
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

    while (ws.State == System.Net.WebSockets.WebSocketState.Open && !clientClosed.IsCancellationRequested)
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
            lock (sendQueue) sent.Add((next.X, next.Z)); // the receive loop reads `sent` under this lock
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

        bw.Flush();
        try
        {
            // The stream's own buffer, no ToArray() copy.
            await ws.SendAsync(new ArraySegment<byte>(ms.GetBuffer(), 0, (int)ms.Length),
                System.Net.WebSockets.WebSocketMessageType.Binary, true, aborted);
        }
        catch { break; }

        lock (sendQueue) sent.Add((next.X, next.Z));
    }

    // Finish the close handshake from the send side (CloseOutputAsync sends our close frame; the receive loop,
    // which owns ReceiveAsync, reads the client's). Covers both orders: we finished sending (Open -> CloseSent)
    // or the client closed first (CloseReceived -> Closed).
    if (ws.State is System.Net.WebSockets.WebSocketState.Open or System.Net.WebSockets.WebSocketState.CloseReceived)
    {
        try { await ws.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
        catch { }
    }
    try { await receiveLoop.WaitAsync(TimeSpan.FromSeconds(10), aborted); }
    catch { }
});

world.MapPost("/cache/clear", (string? server, ServerManager servers) =>
{
    if (servers.Get(server) is not { } instance) return Results.NotFound();
    instance.World.ClearCache();
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

/// <summary>Visible to the integration tests (WebApplicationFactory&lt;Program&gt;).</summary>
public partial class Program;
