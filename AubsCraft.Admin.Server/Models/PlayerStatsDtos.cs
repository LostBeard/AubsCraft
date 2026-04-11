namespace AubsCraft.Admin.Server.Models;

public class PlayerSummary
{
    public string UUID { get; set; } = "";
    public string Name { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public DateTime? LastLogin { get; set; }
    public DateTime? LastLogout { get; set; }
    public long PlayTimeTicks { get; set; }
    public string PlayTimeFormatted => FormatTicks(PlayTimeTicks);
    public string Platform { get; set; } = "Java"; // Java, Bedrock
    public string? DeviceOS { get; set; } // Windows, Android, iOS, Xbox, PlayStation, Switch, etc.
    public bool IsVR { get; set; }
    private static string FormatTicks(long ticks)
    {
        var ts = TimeSpan.FromMilliseconds(ticks * 50.0); // 20 ticks/sec = 50ms/tick
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m";
        return "< 1m";
    }
}

public class PlayerProfile
{
    public string UUID { get; set; } = "";
    public string Name { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public bool GodMode { get; set; }
    public DateTime? LastLogin { get; set; }
    public DateTime? LastLogout { get; set; }
    public DateTime? FirstSeen { get; set; }
    public string Platform { get; set; } = "Java";
    public string? DeviceOS { get; set; }
    public bool IsVR { get; set; }
    public string? ClientVersion { get; set; }

    // Native stats (ticks = 1/20th second)
    public long PlayTimeTicks { get; set; }
    public long TotalWorldTimeTicks { get; set; }
    public long Deaths { get; set; }
    public long MobKills { get; set; }
    public long DamageTaken { get; set; }
    public long DamageDealt { get; set; }
    public long Jumps { get; set; }
    public long TimeSinceDeathTicks { get; set; }
    public long SleepInBed { get; set; }

    // Distance (stored in cm by Minecraft)
    public long WalkDistanceCm { get; set; }
    public long SprintDistanceCm { get; set; }
    public long CrouchDistanceCm { get; set; }
    public long FlyDistanceCm { get; set; }
    public long FallDistanceCm { get; set; }
    public long SwimDistanceCm { get; set; }
    public long TotalDistanceCm => WalkDistanceCm + SprintDistanceCm + CrouchDistanceCm + FlyDistanceCm + SwimDistanceCm;

    // Stat breakdowns
    public Dictionary<string, long> KilledMobs { get; set; } = [];
    public Dictionary<string, long> KilledByMobs { get; set; } = [];
    public Dictionary<string, long> BlocksMined { get; set; } = [];
    public Dictionary<string, long> ItemsUsed { get; set; } = [];
    public Dictionary<string, long> ItemsPickedUp { get; set; } = [];

    // CoreProtect
    public long BlocksPlaced { get; set; }
    public long BlocksBroken { get; set; }
    public long SessionCount { get; set; }
    public long ChatMessages { get; set; }

    // Advancements
    public int AdvancementsCompleted { get; set; }
    public List<string> Advancements { get; set; } = [];

    // GriefPrevention
    public long ClaimBlocksAccrued { get; set; }

    // Formatted helpers
    public string PlayTimeFormatted => FormatTicks(PlayTimeTicks);
    public string TotalDistanceFormatted => FormatDistance(TotalDistanceCm);
    public string WalkDistanceFormatted => FormatDistance(WalkDistanceCm);
    public string SprintDistanceFormatted => FormatDistance(SprintDistanceCm);
    public string FlyDistanceFormatted => FormatDistance(FlyDistanceCm);
    public string SwimDistanceFormatted => FormatDistance(SwimDistanceCm);

    private static string FormatTicks(long ticks)
    {
        var ts = TimeSpan.FromMilliseconds(ticks * 50.0);
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m";
        return "< 1m";
    }

    private static string FormatDistance(long cm)
    {
        if (cm < 100) return $"{cm} cm";
        var blocks = cm / 100.0;
        if (blocks < 1000) return $"{blocks:F0} blocks";
        var km = blocks / 1000.0;
        return $"{km:F1} km ({blocks:F0} blocks)";
    }
}

public class WorldStats
{
    public int TotalPlayers { get; set; }
    public long TotalPlayTimeTicks { get; set; }
    public long TotalDeaths { get; set; }
    public long TotalMobKills { get; set; }
    public long TotalJumps { get; set; }
    public long TotalDistanceCm { get; set; }
    public long TotalBlocksPlaced { get; set; }
    public long TotalBlocksBroken { get; set; }
    public long TotalChatMessages { get; set; }
    public long TotalSessions { get; set; }
    public int PluginCount { get; set; }

    public string TotalPlayTimeFormatted
    {
        get
        {
            var ts = TimeSpan.FromMilliseconds(TotalPlayTimeTicks * 50.0);
            if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h";
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
            return $"{(int)ts.TotalMinutes}m";
        }
    }

    public string TotalDistanceFormatted
    {
        get
        {
            var blocks = TotalDistanceCm / 100.0;
            if (blocks < 1000) return $"{blocks:F0} blocks";
            return $"{blocks / 1000.0:F1} km";
        }
    }
}
