using System.Globalization;
using System.Numerics;
using AubsCraft.Admin.Rendering;
using AubsCraft.Admin.Services;
using AubsCraft.Live.Model.Editing;
using SpawnDev.GameUI.Elements;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace AubsCraft.Admin.Editing;

/// <summary>
/// Building in the viewer, wired up: while edit mode is on it asks the panel which block the camera points at (a few times a second), outlines that block and the one a placement would fill, turns the
/// mouse buttons into edits (left breaks, right places the block in hand, middle copies the block pointed at), the number keys and the wheel into the choice of block in hand, Ctrl+Z / Ctrl+Y into undo
/// and redo, and sends the edits to the panel, which hands them to the server's plugin. The logic is <see cref="EditSession"/>; this is its connection to the HUD, the camera, the layer that draws the
/// outline and the panel.
/// </summary>
public sealed class EditController
{
    /// <summary>How far the camera reaches, in blocks.</summary>
    public const double Reach = 48;
    private const float PickEverySeconds = 0.08f;
    private const int MaxEditsInFlight = 3;

    private readonly SpawnJSRuntime _js;
    private readonly string _baseUrl, _serverQuery;
    private readonly ViewerHud _hud;
    private readonly EntityLayer? _entities;
    private float _pickTimer;
    private bool _pickInFlight;
    private int _editsInFlight;
    private string _lastStatus = "";

    public EditController(SpawnJSRuntime js, string baseUrl, string serverQuery, ViewerHud hud, EntityLayer? entities)
    {
        _js = js;
        _baseUrl = baseUrl;
        _serverQuery = serverQuery;
        _hud = hud;
        _entities = entities;
        _hud.OnEditSlot = slot => { Session.Selected = slot; RefreshHud(); };
        _hud.OnPaletteChoice = block =>
        {
            Session.SetSelectedBlock(block);
            _hud.OpenPalette(false);
            RefreshHud(slots: true);
        };
    }

    public EditSession Session { get; } = new();

    public bool Active => Session.Active;

    /// <summary>The palette is open and wants the mouse: the page lets go of the pointer so the HUD can be clicked.</summary>
    public bool WantsPointer => Session.Active && _hud.IsPaletteOpen;

    /// <summary>Every block the game has, for the palette (from the assets, once they are loaded).</summary>
    public void SetAllBlocks(IEnumerable<string> names) => _hud.SetPaletteBlocks(EditSession.PlaceableBlocks(names));

    // ---- turning it on and off ----

    /// <summary>Turn edit mode on or off. On needs the server's plugin to be there to take edits: when it is not, nothing changes and the HUD says why. Returns whether edit mode is on now.</summary>
    public async Task<bool> SetActiveAsync(bool on)
    {
        if (on == Session.Active) return on;
        if (on)
        {
            string? why = null;
            try
            {
                using var response = await _js.Fetch($"{_baseUrl}/api/world/edit/status?{_serverQuery}", new FetchOptions { Credentials = "include" });
                if (response.Status is 401 or 403) why = "Editing the world is for the server's admins";
                else if (!response.Ok) why = $"The panel said {response.Status}";
                else if (!(await response.Text()).Contains("\"available\":true")) why = "The server's AubsCraft Live plugin is not ready for editing: it needs the latest version, installed on the Plugins page, and a restart of the server";
            }
            catch (Exception ex) { why = ex.Message; }
            if (why != null)
            {
                _hud.Toast(why, NotificationType.Warning);
                return false;
            }
        }
        Session.Active = on;
        Session.Target = null;
        _pickTimer = PickEverySeconds;
        _hud.SetEditMode(on, on ? Session.Hotbar.Select(EditSession.DisplayName).ToArray() : null, Session.Selected);
        if (!on) _hud.OpenPalette(false);
        if (!on && _entities != null) _entities.Extras.Clear();
        RefreshHud();
        return on;
    }

    // ---- once a frame ----

    /// <summary>Ask where the camera points (at most one question at a time), and draw the outline of what it last said.</summary>
    public void Update(float dt, FpsCamera camera)
    {
        if (!Session.Active) return;
        _pickTimer += dt;
        if (!_pickInFlight && _pickTimer >= PickEverySeconds)
        {
            _pickTimer = 0;
            _ = PickAsync(camera.Position, camera.Front);
        }
        DrawOutline();
        RefreshHud();
    }

    private void DrawOutline()
    {
        if (_entities == null) return;
        _entities.Extras.Clear();
        if (Session.Target is not { } t) return;
        SelectionBoxes.Outline(t.X, t.Y, t.Z, 1, 1, 1, new Vector3(0.04f, 0.04f, 0.04f), _entities.Extras, bar: 0.9f);
        var (px, py, pz) = t.PlaceAt;
        if (Session.SelectedBlock != EditSession.Air && py is >= EditSession.MinY and <= EditSession.MaxY)
            SelectionBoxes.Outline(px, py, pz, 1, 1, 1, new Vector3(0.25f, 0.6f, 1f), _entities.Extras, bar: 0.5f);
    }

    private async Task PickAsync(Vector3 origin, Vector3 direction)
    {
        _pickInFlight = true;
        try
        {
            string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
            var url = $"{_baseUrl}/api/world/edit/pick?{_serverQuery}&ox={F(origin.X)}&oy={F(origin.Y)}&oz={F(origin.Z)}&dx={F(direction.X)}&dy={F(direction.Y)}&dz={F(direction.Z)}&max={Reach.ToString(CultureInfo.InvariantCulture)}";
            using var response = await _js.Fetch(url, new FetchOptions { Credentials = "include" });
            if (!response.Ok) { Session.Target = null; return; }
            var reply = EditJson.ReadPick(await response.Text());
            if (Session.Active) Session.Target = EditTarget.Of(reply);
        }
        catch (Exception ex)
        {
            Session.Target = null;
            Console.WriteLine($"[Edit] pick failed: {ex.Message}");
        }
        finally { _pickInFlight = false; }
    }

    // ---- input ----

    /// <summary>Handle an input event when edit mode wants it; true when it did (the HUD does not get it then).</summary>
    public bool Handle(UiInputEvent e, FpsCamera camera)
    {
        // B turns edit mode on and off (on checks with the panel that this server can be edited by this user)
        if (e.Kind == UiInputEvent.KeyDown && e.Code == "KeyB" && (int)e.X == 0)
        {
            _ = SetActiveAsync(!Session.Active);
            return true;
        }
        if (!Session.Active) return false;
        // E opens the block palette (Escape closes it); the pointer is the HUD's while it is open
        if (e.Kind == UiInputEvent.KeyDown && e.Code == "KeyE" && (int)e.X == 0) { _hud.OpenPalette(!_hud.IsPaletteOpen); return true; }
        if (e.Kind == UiInputEvent.KeyDown && e.Code == "Escape" && _hud.IsPaletteOpen) { _hud.OpenPalette(false); return true; }
        switch (e.Kind)
        {
            case UiInputEvent.WorldMouseDown:
                switch ((int)e.X)
                {
                    case 0: Send(Session.PlanBreak()); break;
                    case 2: Send(Session.PlanPlace(camera.Yaw)); break;
                    case 1: if (Session.PickBlock()) RefreshHud(slots: true); break;
                }
                return true;
            case UiInputEvent.WorldMouseUp:
                return true;
            case UiInputEvent.WorldWheel:
                if (e.Y != 0)
                {
                    Session.Selected = (Session.Selected + (e.Y > 0 ? 1 : -1) + Session.Hotbar.Length) % Session.Hotbar.Length;
                    _hud.SelectEditSlot(Session.Selected);
                }
                return true;
            case UiInputEvent.KeyDown when ((int)e.X & UiInputEvent.Ctrl) != 0 && e.Code == "KeyZ" && ((int)e.X & UiInputEvent.Shift) == 0:
                _ = UndoAsync();
                return true;
            case UiInputEvent.KeyDown when ((int)e.X & UiInputEvent.Ctrl) != 0 && (e.Code == "KeyY" || (e.Code == "KeyZ" && ((int)e.X & UiInputEvent.Shift) != 0)):
                _ = RedoAsync();
                return true;
        }
        return false;
    }

    // ---- talking to the panel ----

    private void Send(EditRequest? plan)
    {
        if (plan == null) return;
        if (_editsInFlight >= MaxEditsInFlight) return;
        _ = SendAsync(plan);
    }

    private async Task SendAsync(EditRequest plan)
    {
        _editsInFlight++;
        try
        {
            using var response = await _js.Fetch($"{_baseUrl}/api/world/edit?{_serverQuery}", new FetchOptions
            {
                Method = "POST",
                Credentials = "include",
                Headers = new() { ["Content-Type"] = "application/json" },
                Body = EditJson.Write(plan),
            });
            await Finished(response, isUndo: false);
        }
        catch (Exception ex) { _hud.Toast("Edit failed: " + ex.Message, NotificationType.Warning); }
        finally { _editsInFlight--; }
    }

    private async Task UndoAsync()
    {
        if (Session.NextUndo is not { } id) { _hud.Toast("Nothing to undo", NotificationType.Info); return; }
        if (await ToggleAsync(id)) Session.Undone();
    }

    private async Task RedoAsync()
    {
        if (Session.NextRedo is not { } id) { _hud.Toast("Nothing to redo", NotificationType.Info); return; }
        if (await ToggleAsync(id)) Session.Redone();
    }

    // The server swaps the world with the edit's journal: the same call undoes and redoes
    private async Task<bool> ToggleAsync(string editId)
    {
        _editsInFlight++;
        try
        {
            using var response = await _js.Fetch($"{_baseUrl}/api/world/edit/undo?{_serverQuery}&edit={Uri.EscapeDataString(editId)}", new FetchOptions { Method = "POST", Credentials = "include" });
            return await Finished(response, isUndo: true);
        }
        catch (Exception ex) { _hud.Toast("Undo failed: " + ex.Message, NotificationType.Warning); return false; }
        finally { _editsInFlight--; }
    }

    private async Task<bool> Finished(Response response, bool isUndo)
    {
        if (response.Status is 401 or 403) { _hud.Toast("Editing the world is for the server's admins", NotificationType.Warning); return false; }
        var text = await response.Text();
        if (response.Status == 400)
        {
            _hud.Toast("That edit was refused: " + EditJson.ReadError(text), NotificationType.Warning);
            return false;
        }
        if (!response.Ok) { _hud.Toast($"The panel said {response.Status}", NotificationType.Warning); return false; }
        var result = EditJson.ReadResult(text);
        if (!result.Ok)
        {
            _hud.Toast(result.Error ?? "The server did not do it", NotificationType.Warning);
            return false;
        }
        if (!isUndo && result.Edit != null && result.Placed > 0) Session.Record(result.Edit);
        return true;
    }

    // ---- the HUD ----

    private void RefreshHud(bool slots = false)
    {
        if (!Session.Active) return;
        if (slots) for (int i = 0; i < Session.Hotbar.Length; i++) _hud.SetEditSlot(i, EditSession.DisplayName(Session.Hotbar[i]));
        string pointed = Session.Target is { } t ? $"{EditSession.DisplayName(t.Block)}  {t.X} {t.Y} {t.Z}" : "nothing in reach";
        string status = $"{pointed}     in hand: {EditSession.DisplayName(Session.SelectedBlock)}     undo {Session.Undoable}  redo {Session.Redoable}";
        if (status != _lastStatus) { _lastStatus = status; _hud.SetEditStatus(status); }
    }
}
