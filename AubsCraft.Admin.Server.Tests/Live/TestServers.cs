using SpawnDev.Rcon;
using AubsCraft.Admin.Server.Tests;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// Starts two REAL Paper servers once for the whole test run. They are deliberately different so a test can
/// tell which one answered: Alpha is a flat world capped at 7 players, Bravo a normal world capped at 9
/// whose overworld folder is renamed through server.properties level-name.
/// </summary>
[SetUpFixture]
public class TestServers
{
    public static PaperServer Alpha { get; private set; } = null!;
    public static PaperServer Bravo { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        var timeout = TimeSpan.FromMinutes(6);
        var alpha = PaperServer.StartAsync("alpha", 25601, 25611, 7, "minecraft:flat", "world", timeout);
        var bravo = PaperServer.StartAsync("bravo", 25602, 25612, 9, "minecraft:normal", "bravo_world", timeout);
        Alpha = await alpha;
        Bravo = await bravo;
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        if (Alpha != null) await Alpha.DisposeAsync();
        if (Bravo != null) await Bravo.DisposeAsync();
    }

    /// <summary>A direct RCON connection to one test server, for checking state independently of the code under test.</summary>
    public static async Task<MinecraftRconClient> RconAsync(PaperServer server)
    {
        var client = new MinecraftRconClient("127.0.0.1", server.RconPort);
        if (!await client.ConnectAsync(server.RconPassword))
            throw new InvalidOperationException($"RCON auth to {server.Name} failed");
        return client;
    }

    /// <summary>Empties a server's whitelist and ban list (tests share the servers). Names come from the exact JSON files.</summary>
    public static async Task ResetListsAsync(PaperServer server)
    {
        await using var rcon = await RconAsync(server);
        foreach (var name in Names(server, "whitelist.json"))
            await rcon.WhitelistRemoveAsync(name);
        foreach (var name in Names(server, "banned-players.json"))
            await rcon.PardonAsync(name);
    }

    /// <summary>The "name" of every entry in one of the server's list files (whitelist.json, banned-players.json).</summary>
    public static List<string> Names(PaperServer server, string file)
    {
        var path = Path.Combine(server.Dir, file);
        if (!File.Exists(path)) return [];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var doc = System.Text.Json.JsonDocument.Parse(fs);
        return doc.RootElement.EnumerateArray().Select(e => e.GetProperty("name").GetString()!).ToList();
    }
}
