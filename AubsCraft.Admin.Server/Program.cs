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

world.MapGet("/chunk/{x:int}/{z:int}", (int x, int z, WorldDataService worldData) =>
{
    var chunk = worldData.GetChunk(x, z);
    if (chunk == null) return Results.NotFound();
    var blocksBase64 = Convert.ToBase64String(
        System.Runtime.InteropServices.MemoryMarshal.AsBytes<ushort>(chunk.Blocks).ToArray());
    return Results.Ok(new ChunkDataResponse(blocksBase64, chunk.Palette));
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
public record ChunkDataResponse(string Blocks, List<string> Palette);
