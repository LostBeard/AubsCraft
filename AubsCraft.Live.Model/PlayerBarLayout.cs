namespace AubsCraft.Live.Model;

/// <summary>One sprite of a player's bar: its name in the HUD atlas (or, with <see cref="IsItem"/>, an item's name), where it goes in the game's own HUD pixels, and how much of it shows across.</summary>
public readonly record struct BarSprite(string Name, float X, float Y, float W, float H, bool IsItem = false, float ShowWidth = 1f);

/// <summary>A number on a bar: the count of a stack, or the experience level.</summary>
public readonly record struct BarText(string Text, float X, float Y, bool IsLevel);

/// <summary>
/// The HUD a player sees, laid out as the game lays it out, in the game's own HUD pixels with the hotbar's top left corner at 0, 0: hearts on the left and hunger on the right above the
/// experience bar, armor above the hearts, air above the hunger, the nine slots and the selection round the one in hand. A creative player has only the hotbar, a spectator nothing; a player
/// from a plugin that does not send the status (<c>status</c> null) has only their hearts.
/// </summary>
public static class PlayerBarLayout
{
    public const float Width = 182f, HotbarHeight = 22f;
    private const float SlotPitch = 20f, IconInset = 3f, IconSize = 16f, IconPitch = 8f, IconSprite = 9f;
    private const float RowAboveHotbar = -17f, ExperienceY = -7f;
    /// <summary>The game's survival, creative and adventure and spectator mode numbers.</summary>
    public const int Survival = 0, Creative = 1, Adventure = 2, Spectator = 3;

    /// <summary>The topmost pixel of what was laid out (negative: above the hotbar), for placing the bar over a head.</summary>
    public static float Build(LivePlayerStatus? status, float healthFraction, int gameMode, List<BarSprite> sprites, List<BarText> texts)
    {
        sprites.Clear();
        texts.Clear();
        if (gameMode == Spectator) return 0f;
        if (status == null)
        {
            Hearts((int)MathF.Ceiling(Math.Clamp(healthFraction, 0f, 1f) * 20f), 20, 0, sprites, out float top);
            return top;
        }

        float highest = 0f;
        if (gameMode != Creative)
        {
            Hearts(status.HealthPoints, status.MaxHealth, status.Absorption, sprites, out float heartsTop);
            highest = heartsTop;

            // Armor stands one row over the hearts, when there is any
            if (status.Armor > 0)
            {
                float y = heartsTop - 10f;
                for (int i = 0; i < 10; i++)
                {
                    int points = status.Armor - i * 2;
                    sprites.Add(new BarSprite(points >= 2 ? "armor_full" : points == 1 ? "armor_half" : "armor_empty", i * IconPitch, y, IconSprite, IconSprite));
                }
                highest = Math.Min(highest, y);
            }

            // Hunger runs from the right, its first point at the far end
            for (int i = 0; i < 10; i++)
            {
                float x = Width - IconSprite - i * IconPitch;
                sprites.Add(new BarSprite("food_empty", x, RowAboveHotbar, IconSprite, IconSprite));
                int points = status.Food - i * 2;
                if (points >= 2) sprites.Add(new BarSprite("food_full", x, RowAboveHotbar, IconSprite, IconSprite));
                else if (points == 1) sprites.Add(new BarSprite("food_half", x, RowAboveHotbar, IconSprite, IconSprite));
            }

            // Air, over the hunger, only while it is being used
            if (status.Air < 20)
            {
                float y = RowAboveHotbar - 10f;
                int bubbles = (status.Air + 1) / 2;
                for (int i = 0; i < 10; i++)
                    sprites.Add(new BarSprite(i < bubbles ? "air" : "air_empty", Width - IconSprite - i * IconPitch, y, IconSprite, IconSprite));
                highest = Math.Min(highest, y);
            }

            sprites.Add(new BarSprite("experience_bar_background", 0f, ExperienceY, Width, 5f));
            if (status.XpProgress > 0f)
                sprites.Add(new BarSprite("experience_bar_progress", 0f, ExperienceY, Width, 5f, ShowWidth: Math.Clamp(status.XpProgress, 0f, 1f)));
            if (status.XpLevel > 0)
                texts.Add(new BarText(status.XpLevel.ToString(System.Globalization.CultureInfo.InvariantCulture), Width / 2f, ExperienceY - 4f, true));
        }

        sprites.Add(new BarSprite("hotbar", 0f, 0f, Width, HotbarHeight));
        int selected = Math.Clamp(status.SelectedSlot, 0, 8);
        for (int i = 0; i < status.Hotbar.Count && i < 9; i++)
        {
            var slot = status.Hotbar[i];
            if (string.IsNullOrEmpty(slot.Item)) continue;
            sprites.Add(new BarSprite(slot.Item, IconInset + i * SlotPitch, IconInset, IconSize, IconSize, IsItem: true));
            if (slot.Count > 1)
                texts.Add(new BarText(slot.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), IconInset + i * SlotPitch + IconSize, IconInset + IconSize, false));
        }
        sprites.Add(new BarSprite("hotbar_selection", selected * SlotPitch - 1f, -1f, 24f, 23f));
        return Math.Min(highest, -1f);
    }

    // Hearts from the left, ten to a row, each row above the last; the rows close up when there are many, as the game's do. The absorption hearts come after the real ones.
    private static void Hearts(int healthPoints, int maxHealth, int absorption, List<BarSprite> sprites, out float top)
    {
        int hearts = (Math.Max(maxHealth, 1) + 1) / 2, extra = (absorption + 1) / 2, total = hearts + extra;
        int rows = (total + 9) / 10;
        float rowHeight = Math.Max(10 - (rows - 2), 3);
        top = RowAboveHotbar - (rows - 1) * rowHeight;
        for (int i = 0; i < total; i++)
        {
            float x = i % 10 * IconPitch, y = RowAboveHotbar - i / 10 * rowHeight;
            sprites.Add(new BarSprite("heart/container", x, y, IconSprite, IconSprite));
            if (i < hearts)
            {
                int points = healthPoints - i * 2;
                if (points >= 2) sprites.Add(new BarSprite("heart/full", x, y, IconSprite, IconSprite));
                else if (points == 1) sprites.Add(new BarSprite("heart/half", x, y, IconSprite, IconSprite));
            }
            else
            {
                int points = absorption - (i - hearts) * 2;
                if (points >= 2) sprites.Add(new BarSprite("heart/absorbing_full", x, y, IconSprite, IconSprite));
                else if (points == 1) sprites.Add(new BarSprite("heart/absorbing_half", x, y, IconSprite, IconSprite));
            }
        }
    }
}
