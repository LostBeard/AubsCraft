using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AubsCraft.Admin.Server.Models;
using SpawnDev.Rcon;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Builds a working portal to another server near a world's spawn, without anyone in game:
///   1. a small datapack (aubscraft-portals) gives two block tags - what counts as a clear spot and as natural ground -
///      and, once portals exist, the ARRIVAL FIX below;
///   2. searches outward from spawn (live, over RCON) for a spot where the frame and a walk-in row on each side are
///      clear and stand on natural ground - it never replaces a built block;
///   3. builds an obsidian frame with a lit portal;
///   4. registers it with the server's portal add-on: Advanced Portals (Paper; a portal file + "portal reload") or
///      ProxyPortal (Fabric; "portal create").
///
/// The arrival fix: a player comes back to a server where they left it - inside the portal they left through - and
/// ProxyPortal sends anyone it finds inside a portal at once (Advanced Portals after 5 s), so they would bounce straight
/// back. The datapack's tick function moves a player who just came back and stands in a portal to the spot in front of
/// it. Functions run each tick before players are ticked, so this happens before the portal add-on looks.
/// </summary>
public class SpawnPortalService
{
    public const string DatapackName = "aubscraft-portals";
    private readonly ServerRegistry _registry;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<SpawnPortalService> _logger;

    /// <summary>Portals are at least this far from spawn: new players appear at spawn and must not land in one.</summary>
    public int MinDistance { get; set; } = 6;
    public int MaxDistance { get; set; } = 40;

    public SpawnPortalService(ServerRegistry registry, ILoggerFactory loggers)
    {
        _registry = registry;
        _loggers = loggers;
        _logger = loggers.CreateLogger<SpawnPortalService>();
    }

    private ServerDefinition Get(string id) => _registry.Get(id) ?? throw new InvalidOperationException($"No server '{id}'.");

    /// <summary>Which portal add-on a server has, or null.</summary>
    public static string? PortalAddon(ServerDefinition def)
    {
        static bool Has(string dir, string prefix) =>
            Directory.Exists(dir) && Directory.GetFiles(dir, "*.jar").Any(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return def.Loader switch
        {
            ServerLoader.Paper when Has(Path.Combine(def.Path, "plugins"), "advanced-portals") => "advanced-portals",
            ServerLoader.Fabric or ServerLoader.Vanilla when Has(Path.Combine(def.Path, "mods"), "ProxyPortal") => "proxyportal",
            _ => null,
        };
    }

    public static string PortalName(ServerDefinition target) => "aubs-" + target.Id;

    public async Task<PortalDefinition> BuildAtSpawnAsync(string serverId, string targetId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var def = Get(serverId);
        var target = Get(targetId);
        if (def.Id == target.Id) throw new InvalidOperationException("A portal has to lead to a different server.");
        var name = PortalName(target);
        if (def.Portals.FirstOrDefault(p => p.Name == name) is { } existing)
            throw new InvalidOperationException($"{def.Name} already has a portal to {target.Name} at {existing.X} {existing.Y} {existing.Z}.");
        var addon = PortalAddon(def) ?? throw new InvalidOperationException(def.Loader == ServerLoader.Paper
            ? $"{def.Name} needs the Advanced Portals plugin first (Plugins page)."
            : $"{def.Name} needs the ProxyPortal mod first (Mods page).");
        var spawn = new WorldDataService(def, _loggers.CreateLogger<WorldDataService>()).GetSpawn();
        if (!spawn.Known) throw new InvalidOperationException($"Could not read where {def.Name}'s spawn is.");

        await using var rcon = new MinecraftRconClient(def.RconHost, def.RconPort);
        if (!await rcon.ConnectAsync(def.RconPassword, ct)) throw new InvalidOperationException($"{def.Name} is not running (RCON did not answer).");

        progress?.Report("Installing the portal datapack");
        WriteDatapack(def, def.Portals);
        await ApplyDatapackAsync(rcon, def, ct);

        progress?.Report($"Looking for a clear spot {MinDistance}-{MaxDistance} blocks from spawn ({spawn.X}, {spawn.Z})");
        var spot = await WithChunksLoadedAsync(rcon, spawn.X - MaxDistance - 5, spawn.Z - MaxDistance - 5, spawn.X + MaxDistance + 5, spawn.Z + MaxDistance + 5,
            () => FindSpotAsync(rcon, spawn.X, spawn.Z, progress, ct), ct)
            ?? throw new InvalidOperationException($"No clear spot on natural ground within {MaxDistance} blocks of spawn. Build this one by hand.");

        var portal = Place(spot, name, target.Id, spawn.X, spawn.Z);
        progress?.Report($"Building the portal at {portal.X} {portal.Y} {portal.Z}");
        await WithChunksLoadedAsync(rcon, portal.X - 2, portal.Z - 2, portal.X + 5, portal.Z + 5, async () =>
        {
            foreach (var command in BuildCommands(portal)) await rcon.SendCommandAsync(command, ct);
            var lit = await rcon.SendCommandAsync($"execute if block {Inside(portal)} minecraft:nether_portal", ct);
            if (!lit.Contains("Test passed")) throw new InvalidOperationException("The portal did not light: " + lit);
            return true;
        }, ct);

        progress?.Report($"Connecting it to {target.Name}");
        if (addon == "advanced-portals")
        {
            var dir = Path.Combine(def.Path, "plugins", "AdvancedPortals", "portals");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name + ".yaml"), AdvancedPortalsYaml(portal, Path.GetFileName(def.WorldPath)));
            await rcon.SendCommandAsync("portal reload", ct);
        }
        else
        {
            var (a, b) = InteriorCorners(portal);
            var r = await rcon.SendCommandAsync($"portal create {name} {a.x} {a.y} {a.z} {b.x} {b.y} {b.z} {target.Id}", ct);
            if (!r.Contains("Successfully created")) throw new InvalidOperationException("ProxyPortal did not take the portal: " + r);
        }

        def.Portals.Add(portal);
        _registry.Update(def);
        WriteDatapack(def, def.Portals);
        await ApplyDatapackAsync(rcon, def, ct);
        _logger.LogWarning("Built a portal on {Server} to {Target} at {X} {Y} {Z}", def.Id, target.Id, portal.X, portal.Y, portal.Z);
        progress?.Report($"Portal to {target.Name} is ready at {portal.X} {portal.Y} {portal.Z}");
        return portal;
    }

    // -- the search --

    /// <summary>A frame origin: the frame's lowest, smallest corner; Axis 'x' = the frame runs along X (walk in along Z).</summary>
    public record Spot(int X, int Y, int Z, char Axis);

    private async Task<Spot?> FindSpotAsync(MinecraftRconClient rcon, int spawnX, int spawnZ, IProgress<string>? progress, CancellationToken ct)
    {
        var tried = 0;
        foreach (var (cx, cz) in Rings(spawnX, spawnZ))
        {
            ct.ThrowIfCancellationRequested();
            var surface = await SurfaceAsync(rcon, cx, cz, ct);
            if (surface == null) continue;
            foreach (var axis in new[] { 'x', 'z' })
            {
                // The frame's centre column is (cx, cz).
                var spot = axis == 'x' ? new Spot(cx - 1, surface.Value, cz, 'x') : new Spot(cx, surface.Value, cz - 1, 'z');
                if (await IsClearAsync(rcon, spot, ct)) return spot;
            }
            if (++tried % 50 == 0) progress?.Report($"  checked {tried} spots so far");
        }
        return null;
    }

    /// <summary>Frame centres from MinDistance outward, nearest first, every 2 blocks.</summary>
    private IEnumerable<(int x, int z)> Rings(int sx, int sz)
    {
        var points = new List<(int x, int z, double d)>();
        for (var dx = -MaxDistance; dx <= MaxDistance; dx += 2)
            for (var dz = -MaxDistance; dz <= MaxDistance; dz += 2)
            {
                var d = Math.Sqrt(dx * dx + dz * dz);
                if (d >= MinDistance && d <= MaxDistance) points.Add((sx + dx, sz + dz, d));
            }
        return points.OrderBy(p => p.d).Select(p => (p.x, p.z));
    }

    /// <summary>The height a block placed on the ground at (x, z) would sit at (the surface, ignoring leaves).</summary>
    private static async Task<int?> SurfaceAsync(MinecraftRconClient rcon, int x, int z, CancellationToken ct)
    {
        await rcon.SendCommandAsync($"execute positioned {x} 0 {z} positioned over motion_blocking_no_leaves run summon minecraft:marker ~ ~ ~ {{Tags:[\"aubs_probe\"]}}", ct);
        try
        {
            var r = await rcon.SendCommandAsync("data get entity @e[type=minecraft:marker,tag=aubs_probe,limit=1] Pos[1]", ct);
            var m = Regex.Match(r, @"(-?\d+(?:\.\d+)?)d");
            return m.Success ? (int)Math.Floor(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) : null;
        }
        finally { await rcon.SendCommandAsync("kill @e[type=minecraft:marker,tag=aubs_probe]", ct); }
    }

    /// <summary>
    /// The frame and a walk-in row on each side are clear (#aubscraft:portal_clear: air and plants), 5 high, on natural
    /// ground (#aubscraft:portal_floor) - checked as a few long "execute if block ..." commands (RCON takes ~1400 bytes).
    /// </summary>
    private static async Task<bool> IsClearAsync(MinecraftRconClient rcon, Spot s, CancellationToken ct)
    {
        var conditions = new List<string>();
        foreach (var (dx, dz) in Footprint(s.Axis)) conditions.Add($"if block ~{dx} ~-1 ~{dz} #aubscraft:portal_floor");
        for (var dy = 0; dy <= 4; dy++)
            foreach (var (dx, dz) in Footprint(s.Axis)) conditions.Add($"if block ~{dx} ~{dy} ~{dz} #aubscraft:portal_clear");
        var prefix = $"execute positioned {s.X} {s.Y} {s.Z} ";
        var command = new StringBuilder(prefix);
        foreach (var c in conditions)
        {
            if (command.Length + c.Length + 1 > 1300)
            {
                if (!(await rcon.SendCommandAsync(command.ToString().TrimEnd(), ct)).Contains("Test passed")) return false;
                command.Clear().Append(prefix);
            }
            command.Append(c).Append(' ');
        }
        return (await rcon.SendCommandAsync(command.ToString().TrimEnd(), ct)).Contains("Test passed");
    }

    /// <summary>The 4 x 3 columns: the frame line plus one row in front and one behind.</summary>
    private static IEnumerable<(int dx, int dz)> Footprint(char axis)
    {
        for (var along = 0; along <= 3; along++)
            for (var across = -1; across <= 1; across++)
                yield return axis == 'x' ? (along, across) : (across, along);
    }

    // -- the portal --

    /// <summary>Picks the walk-in side nearer spawn as where returning players are put, facing away from the portal.</summary>
    public static PortalDefinition Place(Spot s, string name, string target, int spawnX, int spawnZ)
    {
        if (s.Axis == 'x')
        {
            var front = Dist(s.X + 2, s.Z - 1, spawnX, spawnZ) <= Dist(s.X + 2, s.Z + 1, spawnX, spawnZ) ? -1 : 1;
            return new PortalDefinition(name, target, s.X, s.Y, s.Z, "x", s.X + 2.0, s.Y, s.Z + front + 0.5, front < 0 ? 180 : 0);
        }
        else
        {
            var front = Dist(s.X - 1, s.Z + 2, spawnX, spawnZ) <= Dist(s.X + 1, s.Z + 2, spawnX, spawnZ) ? -1 : 1;
            return new PortalDefinition(name, target, s.X, s.Y, s.Z, "z", s.X + front + 0.5, s.Y, s.Z + 2.0, front < 0 ? 90 : -90);
        }
    }

    private static double Dist(int x, int z, int sx, int sz) => Math.Sqrt((double)(x - sx) * (x - sx) + (double)(z - sz) * (z - sz));

    public static IEnumerable<string> BuildCommands(PortalDefinition p) => p.Axis == "x"
        ? [$"fill {p.X} {p.Y} {p.Z} {p.X + 3} {p.Y + 4} {p.Z} minecraft:obsidian",
           $"fill {p.X + 1} {p.Y + 1} {p.Z} {p.X + 2} {p.Y + 3} {p.Z} minecraft:nether_portal[axis=x]"]
        : [$"fill {p.X} {p.Y} {p.Z} {p.X} {p.Y + 4} {p.Z + 3} minecraft:obsidian",
           $"fill {p.X} {p.Y + 1} {p.Z + 1} {p.X} {p.Y + 3} {p.Z + 2} minecraft:nether_portal[axis=z]"];

    /// <summary>A block inside the portal (for checks and tests).</summary>
    public static string Inside(PortalDefinition p) => p.Axis == "x" ? $"{p.X + 1} {p.Y + 1} {p.Z}" : $"{p.X} {p.Y + 1} {p.Z + 1}";

    /// <summary>The lit 2 x 3 inside of the frame, as block corners (ProxyPortal compares block positions, inclusive).</summary>
    public static ((int x, int y, int z) a, (int x, int y, int z) b) InteriorCorners(PortalDefinition p) => p.Axis == "x"
        ? ((p.X + 1, p.Y + 1, p.Z), (p.X + 2, p.Y + 3, p.Z))
        : ((p.X, p.Y + 1, p.Z + 1), (p.X, p.Y + 3, p.Z + 2));

    private static string AdvancedPortalsYaml(PortalDefinition p, string worldName)
    {
        var (maxX, maxZ) = p.Axis == "x" ? (p.X + 3, p.Z) : (p.X, p.Z + 3);
        return $"""
            maxLoc:
              posX: {maxX}
              posY: {p.Y + 4}
              posZ: {maxZ}
              worldName: {worldName}
            minLoc:
              posX: {p.X}
              posY: {p.Y}
              posZ: {p.Z}
              worldName: {worldName}
            args:
              name:
              - {p.Name}
              bungee:
              - {p.Target}
              triggerblock:
              - NETHER_PORTAL

            """;
    }

    // -- the datapack --

    public static string DatapackPath(ServerDefinition def) => Path.Combine(def.WorldPath, "datapacks", DatapackName);

    /// <summary>Writes the datapack: block tags for the search, and the arrival fix for every portal on this server.</summary>
    public static void WriteDatapack(ServerDefinition def, IEnumerable<PortalDefinition> portals)
    {
        var root = DatapackPath(def);
        void Write(string relative, string text)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        // 71 = 1.21.5; the range keeps it loading on nearby versions.
        Write("pack.mcmeta", """{"pack":{"pack_format":71,"supported_formats":{"min_inclusive":48,"max_inclusive":99},"description":"AubsCraft portals (written by the panel)"}}""");
        Write("data/aubscraft/tags/block/portal_clear.json", """
            {"values":["minecraft:air","minecraft:cave_air","minecraft:short_grass","minecraft:tall_grass","minecraft:fern",
            "minecraft:large_fern","minecraft:snow","minecraft:dead_bush","#minecraft:small_flowers",
            {"id":"minecraft:bush","required":false},{"id":"minecraft:short_dry_grass","required":false},
            {"id":"minecraft:tall_dry_grass","required":false},{"id":"minecraft:leaf_litter","required":false},
            {"id":"minecraft:firefly_bush","required":false}]}
            """);
        Write("data/aubscraft/tags/block/portal_floor.json", """
            {"values":["#minecraft:dirt","#minecraft:sand","#minecraft:base_stone_overworld","#minecraft:terracotta",
            "minecraft:gravel","minecraft:snow_block","minecraft:dirt_path","minecraft:clay","minecraft:sandstone","minecraft:red_sandstone"]}
            """);
        Write("data/minecraft/tags/function/load.json", """{"values":["aubscraft:load"]}""");
        Write("data/minecraft/tags/function/tick.json", """{"values":["aubscraft:tick"]}""");
        Write("data/aubscraft/function/load.mcfunction",
            "# Counts leaving the game: a score above 0 means this player has just come back.\n" +
            "scoreboard objectives add aubs_left minecraft.custom:minecraft.leave_game\n");
        var tick = new StringBuilder("# A player who just came back and stands in a portal is moved in front of it (no bounce back).\n");
        foreach (var p in portals)
        {
            var (dx, dz) = p.Axis == "x" ? (3, 0) : (0, 3);
            tick.Append(CultureInfo.InvariantCulture,
                $"execute in minecraft:overworld as @a[scores={{aubs_left=1..}},x={p.X},y={p.Y},z={p.Z},dx={dx},dy=4,dz={dz}] run tp @s {p.FrontX:0.0#} {p.FrontY} {p.FrontZ:0.0#} {p.Yaw} 0\n");
        }
        tick.Append("scoreboard players set @a[scores={aubs_left=1..}] aubs_left 0\n");
        Write("data/aubscraft/function/tick.mcfunction", tick.ToString());
    }

    /// <summary>
    /// Reloads data packs and makes sure ours is enabled. Paper needs "minecraft:reload" (its bare "reload" reloads
    /// PLUGINS); vanilla and Fabric only know "reload" (no "minecraft:" alias - the reload silently never happens).
    /// </summary>
    private static async Task ApplyDatapackAsync(MinecraftRconClient rcon, ServerDefinition def, CancellationToken ct)
    {
        await rcon.SendCommandAsync(def.Loader == ServerLoader.Paper ? "minecraft:reload" : "reload", ct);
        await rcon.SendCommandAsync($"datapack enable \"file/{DatapackName}\"", ct);
        var enabled = await rcon.SendCommandAsync("datapack list enabled", ct);
        if (!enabled.Contains(DatapackName)) throw new InvalidOperationException("The portal datapack did not load: " + enabled);
    }

    /// <summary>
    /// Force-loads the chunks of an area while <paramref name="work"/> runs (block checks need loaded chunks), then
    /// un-force-loads only the chunks it added - chunks someone force-loaded before stay as they were.
    /// </summary>
    private static async Task<T> WithChunksLoadedAsync<T>(MinecraftRconClient rcon, int x1, int z1, int x2, int z2, Func<Task<T>> work, CancellationToken ct)
    {
        var before = new HashSet<(int, int)>();
        foreach (Match m in Regex.Matches(await rcon.SendCommandAsync("forceload query", ct), @"\[(-?\d+), (-?\d+)\]"))
            before.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
        await rcon.SendCommandAsync($"forceload add {x1} {z1} {x2} {z2}", ct);
        try
        {
            var end = DateTime.UtcNow.AddSeconds(60);
            foreach (var (x, z) in new[] { (x1, z1), (x2, z1), (x1, z2), (x2, z2) })
                while (!(await rcon.SendCommandAsync($"execute if loaded {x} 0 {z}", ct)).Contains("Test passed"))
                {
                    if (DateTime.UtcNow > end) throw new TimeoutException("The area around spawn did not load.");
                    await Task.Delay(250, ct);
                }
            return await work();
        }
        finally
        {
            for (var cx = Math.Floor(x1 / 16.0); cx <= Math.Floor(x2 / 16.0); cx++)
                for (var cz = Math.Floor(z1 / 16.0); cz <= Math.Floor(z2 / 16.0); cz++)
                    if (!before.Contains(((int)cx, (int)cz)))
                        await rcon.SendCommandAsync($"forceload remove {(int)cx * 16} {(int)cz * 16}", CancellationToken.None);
        }
    }
}
