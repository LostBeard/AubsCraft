using AubsCraft.Admin.Server.Services;
using AubsCraft.Live.Model.BlockEntities;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>What a sign says: the words out of the game's text components (JSON and NBT), the sign's block entity, and the header that carries it.</summary>
public class SignTextTests
{
    private static NbtList Strings(params string[] items)
    {
        var list = new NbtList(NbtTagType.String, items.Length);
        foreach (var s in items) list.Add(s);
        return list;
    }

    [Test]
    public void JsonTexts_AreTheirWords()
    {
        Assert.That(TextComponent.Plain("\"Hello\""), Is.EqualTo("Hello"));
        Assert.That(TextComponent.Plain("{\"text\":\"Shop\"}"), Is.EqualTo("Shop"));
        Assert.That(TextComponent.Plain("{\"text\":\"\",\"extra\":[\"Buy \",{\"text\":\"food\",\"color\":\"red\"}]}"), Is.EqualTo("Buy food"));
        Assert.That(TextComponent.Plain("[\"a\",\"b\"]"), Is.EqualTo("ab"));
        Assert.That(TextComponent.Plain("\"\""), Is.EqualTo(""));
        Assert.That(TextComponent.Plain("{\"text\":\"caf\u00e9\"}"), Is.EqualTo("café"));
    }

    [Test]
    public void PlainWordsAndNbtTexts_AreTheirWordsToo()
    {
        Assert.That(TextComponent.Plain("just words"), Is.EqualTo("just words"));
        Assert.That(TextComponent.Plain("{broken json"), Is.EqualTo("{broken json"), "what is not JSON is left as it is");
        Assert.That(TextComponent.Plain(new NbtCompound { ["text"] = "Inn" }), Is.EqualTo("Inn"));
        var extra = new NbtList(NbtTagType.Compound, 1);
        extra.Add(new NbtCompound { ["text"] = "two" });
        Assert.That(TextComponent.Plain(new NbtCompound { ["text"] = "one ", ["extra"] = extra }), Is.EqualTo("one two"));
        Assert.That(TextComponent.Plain(null), Is.EqualTo(""));
    }

    private static NbtCompound Sign(string id, int x, int y, int z, string color, bool glow, params string[] lines) => new()
    {
        ["id"] = id, ["x"] = x, ["y"] = y, ["z"] = z,
        ["front_text"] = new NbtCompound { ["messages"] = Strings(lines), ["color"] = color, ["has_glowing_text"] = (byte)(glow ? 1 : 0) },
        ["back_text"] = new NbtCompound { ["messages"] = Strings("\"\"", "\"\"", "\"\"", "\"\""), ["color"] = "black", ["has_glowing_text"] = (byte)0 },
    };

    private static NbtCompound Chunk(params NbtCompound[] entities)
    {
        var list = new NbtList(NbtTagType.Compound, entities.Length);
        foreach (var e in entities) list.Add(e);
        return new NbtCompound { ["block_entities"] = list };
    }

    [Test]
    public void TheSignsOfAChunk_AreReadWithTheirPlaceDyeAndGlow_AndBlankOnesAreSkipped()
    {
        var chunk = Chunk(
            Sign("minecraft:sign", 35, 70, -13, "red", true, "\"Welcome\"", "\"to\"", "\"town\"", "\"\""),
            Sign("minecraft:hanging_sign", 20, 64, 0, "black", false, "\"Door\"", "\"\"", "\"\"", "\"\""),
            Sign("minecraft:sign", 1, 1, 1, "black", false, "\"\"", "\"\"", "\"\"", "\"\""),
            new NbtCompound { ["id"] = "minecraft:chest", ["x"] = 0, ["y"] = 0, ["z"] = 0 });
        var signs = RegionReader.SignTexts(chunk)!;
        Assert.That(signs.Count, Is.EqualTo(2));
        Assert.That(signs[0], Is.EqualTo(signs[0] with { Lines = signs[0].Lines }));
        Assert.That((signs[0].X, signs[0].Y, signs[0].Z), Is.EqualTo((3, 70, 3)), "x and z are the column's own: 35 -> 3, -13 -> 3");
        Assert.That(signs[0].Lines, Is.EqualTo(new[] { "Welcome", "to", "town", "" }));
        Assert.That(signs[0].Color, Is.EqualTo(14), "red");
        Assert.That(signs[0].Glowing, Is.True);
        Assert.That(signs[1].Lines[0], Is.EqualTo("Door"));
        Assert.That(signs[1].Color, Is.EqualTo(15), "black");
        Assert.That(RegionReader.SignTexts(new NbtCompound()), Is.Null);
    }

    [Test]
    public void TheHeaderCarriesThem_AndALongOrMultiByteLineIsCutCleanly()
    {
        var lines = new[] { "Hello", "", "é" + new string('x', 70), new string('日', 30) };
        var signs = new List<SignText> { new(5, -60, 9, lines, 3, true), new(15, 300, 0, ["a", "b", "c", "d"], 15, false) };
        var back = SignTextCodec.Decode(SignTextCodec.Encode(signs));
        Assert.That(back.Count, Is.EqualTo(2));
        Assert.That((back[0].X, back[0].Y, back[0].Z, back[0].Color, back[0].Glowing), Is.EqualTo((5, -60, 9, (byte)3, true)));
        Assert.That(back[0].Lines[0], Is.EqualTo("Hello"));
        Assert.That(back[0].Lines[2].Length, Is.LessThanOrEqualTo(SignTextCodec.MaxLineBytes));
        Assert.That(back[0].Lines[2], Does.StartWith("éx"));
        Assert.That(System.Text.Encoding.UTF8.GetByteCount(back[0].Lines[3]), Is.LessThanOrEqualTo(SignTextCodec.MaxLineBytes));
        Assert.That(back[0].Lines[3], Is.All.EqualTo('日'), "never half a character");
        Assert.That(back[1].Y, Is.EqualTo(300));
        Assert.That(back[1].Lines, Is.EqualTo(new[] { "a", "b", "c", "d" }));
    }

    [Test]
    public void ABrokenHeader_GivesWhatCameBeforeItOrNothing()
    {
        Assert.That(SignTextCodec.Decode(null), Is.Empty);
        Assert.That(SignTextCodec.Decode("not base64!!"), Is.Empty);
        var one = SignTextCodec.Encode([new SignText(1, 2, 3, ["x", "", "", ""], 0, false)]);
        var bytes = Convert.FromBase64String(one);
        Assert.That(SignTextCodec.Decode(Convert.ToBase64String(bytes[..^2])), Is.Empty, "cut short");
    }

    [Test]
    public void TheColour_IsTheDyesTextColourDarkened_OrItsOwnWhenGlowing()
    {
        Assert.That(SignTextCodec.TextColour(15, false), Is.EqualTo(((byte)(0x1D * 4 / 10), (byte)(0x1D * 4 / 10), (byte)(0x21 * 4 / 10))), "black writing is nearly black");
        Assert.That(SignTextCodec.TextColour(15, true), Is.EqualTo(((byte)0xF0, (byte)0xEB, (byte)0xCC)), "glowing black is warm white");
        Assert.That(SignTextCodec.TextColour(14, true), Is.EqualTo(((byte)0xB0, (byte)0x2E, (byte)0x26)));
        Assert.That(SignTextCodec.TextColour(14, false).r, Is.EqualTo(0xB0 * 4 / 10));
    }

    [Test]
    public void TwoSignsThatSayTheSameShareAPicture()
    {
        var a = new SignText(1, 2, 3, ["x", "y", "", ""], 4, false);
        var b = new SignText(9, 9, 9, ["x", "y", "", ""], 4, false);
        Assert.That(a.Key, Is.EqualTo(b.Key));
        Assert.That(a.Key, Is.Not.EqualTo((b with { Glowing = true }).Key));
        Assert.That(a.Key, Is.Not.EqualTo((b with { Color = 5 }).Key));
    }
}
