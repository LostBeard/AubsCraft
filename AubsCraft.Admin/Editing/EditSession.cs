using AubsCraft.Live.Model.Editing;

namespace AubsCraft.Admin.Editing;

/// <summary>The block under the cursor, as the panel's pick says it.</summary>
public readonly record struct EditTarget(int X, int Y, int Z, int Nx, int Ny, int Nz, string Block, double Distance, double Hx, double Hy, double Hz)
{
    /// <summary>Where a block put on the face that was hit goes.</summary>
    public (int X, int Y, int Z) PlaceAt => (X + Nx, Y + Ny, Z + Nz);

    public static EditTarget? Of(PickReply? r) => r is { Hit: true } ? new EditTarget(r.X, r.Y, r.Z, r.Nx, r.Ny, r.Nz, r.Block, r.Distance, r.Hx, r.Hy, r.Hz) : null;
}

/// <summary>
/// An admin's building session in the viewer: which block is in hand (a hotbar of nine), what the cursor points at, and the plans a click makes - break the block, put the block in hand on the face, copy the block
/// (the eyedropper) - and the history that undo and redo walk. Pure logic: the viewer does the picking, the sending and the drawing.
/// </summary>
public sealed class EditSession
{
    public static readonly string[] DefaultHotbar =
    [
        "minecraft:stone", "minecraft:cobblestone", "minecraft:oak_planks", "minecraft:oak_log", "minecraft:glass",
        "minecraft:dirt", "minecraft:grass_block", "minecraft:torch", "minecraft:oak_stairs",
    ];

    public const int MinY = -64, MaxY = 319;
    public const string Air = "minecraft:air";

    public bool Active { get; set; }
    public string[] Hotbar { get; } = (string[])DefaultHotbar.Clone();
    public int Selected { get; set; }
    public EditTarget? Target { get; set; }

    /// <summary>The block in hand.</summary>
    public string SelectedBlock => Hotbar[Math.Clamp(Selected, 0, Hotbar.Length - 1)];

    /// <summary>Put another block in the slot in hand.</summary>
    public void SetSelectedBlock(string block) => Hotbar[Math.Clamp(Selected, 0, Hotbar.Length - 1)] = block;

    /// <summary>A short, readable name of a block: <c>minecraft:oak_stairs[facing=north]</c> is "oak stairs".</summary>
    public static string DisplayName(string block)
    {
        int bracket = block.IndexOf('[');
        var name = bracket < 0 ? block : block[..bracket];
        int colon = name.IndexOf(':');
        if (colon >= 0) name = name[(colon + 1)..];
        return name.Replace('_', ' ');
    }

    /// <summary>
    /// The blocks worth offering for placing: every block name of the game except the ones that cannot be put down as they are - air, the moving parts of a piston, the liquids, portals, fire,
    /// the wall variants (a wall torch or sign is made by placing the torch or sign on a wall) and the technical ones. Sorted by name.
    /// </summary>
    public static List<string> PlaceableBlocks(IEnumerable<string> names)
    {
        var list = new List<string>();
        foreach (var n in names)
        {
            if (n.Contains('[')) continue;
            var plain = n.StartsWith("minecraft:", StringComparison.Ordinal) ? n[10..] : n;
            if (plain is "air" or "cave_air" or "void_air" or "moving_piston" or "piston_head" or "water" or "lava" or "bubble_column" or "nether_portal" or "end_portal" or "end_gateway" or "fire" or "soul_fire" or "frosted_ice" or "test_block" or "test_instance_block") continue;
            if (plain.EndsWith("_wall_sign", StringComparison.Ordinal) || plain.EndsWith("_wall_banner", StringComparison.Ordinal) || plain.EndsWith("_wall_head", StringComparison.Ordinal) || plain.EndsWith("_wall_skull", StringComparison.Ordinal)
                || plain.EndsWith("_wall_hanging_sign", StringComparison.Ordinal) || plain is "wall_torch" or "soul_wall_torch" or "redstone_wall_torch") continue;
            list.Add(n);
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }


    // ---- plans: what a click asks the server to do ----

    /// <summary>Break the block under the cursor (it becomes air). Null when nothing is pointed at.</summary>
    public EditRequest? PlanBreak()
    {
        if (Target is not { } t || t.Y < MinY || t.Y > MaxY) return null;
        return new EditRequest { Label = "break " + DisplayName(t.Block), Ops = [new EditOpDto { Op = "set", At = [t.X, t.Y, t.Z], Block = Air }] };
    }

    /// <summary>Put the block in hand against the face under the cursor, turned as a player would turn it. Null when nothing is pointed at, or the place is outside the world.</summary>
    public EditRequest? PlanPlace(double cameraYawDegrees)
    {
        if (Target is not { } t) return null;
        var (x, y, z) = t.PlaceAt;
        if (y < MinY || y > MaxY) return null;
        var block = SelectedBlock == Air ? Air : PlacementRules.Orient(SelectedBlock, t.Nx, t.Ny, t.Nz, t.Hy - Math.Floor(t.Hy), cameraYawDegrees);
        return new EditRequest { Label = (block == Air ? "break " : "place ") + DisplayName(block), Ops = [new EditOpDto { Op = "set", At = [x, y, z], Block = block }] };
    }

    /// <summary>Take the block under the cursor into the slot in hand (the eyedropper). False when nothing is pointed at, or it is not something to place.</summary>
    public bool PickBlock()
    {
        if (Target is not { } t || t.Block == Air) return false;
        SetSelectedBlock(t.Block);
        return true;
    }

    // ---- history ----

    private readonly List<string> _history = new();
    private int _cursor;   // edits before it are applied; those from it on were undone

    public int Undoable => _cursor;
    public int Redoable => _history.Count - _cursor;

    /// <summary>An edit went through: it is the newest; whatever was undone and not redone is forgotten.</summary>
    public void Record(string editId)
    {
        if (_cursor < _history.Count) _history.RemoveRange(_cursor, _history.Count - _cursor);
        _history.Add(editId);
        if (_history.Count > 200) _history.RemoveAt(0);
        _cursor = _history.Count;
    }

    /// <summary>The edit to undo next (null when none); call <see cref="Undone"/> once the server has done it.</summary>
    public string? NextUndo => _cursor > 0 ? _history[_cursor - 1] : null;

    /// <summary>The edit to redo next (null when none); call <see cref="Redone"/> once the server has done it.</summary>
    public string? NextRedo => _cursor < _history.Count ? _history[_cursor] : null;

    public void Undone() { if (_cursor > 0) _cursor--; }
    public void Redone() { if (_cursor < _history.Count) _cursor++; }
}
