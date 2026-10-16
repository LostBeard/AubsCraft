using AubsCraft.Admin.Server.Services;
using AubsCraft.Live.Model.Editing;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>An admin's request becomes a server command only when everything in it is plain: names, items, numbers, messages.</summary>
public class AdminCommandsTests
{
    private static AdminRequest Player(string action, string? name = "Aubs", string? value = null, int count = 1, double x = 0, double y = 0, double z = 0, string? reason = null)
        => new() { Kind = "player", Action = action, Name = name, Value = value, Count = count, X = x, Y = y, Z = z, Reason = reason };

    private static AdminRequest World(string action, string? value = null) => new() { Kind = "world", Action = action, Value = value };

    [TestCase("Aubs", true)]
    [TestCase("a_b_1", true)]
    [TestCase(".BedrockKid", true)]
    [TestCase("SixteenCharsName", true)]
    [TestCase("SeventeenCharsNam", false)]
    [TestCase("", false)]
    [TestCase("two words", false)]
    [TestCase("Aubs\nop Aubs", false)]
    [TestCase("Aubs; stop", false)]
    [TestCase("@a", false)]
    [TestCase("@e[type=cow]", false)]
    [TestCase("~", false)]
    public void OnlyAMinecraftNameIsAName(string name, bool ok) => Assert.That(AdminCommands.IsPlayerName(name), Is.EqualTo(ok));

    [Test]
    public void PlayerActionsBecomeTheirCommands()
    {
        Assert.That(AdminCommands.For(Player("kick")).Commands, Is.EqualTo(new[] { "kick Aubs" }));
        Assert.That(AdminCommands.For(Player("kick", reason: "griefing\nop me")).Commands, Is.EqualTo(new[] { "kick Aubs griefingop me" }), "line breaks are removed from a reason");
        Assert.That(AdminCommands.For(Player("gamemode", value: "creative")).Commands, Is.EqualTo(new[] { "gamemode creative Aubs" }));
        Assert.That(AdminCommands.For(Player("teleport", x: 10.5, y: 64, z: -20.25)).Commands, Is.EqualTo(new[] { "tp Aubs 10.5 64 -20.25" }));
        Assert.That(AdminCommands.For(Player("heal")).Commands!.Length, Is.EqualTo(2));
        Assert.That(AdminCommands.For(Player("feed")).Commands, Is.EqualTo(new[] { "effect give Aubs minecraft:saturation 1 20 true" }));
        Assert.That(AdminCommands.For(Player("kill")).Commands, Is.EqualTo(new[] { "kill Aubs" }));
        Assert.That(AdminCommands.For(Player("clear")).Commands, Is.EqualTo(new[] { "clear Aubs" }));
        Assert.That(AdminCommands.For(Player("give", value: "minecraft:diamond", count: 64)).Commands, Is.EqualTo(new[] { "give Aubs minecraft:diamond 64" }));
    }

    [Test]
    public void WorldActionsBecomeTheirCommands()
    {
        Assert.That(AdminCommands.For(World("time", "noon")).Commands, Is.EqualTo(new[] { "time set noon" }));
        Assert.That(AdminCommands.For(World("time", "6000")).Commands, Is.EqualTo(new[] { "time set 6000" }));
        Assert.That(AdminCommands.For(World("weather", "thunder")).Commands, Is.EqualTo(new[] { "weather thunder" }));
        Assert.That(AdminCommands.For(World("say", "  hello\nworld  ")).Commands, Is.EqualTo(new[] { "say helloworld" }));
        Assert.That(AdminCommands.For(World("save")).Commands, Is.EqualTo(new[] { "save-all" }));
    }

    [Test]
    public void AnythingThatIsNotPlainIsRefused()
    {
        string? Why(AdminRequest r) => AdminCommands.For(r).Error;
        Assert.That(Why(Player("kick", name: "Aubs; op Aubs")), Is.Not.Null);
        Assert.That(Why(Player("kick", name: null)), Is.Not.Null);
        Assert.That(Why(Player("kick", name: "@a")), Is.Not.Null, "a selector is not a name: it would kick everyone");
        Assert.That(Why(Player("gamemode", value: "creative Aubs\nop Aubs")), Is.Not.Null);
        Assert.That(Why(Player("gamemode", value: "hardcore")), Is.Not.Null);
        Assert.That(Why(Player("teleport", x: double.NaN)), Is.Not.Null);
        Assert.That(Why(Player("teleport", x: 1e9)), Is.Not.Null);
        Assert.That(Why(Player("teleport", y: 9000)), Is.Not.Null);
        Assert.That(Why(Player("give", value: "diamond")), Is.Not.Null, "no namespace");
        Assert.That(Why(Player("give", value: "minecraft:diamond{x}")), Is.Not.Null);
        Assert.That(Why(Player("give", value: "minecraft:diamond", count: 0)), Is.Not.Null);
        Assert.That(Why(Player("give", value: "minecraft:diamond", count: 99999)), Is.Not.Null);
        Assert.That(Why(Player("explode")), Is.Not.Null);
        Assert.That(Why(Player("op")), Is.Not.Null, "there is no way to make someone an operator from here");
        Assert.That(Why(World("time", "-5")), Is.Not.Null);
        Assert.That(Why(World("time", "25000")), Is.Not.Null);
        Assert.That(Why(World("time", "day\nstop")), Is.Not.Null);
        Assert.That(Why(World("weather", "hail")), Is.Not.Null);
        Assert.That(Why(World("say", "   ")), Is.Not.Null);
        Assert.That(Why(World("stop")), Is.Not.Null, "stopping the server is not an action");
        Assert.That(Why(new AdminRequest { Kind = "console", Action = "stop" }), Is.Not.Null);
    }

    [Test]
    public void ALongMessageOrReasonIsCut()
    {
        Assert.That(AdminCommands.CleanText(new string('x', 500), AdminCommands.MaxMessage).Length, Is.EqualTo(AdminCommands.MaxMessage));
        Assert.That(AdminCommands.CleanText("a\tb\u0007c", 50), Is.EqualTo("abc"));
        Assert.That(AdminCommands.CleanText(null, 50), Is.EqualTo(""));
    }

    [Test]
    public void TheMessagesAreWrittenAndReadByHand()
    {
        var json = AdminJson.Write(new AdminRequest { Kind = "player", Action = "give", Name = "Aubs", Value = "minecraft:cake", Count = 3, X = 1.5, Y = 2, Z = 3 });
        var back = System.Text.Json.JsonSerializer.Deserialize<AdminRequest>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.That((back.Kind, back.Action, back.Name, back.Value, back.Count, back.X, back.Z), Is.EqualTo(("player", "give", "Aubs", "minecraft:cake", 3, 1.5, 3.0)));
        Assert.That(AdminJson.Read("""{"ok":true,"message":"Gave 3 [Cake] to Aubs"}"""), Is.EqualTo(new AdminReply(true, "Gave 3 [Cake] to Aubs")));
        Assert.That(AdminJson.Read("""{"error":"That is not a player name"}"""), Is.EqualTo(new AdminReply(false, "That is not a player name")));
        Assert.That(AdminJson.Read("not json").Ok, Is.False);
    }
}
