using SpawnDev.Rcon;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// SpawnDev.Rcon's banlist parsing, on the exact text Paper 1.21.5 returns over RCON (captured from a real
/// server in the live tests): entries are concatenated with no separator.
/// </summary>
public class RconParsingTests
{
    [Test]
    public void BanList_SplitsConcatenatedEntries()
    {
        const string response = "There are 2 ban(s):LateGriefer was banned by Rcon: Banned by an operator.Griefer was banned by Rcon: test ban";
        Assert.That(MinecraftRconClient.ParseBanList(response), Is.EqualTo(new[] { "LateGriefer", "Griefer" }));
    }

    [Test]
    public void BanList_KeepsFloodgateBedrockNames()
    {
        const string response = "There are 2 ban(s):Steve was banned by Rcon: Banned by an operator..Bedrock_Kid was banned by Server: spam";
        Assert.That(MinecraftRconClient.ParseBanList(response), Is.EqualTo(new[] { "Steve", ".Bedrock_Kid" }));
    }

    [Test]
    public void BanList_Empty()
    {
        Assert.That(MinecraftRconClient.ParseBanList("There are no bans"), Is.Empty);
    }

    [Test]
    public void BanList_AReasonEndingInALetter_GluesItsLastWordOntoTheNextName_AsDocumented()
    {
        // The documented limit: reason "griefing" + name "Alex" arrive as "griefingAlex", indistinguishable
        // from a player really named that. The panel reads banned-players.json instead.
        const string response = "There are 2 ban(s):Steve was banned by Rcon: griefingAlex was banned by Rcon: x";
        Assert.That(MinecraftRconClient.ParseBanList(response), Is.EqualTo(new[] { "Steve", "griefingAlex" }));
    }
}
