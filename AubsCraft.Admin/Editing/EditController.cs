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
/// and redo, and sends the edits to the panel, which hands them to the server's plugin. The select tool makes a box (two clicks) to fill, clear, copy, cut or save as a blueprint; a blueprint (or the
/// clipboard) being placed shows as an outline that follows the cursor. The logic is <see cref="EditSession"/>; this is its connection to the HUD, the camera, the layer that draws the outlines and the panel.
/// </summary>
public sealed class EditController
{
    /// <summary>How far the camera reaches, in blocks (the host sets it: at the scale of a table model it is a metre or two of the room).</summary>
    public float ReachBlocks { get; set; } = 48f;
    private const float PickEverySeconds = 0.08f;
    private const int MaxEditsInFlight = 3;

    private static readonly Vector3 Black = new(0.04f, 0.04f, 0.04f), Blue = new(0.25f, 0.6f, 1f), Gold = new(1f, 0.8f, 0.15f), Cyan = new(0.2f, 0.95f, 0.9f);

    private readonly SpawnJSRuntime _js;
    private readonly string _baseUrl, _serverQuery;
    private readonly ViewerHud _hud;
    private readonly EntityLayer? _entities;
    private float _pickTimer;
    private bool _pickInFlight;
    private int _editsInFlight;
    private string _lastStatus = "";
    private int _lastHint = -1, _lastToolState = -1;
    // What the clipboard holds (its size), once something has been copied
    private (int W, int H, int D)? _clipboard;
    // A headset: the pointing is done by a controller's ray (set each frame), and there is no keyboard to name a blueprint with
    private Vector3? _rayOrigin, _rayDirection;

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
        _hud.OnBlueprintChoice = name => _ = BeginPasteOfAsync(name);
        _hud.OnBlueprintDelete = name => _ = DeleteBlueprintAsync(name);
        _hud.OnNameEntered = name => _ = SaveSelectionAsync(name);
        _hud.OnEditAction = id => Perform(id);
    }

    public EditSession Session { get; } = new();

    public bool Active => Session.Active;

    /// <summary>A menu is open and wants the mouse: the page lets go of the pointer so the HUD can be clicked.</summary>
    public bool WantsPointer => Session.Active && (_hud.IsPaletteOpen || _hud.IsBlueprintsOpen || _hud.IsNamePromptOpen);

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
        Console.WriteLine($"[Edit] edit mode {(on ? "on" : "off")}");
        Session.Target = null;
        _pickTimer = PickEverySeconds;
        _lastHint = -1;
        _hud.SetEditMode(on, on ? Session.Hotbar.Select(EditSession.DisplayName).ToArray() : null, Session.Selected);
        _lastToolState = -1;
        if (!on) { CloseMenus(); _hud.SetEditToolbar(false, false, false, false, false); }
        if (!on) Session.CancelPaste();
        if (!on && _entities != null) _entities.Extras.Clear();
        RefreshHud(slots: on);
        return on;
    }

    private void CloseMenus()
    {
        _hud.OpenPalette(false);
        _hud.OpenBlueprints(false);
        _hud.OpenNamePrompt(false);
    }

    // ---- once a frame ----

    /// <summary>Ask where the camera points (at most one question at a time), and draw the outlines.</summary>
    public void Update(float dt, FpsCamera camera)
    {
        if (!Session.Active) { _entities?.Extras.Clear(); return; }
        _pickTimer += dt;
        if (!_pickInFlight && _pickTimer >= PickEverySeconds)
        {
            _pickTimer = 0;
            if (_rayOrigin is { } origin && _rayDirection is { } direction) _ = PickAsync(origin, direction);
            else if (RayOnly) Session.Target = null;     // a headset points with a controller: no controller ray, nothing is pointed at
            else _ = PickAsync(camera.Position, camera.Front);
        }
        DrawOutlines();
        RefreshHud();
    }

    /// <summary>True in a headset: the camera's look does not pick, only a controller's ray (<see cref="SetRay"/>) does.</summary>
    public bool RayOnly { get; set; }

    /// <summary>Name a blueprint by itself (Blueprint 1, 2...) instead of asking: there is no keyboard in a headset.</summary>
    public bool AutoName { get; set; }

    /// <summary>The controller points along this ray (world space): that is what is picked.</summary>
    public void SetRay(Vector3 origin, Vector3 direction) { _rayOrigin = origin; _rayDirection = Vector3.Normalize(direction); }

    public void ClearRay() { _rayOrigin = null; _rayDirection = null; }

    /// <summary>How far the thing pointed at is (the beam's length), or null when nothing is.</summary>
    public float? TargetDistance => Session.Target is { } t ? (float)t.Distance : null;

    /// <summary>Draw a beam and a hand for a controller (edit mode's outlines are drawn each frame; these are added after them).</summary>
    public void AddController(Vector3 origin, Vector3 direction, float beamLength, Vector3 tint, float blocksPerMetre = 1f)
    {
        if (_entities == null) return;
        SelectionBoxes.Beam(origin, direction, beamLength, 0.006f * blocksPerMetre, tint, _entities.Extras);
        SelectionBoxes.Cube(origin, 0.05f * blocksPerMetre, tint * 0.6f, _entities.Extras);
    }

    // ---- the buttons of a controller (and of the toolbar): what each does depends on the tool ----

    /// <summary>The main button: places the block in hand (building), makes a corner (selecting), puts a blueprint down (placing).</summary>
    public void Primary(float cameraYawDegrees)
    {
        if (!Session.Active) return;
        if (Session.Paste != null) _ = PasteAsync();
        else if (Session.Tool == EditSession.Tools.Select) Session.ClickSelect();
        else
        {
            var plan = Session.PlanPlace(cameraYawDegrees);
            Console.WriteLine(plan == null ? $"[Edit] place: nothing to place ({(Session.Target == null ? "nothing is pointed at" : "no block in hand or the place is not free")})" : "[Edit] place");
            Send(plan);
        }
    }

    /// <summary>The other button: breaks the block (building), clears the selection (selecting), stops placing.</summary>
    public void Alternate()
    {
        if (!Session.Active) return;
        if (Session.Paste != null) Session.CancelPaste();
        else if (Session.Tool == EditSession.Tools.Select) Session.ClearSelection();
        else
        {
            var plan = Session.PlanBreak();
            Console.WriteLine(plan == null ? $"[Edit] break: {(Session.Target == null ? "nothing is pointed at" : "that block cannot be broken")}" : "[Edit] break");
            Send(plan);
        }
    }

    /// <summary>Take the block pointed at into the hand.</summary>
    public void Eyedrop()
    {
        if (!Session.Active) return;
        bool picked = Session.PickBlock();
        Console.WriteLine(picked ? $"[Edit] pick: {Session.Target?.Block} is in hand now" : $"[Edit] pick: {(Session.Target == null ? "nothing is pointed at" : "that is not a block to place")}");
        if (picked) RefreshHud(slots: true);
    }

    public void UndoNow() { if (Session.Active) _ = UndoAsync(); }

    /// <summary>Holding the trigger may keep going (placing and breaking blocks one after another): only with the build tool and nothing being put down.</summary>
    public bool CanRepeat => Session.Active && Session.Paste == null && Session.Tool == EditSession.Tools.Build;

    /// <summary>The next block in hand (+1) or the one before (-1), round the nine.</summary>
    public void CycleSlot(int step)
    {
        if (!Session.Active) return;
        int n = Session.Hotbar.Length;
        Session.Selected = ((Session.Selected + step) % n + n) % n;
        RefreshHud();
    }

    /// <summary>A toolbar button, by id.</summary>
    public void Perform(string id)
    {
        var s = Session;
        if (id == "enter") { _ = SetActiveAsync(true); return; }
        if (!s.Active) return;
        Console.WriteLine($"[Edit] tool {id}");
        switch (id)
        {
            case "tool-build": s.Tool = EditSession.Tools.Build; s.CancelPaste(); break;
            case "blocks": _hud.OpenBlueprints(false); _hud.OpenPalette(!_hud.IsPaletteOpen); break;
            case "select": s.Tool = s.Tool == EditSession.Tools.Select ? EditSession.Tools.Build : EditSession.Tools.Select; s.CancelPaste(); break;
            case "blueprints": _ = OpenBlueprintsAsync(); break;
            case "undo": _ = UndoAsync(); break;
            case "redo": _ = RedoAsync(); break;
            case "fill": Send(s.PlanFill()); break;
            case "clear": Send(s.PlanClear()); break;
            case "copy": if (s.HasSelection) _ = CopyAsync(cut: false); break;
            case "cut": if (s.HasSelection) _ = CopyAsync(cut: true); break;
            case "paste": BeginPasteOfClipboard(); break;
            case "save": _ = SaveSelectionClickedAsync(); break;
            case "turn": s.RotatePaste(); break;
            case "mirror": s.MirrorPaste(); break;
            case "cancel": s.CancelPaste(); break;
            case "leave": _ = SetActiveAsync(false); break;
        }
    }

    private async Task SaveSelectionClickedAsync()
    {
        if (!Session.HasSelection) { _hud.Toast("Select a box first", NotificationType.Info); return; }
        if (!AutoName)
        {
            _hud.OpenPalette(false); _hud.OpenBlueprints(false);
            _hud.OpenNamePrompt(true, "Save the selection as a blueprint called");
            return;
        }
        // Blueprint 1, Blueprint 2...: the first number no blueprint has
        try
        {
            if (!await RefreshBlueprintsAsync()) return;
            int n = 1;
            while (_items.Any(b => string.Equals(b.Name, "Blueprint " + n, StringComparison.OrdinalIgnoreCase))) n++;
            await SaveSelectionAsync("Blueprint " + n);
        }
        catch (Exception ex) { _hud.Toast("Could not save: " + ex.Message, NotificationType.Warning); }
    }

    private void DrawOutlines()
    {
        if (_entities == null) return;
        var extras = _entities.Extras;
        extras.Clear();

        // A selection: the box (or the one corner chosen so far)
        if (Session.SelectionBox is { } box) SelectionBoxes.Outline(box.X, box.Y, box.Z, box.W, box.H, box.D, Gold, extras, bar: 0.9f);
        else if (Session.CornerA is { } a) SelectionBoxes.Outline(a.X, a.Y, a.Z, 1, 1, 1, Gold, extras, bar: 0.9f);

        // A blueprint on its way down: its box, following the cursor
        if (Session.Paste != null && Session.PasteSize is { } size && Session.PasteAnchor() is { } at)
        {
            SelectionBoxes.Outline(at.X, at.Y, at.Z, size.W, size.H, size.D, Cyan, extras, bar: 0.9f);
            return;
        }

        if (Session.Target is not { } t) return;
        SelectionBoxes.Outline(t.X, t.Y, t.Z, 1, 1, 1, Black, extras, bar: 0.9f);
        if (Session.Tool == EditSession.Tools.Build)
        {
            var (px, py, pz) = t.PlaceAt;
            if (Session.SelectedBlock != EditSession.Air && py is >= EditSession.MinY and <= EditSession.MaxY)
                SelectionBoxes.Outline(px, py, pz, 1, 1, 1, Blue, extras, bar: 0.5f);
        }
    }

    private async Task PickAsync(Vector3 origin, Vector3 direction)
    {
        _pickInFlight = true;
        try
        {
            string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
            var url = $"{_baseUrl}/api/world/edit/pick?{_serverQuery}&ox={F(origin.X)}&oy={F(origin.Y)}&oz={F(origin.Z)}&dx={F(direction.X)}&dy={F(direction.Y)}&dz={F(direction.Z)}&max={ReachBlocks.ToString("0.#", CultureInfo.InvariantCulture)}";
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
        bool plain = (int)e.X == 0;
        // B turns edit mode on and off (on checks with the panel that this server can be edited by this user)
        if (e.Kind == UiInputEvent.KeyDown && e.Code == "KeyB" && plain)
        {
            _ = SetActiveAsync(!Session.Active);
            return true;
        }
        if (!Session.Active) return false;
        var s = Session;

        if (e.Kind == UiInputEvent.KeyDown)
        {
            bool ctrl = ((int)e.X & UiInputEvent.Ctrl) != 0, shift = ((int)e.X & UiInputEvent.Shift) != 0;
            switch (e.Code)
            {
                // The menus
                case "KeyE" when plain: _hud.OpenBlueprints(false); _hud.OpenPalette(!_hud.IsPaletteOpen); return true;
                case "KeyK" when plain: _ = OpenBlueprintsAsync(); return true;
                case "Escape":
                    if (_hud.IsPaletteOpen || _hud.IsBlueprintsOpen || _hud.IsNamePromptOpen) { CloseMenus(); return true; }
                    if (s.Paste != null) { s.CancelPaste(); return true; }
                    return false;
                // Building or selecting
                case "KeyV" when plain:
                    s.Tool = s.Tool == EditSession.Tools.Select ? EditSession.Tools.Build : EditSession.Tools.Select;
                    s.CancelPaste();
                    return true;
                // Undo and redo
                case "KeyZ" when ctrl && !shift: _ = UndoAsync(); return true;
                case "KeyY" when ctrl: _ = RedoAsync(); return true;
                case "KeyZ" when ctrl && shift: _ = RedoAsync(); return true;
                // Acting on the selection
                case "KeyF" when !ctrl && s.HasSelection: Send(s.PlanFill(hollow: shift)); return true;
                case "Delete" when s.HasSelection: Send(s.PlanClear()); return true;
                case "KeyC" when ctrl && s.HasSelection: _ = CopyAsync(cut: false); return true;
                case "KeyX" when ctrl && s.HasSelection: _ = CopyAsync(cut: true); return true;
                case "KeyV" when ctrl: BeginPasteOfClipboard(); return true;
                case "KeyS" when ctrl:
                    if (!s.HasSelection) { _hud.Toast("Select a box first (V, then two clicks)", NotificationType.Info); return true; }
                    _hud.OpenPalette(false); _hud.OpenBlueprints(false);
                    _hud.OpenNamePrompt(true, "Save the selection as a blueprint called");
                    return true;
                // Placing a blueprint
                case "KeyR" when s.Paste != null: s.RotatePaste(); return true;
                case "KeyT" when s.Paste != null: s.MirrorPaste(); return true;
            }
            return false;
        }

        switch (e.Kind)
        {
            case UiInputEvent.WorldMouseDown:
                int button = (int)e.X;
                if (s.Paste != null)
                {
                    if (button == 0) _ = PasteAsync();
                    else if (button == 2) s.CancelPaste();
                    return true;
                }
                if (s.Tool == EditSession.Tools.Select)
                {
                    if (button == 0) s.ClickSelect();
                    else if (button == 2) s.ClearSelection();
                    else if (button == 1 && s.PickBlock()) RefreshHud(slots: true);
                    return true;
                }
                switch (button)
                {
                    case 0: Send(s.PlanBreak()); break;
                    case 2: Send(s.PlanPlace(camera.Yaw)); break;
                    case 1: if (s.PickBlock()) RefreshHud(slots: true); break;
                }
                return true;
            case UiInputEvent.WorldMouseUp:
                return true;
            case UiInputEvent.WorldWheel:
                if (e.Y != 0)
                {
                    s.Selected = (s.Selected + (e.Y > 0 ? 1 : -1) + s.Hotbar.Length) % s.Hotbar.Length;
                    _hud.SelectEditSlot(s.Selected);
                }
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

    private async Task<(int Status, string Text)> CallAsync(string path, string method, string? body = null)
    {
        using var response = await _js.Fetch($"{_baseUrl}{path}{(path.Contains('?') ? "&" : "?")}{_serverQuery}", new FetchOptions
        {
            Method = method,
            Credentials = "include",
            Headers = body == null ? null : new() { ["Content-Type"] = "application/json" },
            Body = body,
        });
        return (response.Status, await response.Text());
    }

    private async Task SendAsync(EditRequest plan)
    {
        _editsInFlight++;
        try
        {
            var (status, text) = await CallAsync("/api/world/edit", "POST", EditJson.Write(plan));
            Finished(status, text, isUndo: false);
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
            var (status, text) = await CallAsync($"/api/world/edit/undo?edit={Uri.EscapeDataString(editId)}", "POST");
            return Finished(status, text, isUndo: true);
        }
        catch (Exception ex) { _hud.Toast("Undo failed: " + ex.Message, NotificationType.Warning); return false; }
        finally { _editsInFlight--; }
    }

    private bool Finished(int status, string text, bool isUndo)
    {
        if (status is 401 or 403) { _hud.Toast("Editing the world is for the server's admins", NotificationType.Warning); return false; }
        if (status == 400) { _hud.Toast("That was refused: " + EditJson.ReadError(text), NotificationType.Warning); return false; }
        if (status is < 200 or >= 300) { _hud.Toast($"The panel said {status}", NotificationType.Warning); return false; }
        var result = EditJson.ReadResult(text);
        if (!result.Ok)
        {
            _hud.Toast(result.Error ?? "The server did not do it", NotificationType.Warning);
            return false;
        }
        if (!isUndo && result.Edit != null && result.Placed > 0) Session.Record(result.Edit);
        return true;
    }

    // ---- the selection, the clipboard, blueprints ----

    private async Task CopyAsync(bool cut)
    {
        if (Session.PlanCopy(EditSession.ClipboardName) is not { } request) return;
        try
        {
            var (status, text) = await CallAsync("/api/world/edit/blueprints", "POST", EditJson.Write(request));
            if (status is < 200 or >= 300) { _hud.Toast("Could not copy: " + EditJson.ReadError(text), NotificationType.Warning); return; }
            var item = EditJson.ReadBlueprint(text);
            _clipboard = (item.Width, item.Height, item.Depth);
            _hud.Toast($"{(cut ? "Cut" : "Copied")} {item.Blocks:N0} blocks ({item.Width} x {item.Height} x {item.Depth}); Ctrl+V places them", NotificationType.Success);
            if (cut) Send(Session.PlanClear());
        }
        catch (Exception ex) { _hud.Toast("Could not copy: " + ex.Message, NotificationType.Warning); }
    }

    private void BeginPasteOfClipboard()
    {
        if (_clipboard is not { } c) { _hud.Toast("Nothing has been copied yet (select a box, Ctrl+C)", NotificationType.Info); return; }
        Session.Tool = EditSession.Tools.Build;
        Session.BeginPaste(EditSession.ClipboardName, c.W, c.H, c.D);
    }

    private async Task SaveSelectionAsync(string name)
    {
        _hud.OpenNamePrompt(false);
        if (Session.PlanCopy(name) is not { } request) return;
        try
        {
            var (status, text) = await CallAsync("/api/world/edit/blueprints", "POST", EditJson.Write(request));
            if (status is < 200 or >= 300) { _hud.Toast("Could not save: " + EditJson.ReadError(text), NotificationType.Warning); return; }
            var item = EditJson.ReadBlueprint(text);
            _hud.Toast($"Saved \"{item.Name}\" ({item.Blocks:N0} blocks, {item.Width} x {item.Height} x {item.Depth})", NotificationType.Success);
        }
        catch (Exception ex) { _hud.Toast("Could not save: " + ex.Message, NotificationType.Warning); }
    }

    private async Task OpenBlueprintsAsync()
    {
        if (_hud.IsBlueprintsOpen) { _hud.OpenBlueprints(false); return; }
        try
        {
            if (!await RefreshBlueprintsAsync()) return;
            _hud.OpenPalette(false);
            _hud.OpenNamePrompt(false);
            _hud.OpenBlueprints(true);
        }
        catch (Exception ex) { _hud.Toast("Could not list the blueprints: " + ex.Message, NotificationType.Warning); }
    }

    private List<BlueprintItem> _items = new();

    private async Task<bool> RefreshBlueprintsAsync()
    {
        var (status, text) = await CallAsync("/api/world/edit/blueprints", "GET");
        if (status is < 200 or >= 300) { _hud.Toast("Could not list the blueprints: " + EditJson.ReadError(text), NotificationType.Warning); return false; }
        _items = EditJson.ReadBlueprints(text);
        _hud.SetBlueprints(_items);
        return true;
    }

    private Task BeginPasteOfAsync(string name)
    {
        var item = _items.FirstOrDefault(b => b.Name == name);
        if (item == null) return Task.CompletedTask;
        _hud.OpenBlueprints(false);
        Session.Tool = EditSession.Tools.Build;
        Session.BeginPaste(item.Name, item.Width, item.Height, item.Depth);
        return Task.CompletedTask;
    }

    private async Task DeleteBlueprintAsync(string name)
    {
        try
        {
            var (status, text) = await CallAsync($"/api/world/edit/blueprints/{Uri.EscapeDataString(name)}", "DELETE");
            if (status is < 200 or >= 300) { _hud.Toast("Could not delete: " + EditJson.ReadError(text), NotificationType.Warning); return; }
            _hud.Toast($"Deleted \"{name}\"", NotificationType.Info);
            await RefreshBlueprintsAsync();
        }
        catch (Exception ex) { _hud.Toast("Could not delete: " + ex.Message, NotificationType.Warning); }
    }

    private async Task PasteAsync()
    {
        if (Session.PlanPaste() is not { } request || _editsInFlight >= MaxEditsInFlight) return;
        _editsInFlight++;
        try
        {
            var (status, text) = await CallAsync("/api/world/edit/paste", "POST", EditJson.Write(request));
            Finished(status, text, isUndo: false);
        }
        catch (Exception ex) { _hud.Toast("Paste failed: " + ex.Message, NotificationType.Warning); }
        finally { _editsInFlight--; }
    }

    // ---- the HUD ----

    private void RefreshHud(bool slots = false)
    {
        if (!Session.Active) return;
        if (slots) for (int i = 0; i < Session.Hotbar.Length; i++) _hud.SetEditSlot(i, EditSession.DisplayName(Session.Hotbar[i]), Session.Hotbar[i]);
        _hud.SelectEditSlot(Session.Selected);   // the lit slot is the one in hand, whatever changed it (a grip, a pick, a number key)
        int hint = Session.Paste != null ? 2 : Session.Tool == EditSession.Tools.Select ? 1 : 0;
        if (hint != _lastHint) { _lastHint = hint; _hud.SetEditHint(hint); }

        // The toolbar's buttons: only the ones that mean something now
        int toolState = (Session.Active ? 1 : 0) | (Session.HasSelection ? 2 : 0) | (Session.Paste != null ? 4 : 0) | (_clipboard != null ? 8 : 0) | (Session.Tool == EditSession.Tools.Select ? 16 : 0);
        if (toolState != _lastToolState) { _lastToolState = toolState; _hud.SetEditToolbar(Session.Active, Session.HasSelection, Session.Paste != null, _clipboard != null, Session.Tool == EditSession.Tools.Select); }

        string pointed = Session.Target is { } t ? $"{EditSession.DisplayName(t.Block)}  {t.X} {t.Y} {t.Z}" : "nothing in reach";
        string status = Session.Paste is { } p ? $"placing {p.Name}  {Session.PasteSize!.Value.W} x {Session.PasteSize.Value.H} x {Session.PasteSize.Value.D}     {pointed}"
            : Session.Tool == EditSession.Tools.Select ? $"{(Session.SelectionBox is { } b ? $"selected {b.W} x {b.H} x {b.D}" : Session.CornerA != null ? "pick the second corner" : "pick the first corner")}     {pointed}"
            : $"{pointed}     in hand: {EditSession.DisplayName(Session.SelectedBlock)}     undo {Session.Undoable}  redo {Session.Redoable}";
        if (status != _lastStatus) { _lastStatus = status; _hud.SetEditStatus(status); }
    }
}
