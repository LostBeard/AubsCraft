using System.Drawing;
using SpawnDev.GameUI;
using SpawnDev.GameUI.Elements;
using SpawnDev.GameUI.Input;

namespace AubsCraft.Admin.Rendering;

/// <summary>The blocks in hand, as a row of icons: the one in hand lit, the one pointed at brighter. The icons are put on by the headset's icon pass after the HUD (see <see cref="BlockIconAtlas"/>).</summary>
internal sealed class XrHotbar : UIHotbar
{
    private readonly Func<BlockIconAtlas?> _icons;
    private readonly List<IconQuad> _sink;
    private int _hover = -1;

    public XrHotbar(Func<BlockIconAtlas?> icons, List<IconQuad> sink)
    {
        _icons = icons;
        _sink = sink;
        SlotCount = 9;
    }

    public override void Update(GameInput input, float dt)
    {
        base.Update(input, dt);
        _hover = -1;
        if (!Visible || input.PrimaryPointer?.ScreenPosition is not { } p) return;
        var b = ScreenBounds;
        float lx = p.X - b.X - 6, ly = p.Y - b.Y - 6;
        if (ly < 0 || ly >= SlotSize) return;
        int col = (int)(lx / (SlotSize + SlotGap));
        if (lx >= 0 && col >= 0 && col < SlotCount && lx - col * (SlotSize + SlotGap) <= SlotSize) _hover = col;
    }

    public override void Draw(UIRenderer renderer)
    {
        if (!Visible) return;
        Width = SlotCount * SlotSize + (SlotCount - 1) * SlotGap + 12;
        Height = SlotSize + 12;
        var bounds = ScreenBounds;
        var theme = UITheme.Current;
        renderer.DrawRoundedRect(bounds.X, bounds.Y, Width, Height, 14, Color.FromArgb(235, 12, 16, 24));
        var icons = _icons();
        for (int i = 0; i < SlotCount; i++)
        {
            float sx = bounds.X + 6 + i * (SlotSize + SlotGap), sy = bounds.Y + 6;
            bool selected = i == SelectedSlot;
            var back = selected ? Color.FromArgb(255, 30, 110, 130) : i == _hover ? Color.FromArgb(255, 48, 92, 120) : Color.FromArgb(255, 30, 42, 58);
            renderer.DrawRoundedRect(sx, sy, SlotSize, SlotSize, 8, back);
            if (selected) renderer.DrawRoundedRing(sx - 2, sy - 2, SlotSize + 4, SlotSize + 4, 10, 3, Color.FromArgb(255, 120, 235, 250));
            renderer.DrawText((i + 1).ToString(), sx + 5, sy + 2, FontSize.Caption, Color.FromArgb(200, 190, 205, 220));
            int cell = icons?.CellOf(GetSlot(i).Tag as string) ?? -1;
            if (cell >= 0) _sink.Add(new IconQuad(sx + 6, sy + 6, SlotSize - 12, cell));
            else if (!string.IsNullOrEmpty(GetSlot(i).Label))
            {
                string label = GetSlot(i).Label!;
                float tw = renderer.MeasureText(label, FontSize.Caption);
                renderer.DrawText(label, sx + (SlotSize - tw) / 2, sy + SlotSize / 2 - 6, FontSize.Caption, theme.TextPrimary);
            }
        }
    }
}

/// <summary>The inventory: the blocks as a page of icons, nine across, with a pager outside. A block is chosen by its icon.</summary>
internal sealed class XrBlockGrid : UIElement
{
    public const int Columns = 9, Rows = 3, CellSize = 80;

    private readonly Func<IReadOnlyList<string>> _names;
    private readonly Func<BlockIconAtlas?> _icons;
    private readonly List<IconQuad> _sink;
    private int _hover = -1;

    public XrBlockGrid(Func<IReadOnlyList<string>> names, Func<BlockIconAtlas?> icons, List<IconQuad> sink)
    {
        _names = names;
        _icons = icons;
        _sink = sink;
        Width = Columns * CellSize;
        Height = Rows * CellSize;
    }

    /// <summary>A block was chosen: its index in the list.</summary>
    public Action<int>? OnPick { get; set; }

    public int Page { get; set; }
    public int PerPage => Columns * Rows;
    public int PageCount => Math.Max(1, (_names().Count + PerPage - 1) / PerPage);

    /// <summary>The block the pointer is over (its registry name), or null.</summary>
    public string? HoveredName => _hover >= 0 && Page * PerPage + _hover < _names().Count ? _names()[Page * PerPage + _hover] : null;

    public void Next() => Page = (Page + 1) % PageCount;
    public void Previous() => Page = (Page + PageCount - 1) % PageCount;

    public override void Update(GameInput input, float dt)
    {
        if (!Visible) { _hover = -1; return; }
        _hover = -1;
        var b = ScreenBounds;
        foreach (var pointer in input.Pointers)
        {
            if (pointer.ScreenPosition is not { } p) continue;
            float lx = p.X - b.X, ly = p.Y - b.Y;
            if (lx < 0 || ly < 0 || lx >= Columns * CellSize || ly >= Rows * CellSize) continue;
            int index = (int)(ly / CellSize) * Columns + (int)(lx / CellSize);
            if (Page * PerPage + index >= _names().Count) continue;
            _hover = index;
            if (pointer.WasReleased) { int pick = Page * PerPage + index; input.ClaimClick(pointer, this, () => OnPick?.Invoke(pick)); }
        }
        base.Update(input, dt);
    }

    public override void Draw(UIRenderer renderer)
    {
        if (!Visible) return;
        var b = ScreenBounds;
        var names = _names();
        var icons = _icons();
        for (int i = 0; i < PerPage; i++)
        {
            int index = Page * PerPage + i;
            if (index >= names.Count) break;
            float x = b.X + (i % Columns) * CellSize, y = b.Y + (i / Columns) * CellSize;
            bool hover = i == _hover;
            renderer.DrawRoundedRect(x + 3, y + 3, CellSize - 6, CellSize - 6, 10, hover ? Color.FromArgb(255, 48, 92, 120) : Color.FromArgb(255, 26, 36, 50));
            if (hover) renderer.DrawRoundedRing(x + 1, y + 1, CellSize - 2, CellSize - 2, 11, 2, Color.FromArgb(255, 120, 235, 250));
            int cell = icons?.CellOf(names[index]) ?? -1;
            if (cell >= 0) _sink.Add(new IconQuad(x + 8, y + 8, CellSize - 16, cell));
        }
        base.Draw(renderer);
    }
}
