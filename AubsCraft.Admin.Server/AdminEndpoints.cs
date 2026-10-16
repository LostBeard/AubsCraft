using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;
using AubsCraft.Live.Model.Editing;
using SpawnDev.Rcon;

namespace AubsCraft.Admin.Server;

/// <summary>
/// An admin's hand on the players and the world, for the viewer (and a headset): <c>POST /api/world/admin?server=</c> with an <see cref="AdminRequest"/>. Owner or Admin only; every action is checked by
/// <see cref="AdminCommands"/> before any command is built, and written to the activity log. The same things the Players page does, reachable from the 3D view.
/// </summary>
public static class AdminEndpoints
{
    private static readonly string[] FailureMarks = ["No player was found", "Unknown or incomplete command", "Incorrect argument", "Expected ", "Unknown item", "That player cannot be found", "is not online"];

    public static void MapAdminEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/world/admin").RequireAuthorization(p => p.RequireRole(Roles.Owner, Roles.Admin));

        admin.MapPost("", async (AdminRequest request, string? server, HttpContext ctx, ServerManager servers, NetworkModerationService moderation, ActivityLogService log) =>
        {
            if (servers.Get(server) is not { } instance) return Results.NotFound();
            var user = ctx.User.Identity?.Name ?? "?";
            string message;

            if (request.Kind == "player" && request.Action == "ban")
            {
                if (!AdminCommands.IsPlayerName(request.Name)) return Results.BadRequest(new { error = "That is not a player name" });
                var reason = AdminCommands.CleanText(request.Reason, AdminCommands.MaxReason);
                message = MinecraftText.StripColorCodes(await moderation.BanAsync(request.Name!, reason.Length == 0 ? null : reason, user));
                log.AddEvent(new ActivityEventDto(DateTime.UtcNow, ActivityEventType.AdminAction, user, $"banned {request.Name} from the viewer", instance.Id));
                return Results.Ok(new { ok = true, message });
            }

            var (commands, error) = AdminCommands.For(request);
            if (commands == null) return Results.BadRequest(new { error });

            var replies = new List<string>();
            foreach (var command in commands)
                replies.Add(MinecraftText.StripColorCodes(await instance.Rcon.SendCommandAsync(command, ctx.RequestAborted)).Trim());
            message = string.Join(" ", replies.Where(r => r.Length > 0));
            bool ok = !FailureMarks.Any(m => message.Contains(m, StringComparison.OrdinalIgnoreCase));
            if (ok)
                log.AddEvent(new ActivityEventDto(DateTime.UtcNow, ActivityEventType.AdminAction, user, Describe(request) + " from the viewer", instance.Id));
            return Results.Ok(new { ok, message });
        });
    }

    private static string Describe(AdminRequest r) => r.Kind == "world"
        ? r.Action switch { "time" => $"set the time to {r.Value}", "weather" => $"set the weather to {r.Value}", "say" => "said something to everyone", "save" => "saved the world", _ => r.Action }
        : r.Action switch
        {
            "kick" => $"kicked {r.Name}",
            "gamemode" => $"set {r.Name}'s game mode to {r.Value}",
            "teleport" => $"teleported {r.Name}",
            "heal" => $"healed {r.Name}",
            "feed" => $"fed {r.Name}",
            "kill" => $"killed {r.Name}",
            "clear" => $"cleared {r.Name}'s inventory",
            "give" => $"gave {r.Name} {r.Count} x {r.Value}",
            _ => $"{r.Action} {r.Name}",
        };
}
