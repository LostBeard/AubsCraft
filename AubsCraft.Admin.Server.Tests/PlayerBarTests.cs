using AubsCraft.Assets;
using AubsCraft.Assets.Model;
using AubsCraft.Live.Model;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>The bar over a player's head: laid out as the game lays out its own HUD, from the game's own pictures.</summary>
public class PlayerBarTests
{
    private static LivePlayerStatus Status(int health = 20, int max = 20, int absorption = 0, int food = 20, int armor = 0, int air = 20, int selected = 0, int level = 0, float xp = 0f, HotbarSlot[]? hotbar = null)
        => new(health, max, absorption, food, armor, air, selected, level, xp, hotbar ?? new HotbarSlot[9]);

    private static (List<BarSprite> Sprites, List<BarText> Texts, float Top) Lay(LivePlayerStatus? status, int mode = PlayerBarLayout.Survival, float fraction = 1f)
    {
        var sprites = new List<BarSprite>();
        var texts = new List<BarText>();
        float top = PlayerBarLayout.Build(status, fraction, mode, sprites, texts);
        return (sprites, texts, top);
    }

    private static int Count(List<BarSprite> s, string name) => s.Count(p => p.Name == name);

    [Test]
    public void HalfHearts_AndHunger_FillFromTheirEnds()
    {
        var (s, _, _) = Lay(Status(health: 15, food: 7));
        Assert.That(Count(s, "heart/container"), Is.EqualTo(10));
        Assert.That(Count(s, "heart/full"), Is.EqualTo(7));
        Assert.That(Count(s, "heart/half"), Is.EqualTo(1));
        // The half heart is the eighth from the left
        Assert.That(s.Single(p => p.Name == "heart/half").X, Is.EqualTo(7 * 8f));
        Assert.That(Count(s, "food_empty"), Is.EqualTo(10));
        Assert.That(Count(s, "food_full"), Is.EqualTo(3));
        Assert.That(Count(s, "food_half"), Is.EqualTo(1));
        // Hunger's first point is at the far right, and the half is the fourth in from it
        Assert.That(s.Where(p => p.Name == "food_full").Max(p => p.X), Is.EqualTo(182f - 9f));
        Assert.That(s.Single(p => p.Name == "food_half").X, Is.EqualTo(182f - 9f - 3 * 8f));
    }

    [Test]
    public void TheSelection_IsRoundTheSlotInHand_AndItemsSitInTheirSlots()
    {
        var hotbar = new HotbarSlot[9];
        hotbar[0] = new("diamond_sword", 1);
        hotbar[3] = new("bread", 12);
        var (s, t, _) = Lay(Status(selected: 3, hotbar: hotbar));
        Assert.That(s.Single(p => p.Name == "hotbar_selection").X, Is.EqualTo(59f), "the game's selection is one pixel out from slot 3 at 60");
        var bread = s.Single(p => p.Name == "bread");
        Assert.That((bread.IsItem, bread.X, bread.Y, bread.W), Is.EqualTo((true, 63f, 3f, 16f)));
        Assert.That(t.Select(x => x.Text), Is.EqualTo(new[] { "12" }), "a stack of one has no number");
        Assert.That(s.IndexOf(s.Single(p => p.Name == "hotbar")), Is.LessThan(s.IndexOf(bread)), "the slots under the icons");
        Assert.That(s.IndexOf(s.Single(p => p.Name == "hotbar_selection")), Is.GreaterThan(s.IndexOf(bread)), "the selection over the icons");
    }

    [Test]
    public void ACreativePlayer_HasOnlyTheHotbar_AndASpectatorNothing()
    {
        var hotbar = new HotbarSlot[9];
        hotbar[0] = new("stone", 64);
        var creative = Lay(Status(hotbar: hotbar, level: 5, xp: 0.5f, armor: 8), PlayerBarLayout.Creative);
        Assert.That(creative.Sprites.Any(p => p.Name.StartsWith("heart") || p.Name.StartsWith("food") || p.Name.StartsWith("armor") || p.Name.StartsWith("experience")), Is.False);
        Assert.That(Count(creative.Sprites, "hotbar"), Is.EqualTo(1));
        Assert.That(Lay(Status(), PlayerBarLayout.Spectator).Sprites, Is.Empty);
    }

    [Test]
    public void ArmorAir_AndExperience_ShowWhenThereIsAny()
    {
        var plain = Lay(Status());
        Assert.That(plain.Sprites.Any(p => p.Name.StartsWith("armor") || p.Name.StartsWith("air")), Is.False);
        Assert.That(plain.Sprites.Any(p => p.Name == "experience_bar_progress"), Is.False);
        Assert.That(plain.Texts, Is.Empty);
        Assert.That(plain.Top, Is.EqualTo(-17f));

        var (s, t, top) = Lay(Status(armor: 7, air: 11, level: 30, xp: 0.25f));
        Assert.That(Count(s, "armor_full"), Is.EqualTo(3));
        Assert.That(Count(s, "armor_half"), Is.EqualTo(1));
        Assert.That(Count(s, "air"), Is.EqualTo(6), "the game rounds a part bubble up");
        Assert.That(Count(s, "air_empty"), Is.EqualTo(4));
        Assert.That(s.Single(p => p.Name == "experience_bar_progress").ShowWidth, Is.EqualTo(0.25f));
        Assert.That(t.Single(x => x.IsLevel).Text, Is.EqualTo("30"));
        Assert.That(top, Is.EqualTo(-27f), "armor stands one row over the hearts");
    }

    [Test]
    public void ManyHearts_MakeRowsThatCloseUp_AndAbsorptionComesLast()
    {
        var (s, _, top) = Lay(Status(health: 40, max: 40, absorption: 3));
        Assert.That(Count(s, "heart/container"), Is.EqualTo(22));
        Assert.That(Count(s, "heart/full"), Is.EqualTo(20));
        Assert.That(Count(s, "heart/absorbing_full"), Is.EqualTo(1));
        Assert.That(Count(s, "heart/absorbing_half"), Is.EqualTo(1));
        // Three rows: the game's row height is 10 - (rows - 2) = 9
        Assert.That(top, Is.EqualTo(-17f - 2 * 9f));
    }

    [Test]
    public void APlayerFromAnOldPlugin_HasOnlyHearts_FromTheHealthItSends()
    {
        var (s, t, _) = Lay(null, fraction: 0.5f);
        Assert.That(Count(s, "heart/full"), Is.EqualTo(5));
        Assert.That(s.All(p => p.Name.StartsWith("heart/")), Is.True);
        Assert.That(t, Is.Empty);
    }

    [Test, Category("RealJar")]
    public async Task TheAtlas_HasTheGamesOwnSprites_AndAnIconForEveryItem()
    {
        using var jar = ClientJar.Open(await JarLocator.FindOrDownloadAsync("1.21.5"));
        var items = new ItemAtlasBuilder(jar, "1.21.5").Build();
        var gui = new GuiAtlasBuilder(jar, "1.21.5").Build(items.Assets, items.Rgba);
        var a = gui.Assets;
        Assert.That(gui.Rgba, Has.Length.EqualTo(a.AtlasWidth * a.AtlasHeight * 4));

        foreach (var name in GuiAtlasBuilder.SpriteNames) Assert.That(a.Sprites.ContainsKey(name), name);
        Assert.That(a.Sprites["hotbar"][2..], Is.EqualTo(new[] { 182, 22 }));
        Assert.That(a.Sprites["hotbar_selection"][2..], Is.EqualTo(new[] { 24, 23 }));
        Assert.That(a.Sprites["heart/full"][2..], Is.EqualTo(new[] { 9, 9 }));

        int Opaque(int x, int y, int w, int h)
        {
            int n = 0;
            for (int py = y; py < y + h; py++)
                for (int px = x; px < x + w; px++)
                    if (gui.Rgba[(py * a.AtlasWidth + px) * 4 + 3] > 0) n++;
            return n;
        }
        Assert.That(Opaque(a.Sprites["heart/full"][0], a.Sprites["heart/full"][1], 9, 9), Is.GreaterThan(30), "the heart is really in its place");
        foreach (var name in new[] { "diamond_sword", "stone", "oak_planks", "bread", "torch", "oak_stairs", "water_bucket" })
        {
            Assert.That(a.Items.ContainsKey(name), name);
            var at = a.Items[name];
            Assert.That(Opaque(at[0], at[1], GuiAssets.IconSize, GuiAssets.IconSize), Is.GreaterThan(40), name + "'s icon has pixels");
        }

        // A block's icon is a cube: three faces of three brightnesses, and the corners of its square are empty
        var stone = a.Items["stone"];
        Assert.That(Opaque(stone[0], stone[1], 2, 2), Is.Zero, "the corner of the cube's square");
        double Mean(int fx, int fy)
        {
            int x = stone[0] + fx, y = stone[1] + fy;
            double sum = 0;
            for (int dy = 0; dy < 3; dy++) for (int dx = 0; dx < 3; dx++) sum += gui.Rgba[((y + dy) * a.AtlasWidth + x + dx) * 4];
            return sum / 9;
        }
        double top = Mean(14, 8), left = Mean(8, 18), right = Mean(22, 18);
        Assert.That(top, Is.GreaterThan(left), "lit from above");
        Assert.That(left, Is.GreaterThan(right), "the left face brighter than the right");
    }
}
