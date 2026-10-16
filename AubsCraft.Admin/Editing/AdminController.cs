using System.Numerics;
using AubsCraft.Admin.Rendering;
using AubsCraft.Admin.Services;
using AubsCraft.Live.Model.Editing;
using SpawnDev.GameUI.Elements;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace AubsCraft.Admin.Editing;

/// <summary>
/// An admin's hand on the players and the world from the 3D view: what the player list's menu offers an admin (bring them here, their game mode, heal, kick, ban after a second question) and the world panel
/// (O: the time of day, the weather, save). Each is a request to <c>/api/world/admin</c>, which checks the user's role and every word of the request again; the answer shows as a notice.
/// </summary>
public sealed class AdminController
{
    private readonly SpawnJSRuntime _js;
    private readonly string _baseUrl, _serverQuery;
    private readonly ViewerHud _hud;
    private readonly Func<Vector3> _camera;

    public AdminController(SpawnJSRuntime js, string baseUrl, string serverQuery, ViewerHud hud, Func<Vector3> cameraPosition)
    {
        _js = js;
        _baseUrl = baseUrl;
        _serverQuery = serverQuery;
        _hud = hud;
        _camera = cameraPosition;
        _hud.OnAdminPlayerAction = (name, action, value) => _ = PlayerAsync(name, action, value);
        _hud.OnAdminWorldAction = (action, value) => _ = WorldAsync(action, value);
    }

    public bool IsAdmin
    {
        get => _hud.IsAdmin;
        set { _hud.IsAdmin = value; if (!value) _hud.OpenWorldPanel(false); }
    }

    /// <summary>The world panel is open and wants the mouse.</summary>
    public bool WantsPointer => _hud.IsWorldPanelOpen;

    /// <summary>O opens and closes the world panel (for an admin, when no text is being typed); Escape closes it.</summary>
    public bool Handle(UiInputEvent e)
    {
        if (!IsAdmin || e.Kind != UiInputEvent.KeyDown) return false;
        if (e.Code == "KeyO" && (int)e.X == 0) { _hud.OpenWorldPanel(!_hud.IsWorldPanelOpen); return true; }
        if (e.Code == "Escape" && _hud.IsWorldPanelOpen) { _hud.OpenWorldPanel(false); return true; }
        return false;
    }

    private Task PlayerAsync(string name, string action, string? value)
    {
        var request = new AdminRequest { Kind = "player", Action = action, Name = name, Value = value };
        if (action == "teleport")
        {
            var here = _camera();
            request.X = here.X; request.Y = here.Y; request.Z = here.Z;
        }
        return SendAsync(request, action switch
        {
            "teleport" => $"{name} is here",
            "gamemode" => $"{name} is in {value} mode",
            "heal" => $"{name} is healed",
            "kick" => $"{name} was kicked",
            "ban" => $"{name} is banned",
            _ => "Done",
        });
    }

    private Task WorldAsync(string action, string? value)
        => SendAsync(new AdminRequest { Kind = "world", Action = action, Value = value }, action switch
        {
            "time" => $"It is {value} now",
            "weather" => $"The weather is {value}",
            "save" => "The world is saved",
            _ => "Done",
        });

    private async Task SendAsync(AdminRequest request, string success)
    {
        try
        {
            using var response = await _js.Fetch($"{_baseUrl}/api/world/admin?{_serverQuery}", new FetchOptions
            {
                Method = "POST",
                Credentials = "include",
                Headers = new() { ["Content-Type"] = "application/json" },
                Body = AdminJson.Write(request),
            });
            var text = await response.Text();
            Console.WriteLine($"[Admin] {request.Kind} {request.Action} {request.Name} {request.Value} -> {response.Status}");
            if (response.Status is 401 or 403) { _hud.Toast("That is for the server's admins", NotificationType.Warning); return; }
            var reply = AdminJson.Read(text);
            if (response.Ok && reply.Ok) _hud.Toast(success, NotificationType.Success);
            else _hud.Toast(reply.Message.Length > 0 ? reply.Message : $"The panel said {response.Status}", NotificationType.Warning);
        }
        catch (Exception ex) { _hud.Toast("That did not work: " + ex.Message, NotificationType.Warning); }
    }
}
