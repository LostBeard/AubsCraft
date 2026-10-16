using AubsCraft.Assets.Model;
using AubsCraft.Live.Model.BlockEntities;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.VoxelEngine.Rendering;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// The writing on signs, as pictures in a band of slots of the entity atlas (<see cref="EntityAssets.TextY"/>): each distinct writing (the same words in the same dye on a sign of the same kind)
/// is drawn once into an OffscreenCanvas, four lines centred on the board (the game draws a sign's writing at four times the board's pixel size: 96 x 48 for a standing or wall sign, 56 x 40 for a
/// hanging one), and copied GPU to GPU into a slot. A slot is 192 wide and the writing takes the left half: the quad that shows it has no depth, and its back face reads the clear right half. When
/// the slots are full the writing seen longest ago gives up its slot. A few new pictures are drawn a frame, the others wait their turn (the sign is plain for a moment).
/// </summary>
public sealed class SignTextAtlas
{
    /// <summary>New pictures drawn in a frame, at most.</summary>
    public const int NewPerFrame = 4;
    /// <summary>The writing's width and height on a standing or wall sign's board, and on a hanging sign's, in picture pixels.</summary>
    public const int BoardWidth = 96, BoardHeight = 48, HangingWidth = 56, HangingHeight = 40;

    private readonly EntityTextureAtlas _atlas;
    private readonly EntityAssets _assets;
    private readonly Dictionary<string, int> _slotOf = new(StringComparer.Ordinal);
    private readonly string?[] _owner;
    private readonly long[] _lastUsed;
    private long _frame;
    private int _rendered;

    public SignTextAtlas(EntityTextureAtlas atlas, EntityAssets assets)
    {
        _atlas = atlas;
        _assets = assets;
        int slots = assets.TextColumns * assets.TextRows;
        _owner = new string?[slots];
        _lastUsed = new long[slots];
    }

    public int SlotCount => _owner.Length;
    /// <summary>Pictures in slots now.</summary>
    public int Count => _slotOf.Count;

    public void BeginFrame()
    {
        _frame++;
        _rendered = 0;
    }

    /// <summary>The picture key of a sign's writing (the same for signs that say the same in the same way).</summary>
    public static string KeyOf(SignText sign, bool hanging) => (hanging ? "h" : "s") + sign.Key;

    /// <summary>Where a picture's slot starts in the atlas (and note it is used this frame), false when it has none.</summary>
    public bool TryGetOrigin(string key, out int x, out int y)
    {
        if (_slotOf.TryGetValue(key, out int slot))
        {
            _lastUsed[slot] = _frame;
            x = (slot % _assets.TextColumns) * _assets.TextWidth;
            y = _assets.TextY + (slot / _assets.TextColumns) * _assets.TextHeight;
            return true;
        }
        x = y = 0;
        return false;
    }

    /// <summary>Make sure a sign's writing has a slot: true when it has one (now or already), false when it had to wait (the frame's drawing is used up, or every slot is in use this frame).</summary>
    public bool Ensure(SignText sign, bool hanging)
    {
        var key = KeyOf(sign, hanging);
        if (_slotOf.ContainsKey(key)) { _lastUsed[_slotOf[key]] = _frame; return true; }
        if (_rendered >= NewPerFrame) return false;
        int slot = FreeSlot();
        if (slot < 0) return false;
        _rendered++;
        if (_owner[slot] is { } old) _slotOf.Remove(old);
        _owner[slot] = key;
        _slotOf[key] = slot;
        _lastUsed[slot] = _frame;
        using var canvas = Draw(sign, hanging);
        _atlas.WriteCanvas((slot % _assets.TextColumns) * _assets.TextWidth, _assets.TextY + (slot / _assets.TextColumns) * _assets.TextHeight, canvas);
        return true;
    }

    // An empty slot, or the one least recently used before this frame; none when all are in use this frame
    private int FreeSlot()
    {
        int victim = -1;
        long oldest = long.MaxValue;
        for (int i = 0; i < _owner.Length; i++)
        {
            if (_owner[i] == null) return i;
            if (_lastUsed[i] < _frame && _lastUsed[i] < oldest) { oldest = _lastUsed[i]; victim = i; }
        }
        return victim;
    }

    private OffscreenCanvas Draw(SignText sign, bool hanging)
    {
        var canvas = new OffscreenCanvas(_assets.TextWidth, _assets.TextHeight);
        using var ctx = canvas.Get2DContext();
        int width = hanging ? HangingWidth : BoardWidth, height = hanging ? HangingHeight : BoardHeight;
        int maxWidth = hanging ? 50 : 90;
        // Four lines of ten pixels, the block of them centred in the board's picture
        int top = (height - 40) / 2;
        ctx.Font = "bold 9px monospace";
        ctx.TextAlign = "center";
        ctx.TextBaseline = "alphabetic";
        var (fr, fg, fb) = SignTextCodec.TextColour(sign.Color, sign.Glowing);
        var (or, og, ob) = SignTextCodec.TextColour(sign.Color, false);
        ctx.FillStyle = $"rgb({fr},{fg},{fb})";
        ctx.StrokeStyle = $"rgb({or},{og},{ob})";
        ctx.LineWidth = 2;
        ctx.LineJoin = "round";
        for (int i = 0; i < 4 && i < sign.Lines.Length; i++)
        {
            var line = sign.Lines[i];
            if (string.IsNullOrEmpty(line)) continue;
            double baseline = top + i * 10 + 8;
            double measured = ctx.MeasureText(line).Width;
            ctx.Save();
            ctx.Translate(width / 2.0, baseline);
            if (measured > maxWidth) ctx.Scale(maxWidth / measured, 1);
            if (sign.Glowing) ctx.StrokeText(line, 0, 0);
            ctx.FillText(line, 0, 0);
            ctx.Restore();
        }
        return canvas;
    }
}
