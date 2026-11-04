using System.Drawing;
using AubsCraft.Assets.Model;
using AubsCraft.Live.Model;
using SpawnDev.GameUI;
using SpawnDev.GameUI.Elements;
using SpawnDev.SpawnJS.JSObjects;

namespace AubsCraft.Admin.Rendering;

/// <summary>A player's bar to draw over the world this frame: where their head is on screen (pixels, the bottom centre of the name tag), how big, and what it shows.</summary>
public readonly record struct PlayerBar(float X, float Y, float Scale, LivePlayerStatus? Status, float Health, int GameMode);

public sealed partial class ViewerHud
{
    // ---- what each player's own HUD shows, over their head (the game's own sprites: GuiAtlasBuilder) ----

    private GPUTextureView? _guiView;
    private GuiAssets? _gui;
    private PlayerBar[] _bars = System.Array.Empty<PlayerBar>();
    private int _barCount;
    private readonly List<BarSprite> _barSprites = new();
    private readonly List<BarText> _barTexts = new();
    private readonly List<PlayerBar> _barSort = new();
    private (LivePlayerStatus? Status, float Health, int GameMode)? _eyesBar;

    /// <summary>The bars of the frame's players are drawn under everything else of the HUD, and take no clicks.</summary>
    private sealed class PlayerBarsLayer : UIElement
    {
        private readonly ViewerHud _hud;
        public PlayerBarsLayer(ViewerHud hud) => _hud = hud;
        public override UIElement? HitTest(System.Numerics.Vector2 screenPos) => null;
        public override void Draw(UIRenderer renderer) => _hud.DrawBars(renderer);
    }

    /// <summary>The HUD atlas: until it is given, players have no bars.</summary>
    public void SetGuiAtlas(GPUTextureView view, GuiAssets assets)
    {
        _guiView = view;
        _gui = assets;
    }

    /// <summary>The bars to draw this frame.</summary>
    public void SetPlayerBars(IReadOnlyList<PlayerBar>? bars)
    {
        _barCount = bars?.Count ?? 0;
        if (_barCount == 0) return;
        if (_bars.Length < _barCount) _bars = new PlayerBar[_barCount * 2];
        for (int i = 0; i < _barCount; i++) _bars[i] = bars![i];
    }

    /// <summary>The camera is looking through a player's eyes: their HUD goes where they see it, along the bottom of the view. Null: nobody's.</summary>
    public void SetEyesBar((LivePlayerStatus? Status, float Health, int GameMode)? bar) => _eyesBar = bar;

    private void DrawBars(UIRenderer renderer)
    {
        if (_guiView == null || _gui == null) return;
        if (_settings.PlayerBars && _barCount > 0)
        {
            // Far ones first: a nearer player's bar lies over them
            _barSort.Clear();
            for (int i = 0; i < _barCount; i++) _barSort.Add(_bars[i]);
            _barSort.Sort((a, b) => a.Scale.CompareTo(b.Scale));
            float lineH = _ui.Renderer.GetLineHeight(FontSize.Caption);
            foreach (var bar in _barSort)
                DrawBar(renderer, bar.Status, bar.Health, bar.GameMode, bar.X, bar.Y - lineH - 2f, bar.Scale);
        }
        if (_eyesBar is { } eyes)
        {
            // The game's own rule for its HUD's size: as big as a whole number of times 320 x 240 fits the window
            float scale = Math.Max(1f, MathF.Floor(Math.Min(_root.Width / 320f, _root.Height / 240f)));
            DrawBar(renderer, eyes.Status, eyes.Health, eyes.GameMode, _root.Width / 2f, _root.Height, scale);
        }
    }

    // One player's HUD with its bottom centre at (x, bottom), scale screen pixels to the game's HUD pixel
    private void DrawBar(UIRenderer renderer, LivePlayerStatus? status, float health, int gameMode, float x, float bottom, float scale)
    {
        PlayerBarLayout.Build(status, health, gameMode, _barSprites, _barTexts);
        if (_barSprites.Count == 0) return;
        float lowest = float.MinValue;
        foreach (var s in _barSprites) lowest = Math.Max(lowest, s.Y + s.H);
        float ox = MathF.Round(x - PlayerBarLayout.Width * scale / 2f), oy = MathF.Round(bottom - lowest * scale);
        float texW = _gui!.AtlasWidth, texH = _gui.AtlasHeight;

        foreach (var s in _barSprites)
        {
            int u, v, w, h;
            if (s.IsItem)
            {
                if (!_gui.Items.TryGetValue(s.Name, out var icon)) continue;   // an item the game's jar does not have (a mod's): an empty slot to look at
                (u, v, w, h) = (icon[0], icon[1], GuiAssets.IconSize, GuiAssets.IconSize);
            }
            else
            {
                if (!_gui.Sprites.TryGetValue(s.Name, out var at)) continue;
                (u, v, w, h) = (at[0], at[1], at[2], at[3]);
            }
            float show = s.ShowWidth;
            renderer.DrawImage(_guiView!, ox + s.X * scale, oy + s.Y * scale, s.W * scale * show, s.H * scale,
                u / texW, v / texH, (u + w * show) / texW, (v + h) / texH);
        }

        // The numbers are drawn only where they can be read
        if (scale < MinScaleForNumbers) return;
        float size = 8f * scale * 1.15f;
        foreach (var t in _barTexts)
        {
            float width = renderer.MeasureText(t.Text, size);
            float tx = t.IsLevel ? ox + t.X * scale - width / 2f : ox + t.X * scale - width + scale;
            float ty = t.IsLevel ? oy + t.Y * scale - size * 0.6f : oy + t.Y * scale - size * 0.95f;
            var colour = t.IsLevel ? Color.FromArgb(255, 128, 255, 32) : Color.White;
            renderer.DrawText(t.Text, MathF.Round(tx + scale), MathF.Round(ty + scale), size, Color.FromArgb(255, 30, 30, 30));
            renderer.DrawText(t.Text, MathF.Round(tx), MathF.Round(ty), size, colour);
        }
    }

    private const float MinScaleForNumbers = 0.75f;
}
