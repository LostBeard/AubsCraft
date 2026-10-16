using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;
using AubsCraft.Live.Model.Editing;

namespace AubsCraft.Admin.Server;

/// <summary>
/// Editing the running world from the viewer. The admin's side (<c>/api/world/edit</c>: Owner or Admin only, logged in the activity log) and the plugin's side
/// (<c>/api/live/commands/{serverId}</c>: loopback and the shared secret, like the live frames; the plugin asks for commands and reports their results).
/// </summary>
public static class EditEndpoints
{
    /// <summary>Longest an admin's request waits for the server to finish the edit.</summary>
    public static readonly TimeSpan EditTimeout = TimeSpan.FromSeconds(90);

    public static void MapEditEndpoints(this WebApplication app)
    {
        // ---- the plugin's side ----
        app.MapGet("/api/live/commands/{serverId}", async (string serverId, HttpContext ctx, LiveFeedService live, ServerManager servers, EditChannelService channel) =>
        {
            if (!LiveEndpoints.CameFromThisMachine(ctx.Connection.RemoteIpAddress, ctx.Request.Headers)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!live.VerifySecret(ctx.Request.Headers["X-Live-Secret"].ToString())) return Results.Unauthorized();
            if (servers.Get(serverId) is null) return Results.NotFound();
            var commands = await channel.PollAsync(serverId, EditChannelService.PollWait, ctx.RequestAborted);
            return commands == null ? Results.NoContent() : Results.Bytes(commands, "application/octet-stream");
        });

        app.MapPost("/api/live/commands/{serverId}/result", async (string serverId, HttpContext ctx, LiveFeedService live, ServerManager servers, EditChannelService channel) =>
        {
            if (!LiveEndpoints.CameFromThisMachine(ctx.Connection.RemoteIpAddress, ctx.Request.Headers)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!live.VerifySecret(ctx.Request.Headers["X-Live-Secret"].ToString())) return Results.Unauthorized();
            if (servers.Get(serverId) is null) return Results.NotFound();
            if (ctx.Request.ContentLength is > 64 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            using var reader = new StreamReader(ctx.Request.Body);
            var json = await reader.ReadToEndAsync(ctx.RequestAborted);
            return channel.Complete(serverId, json) ? Results.NoContent() : Results.BadRequest("Not the result of a command waiting here");
        });

        // ---- the admin's side ----
        var edit = app.MapGroup("/api/world/edit").RequireAuthorization(p => p.RequireRole(Roles.Owner, Roles.Admin));

        // Whether this server can be edited from here (its plugin is there to take the commands)
        edit.MapGet("/status", (string? server, ServerManager servers, EditChannelService channel) =>
            servers.Get(server) is not { } instance ? Results.NotFound() : Results.Ok(new { available = channel.PluginConnected(instance.Id) }));

        // The block a ray hits (the viewer's camera, a controller): where it is, which face the ray came through, and what it is. {"hit": false} when nothing is in reach.
        edit.MapGet("/pick", (string? server, double ox, double oy, double oz, double dx, double dy, double dz, double? max, ServerManager servers) =>
        {
            if (servers.Get(server) is not { } instance) return Results.NotFound();
            var hit = WorldPicker.Cast((cx, cz) => instance.World.GetChunk(cx, cz), ox, oy, oz, dx, dy, dz, max ?? 32);
            return hit == null ? Results.Ok(new { hit = false }) : Results.Ok(new { hit = true, x = hit.X, y = hit.Y, z = hit.Z, nx = hit.NormalX, ny = hit.NormalY, nz = hit.NormalZ, block = hit.Block, distance = hit.Distance, hx = hit.HitX, hy = hit.HitY, hz = hit.HitZ });
        });

        edit.MapPost("", async (EditRequest request, string? server, HttpContext ctx, ServerManager servers, EditChannelService channel, ActivityLogService log) =>
        {
            if (servers.Get(server) is not { } instance) return Results.NotFound();
            var commandId = Guid.NewGuid().ToString("N");
            var user = ctx.User.Identity?.Name ?? "?";
            byte[] payload;
            long blocks;
            try { payload = EditCodec.Build(commandId, user, request, out blocks); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }

            var result = await channel.SubmitAsync(instance.Id, commandId, payload, EditTimeout, ctx.RequestAborted);
            if (result.Ok)
                log.AddEvent(new ActivityEventDto(DateTime.UtcNow, ActivityEventType.AdminAction, user,
                    $"edited the world from the viewer: {(string.IsNullOrWhiteSpace(request.Label) ? "an edit" : request.Label)} ({result.Placed:N0} blocks changed)", instance.Id));
            return Results.Ok(result);
        });

        edit.MapPost("/undo", async (string edit, string? server, HttpContext ctx, ServerManager servers, EditChannelService channel, ActivityLogService log) =>
        {
            if (servers.Get(server) is not { } instance) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(edit) || edit.Length > 64) return Results.BadRequest(new { error = "Which edit?" });
            var commandId = Guid.NewGuid().ToString("N");
            var user = ctx.User.Identity?.Name ?? "?";
            var result = await channel.SubmitAsync(instance.Id, commandId, EditCodec.BuildUndo(commandId, edit), EditTimeout, ctx.RequestAborted);
            if (result.Ok)
                log.AddEvent(new ActivityEventDto(DateTime.UtcNow, ActivityEventType.AdminAction, user,
                    $"{(result.Applied ? "redid" : "undid")} an edit from the viewer ({result.Placed:N0} blocks changed)", instance.Id));
            return Results.Ok(result);
        });
    }
}
