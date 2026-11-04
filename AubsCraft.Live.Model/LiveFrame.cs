namespace AubsCraft.Live.Model;

/// <summary>What the world looked like at one sampling: every entity in the loaded chunks, per world, from the AubsCraft Live plugin.</summary>
public sealed class LiveFrame
{
    /// <summary>The first world's full time in ticks (monotonic while the server runs): the clock of the frames.</summary>
    public long FullTime { get; init; }
    /// <summary>The first world's time of day in ticks (0 sunrise, 6000 noon, 12000 sunset, 18000 midnight).</summary>
    public long DayTime { get; init; }
    public bool Raining { get; init; }
    public bool Thundering { get; init; }
    public required IReadOnlyList<LiveWorld> Worlds { get; init; }
}

public sealed class LiveWorld
{
    /// <summary>"world", "world_nether", "world_the_end", or whatever the server calls it.</summary>
    public required string Name { get; init; }
    public required IReadOnlyList<LiveEntity> Entities { get; init; }
    public required IReadOnlyList<LivePlayer> Players { get; init; }
}

/// <summary>One entity at one moment. Angles are degrees; velocity is blocks per tick.</summary>
public readonly record struct LiveEntity(
    int Id, string Type, float X, float Y, float Z, float Yaw, float Pitch, float BodyYaw, int Flags,
    float Vx, float Vy, float Vz, string? Name, string? Variant)
{
    public bool OnGround => (Flags & LiveFlags.OnGround) != 0;
    public bool Sneaking => (Flags & LiveFlags.Sneaking) != 0;
    public bool Sprinting => (Flags & LiveFlags.Sprinting) != 0;
    public bool Swimming => (Flags & LiveFlags.Swimming) != 0;
    public bool Gliding => (Flags & LiveFlags.Gliding) != 0;
    public bool Sleeping => (Flags & LiveFlags.Sleeping) != 0;
    public bool Baby => (Flags & LiveFlags.Baby) != 0;
    public bool Burning => (Flags & LiveFlags.Burning) != 0;
    public bool Glowing => (Flags & LiveFlags.Glowing) != 0;
    public bool Invisible => (Flags & LiveFlags.Invisible) != 0;
    public bool IsPlayer => (Flags & LiveFlags.Player) != 0;
    public bool Riding => (Flags & LiveFlags.Riding) != 0;
    public bool Dead => (Flags & LiveFlags.Dead) != 0;
}

/// <summary>A player: who, what they look like and what they hold. <see cref="Health"/> is 0 to 1. <see cref="Status"/> is what their own HUD shows (null from an older plugin, which does not send it).</summary>
public sealed record LivePlayer(int EntityId, string Uuid, string Name, string SkinUrl, bool Slim, int GameMode, string HeldItem, float Health, LivePlayerStatus? Status = null);

/// <summary>One slot of a player's hotbar: the item's name ("diamond_sword"; "" for an empty slot) and how many.</summary>
public readonly record struct HotbarSlot(string Item, int Count);

/// <summary>
/// What a player's own HUD shows, as the game counts it: <see cref="HealthPoints"/> of <see cref="MaxHealth"/> (two to a heart), <see cref="Absorption"/> extra points, <see cref="Food"/> of 20,
/// <see cref="Armor"/> of 20, <see cref="Air"/> of 20 (20 is a full breath), the nine hotbar slots with <see cref="SelectedSlot"/> in hand, the experience level and how far to the next (0 to 1).
/// </summary>
public sealed record LivePlayerStatus(int HealthPoints, int MaxHealth, int Absorption, int Food, int Armor, int Air, int SelectedSlot, int XpLevel, float XpProgress, IReadOnlyList<HotbarSlot> Hotbar)
{
    // A record compares a list by reference: two statuses are the same when their slots are
    public bool Equals(LivePlayerStatus? other) => other != null && (HealthPoints, MaxHealth, Absorption, Food, Armor, Air, SelectedSlot, XpLevel, XpProgress) == (other.HealthPoints, other.MaxHealth, other.Absorption, other.Food, other.Armor, other.Air, other.SelectedSlot, other.XpLevel, other.XpProgress) && Hotbar.SequenceEqual(other.Hotbar);
    public override int GetHashCode() => HashCode.Combine(HealthPoints, Food, SelectedSlot, Hotbar.Count);
}

public static class LiveFlags
{
    public const int OnGround = 1, Sneaking = 2, Sprinting = 4, Swimming = 8, Gliding = 16, Sleeping = 32, Baby = 64, Burning = 128,
        Glowing = 256, Invisible = 512, HasName = 1024, Player = 2048, Riding = 4096, Dead = 8192,
        /// <summary>Its arm is swinging (it hits, mines, places).</summary>
        Swinging = 16384,
        /// <summary>It is using what it holds (eating, drinking, drawing a bow, blocking with a shield).</summary>
        Using = 32768;
}
