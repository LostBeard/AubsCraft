using System.Text;
using AubsCraft.Admin.Server.Services;
using AubsCraft.Live.Model.Editing;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>An admin's edit request becomes the plugin's command (and is refused when it is not sane), and the channel between the admin's request and the plugin's long poll.</summary>
public class EditChannelTests
{
    private static EditOpDto Set(int x, int y, int z, string block) => new() { Op = "set", At = [x, y, z], Block = block };
    private static EditOpDto Fill(int[] a, int[] b, string block, string? only = null, bool hollow = false) => new() { Op = "fill", From = a, To = b, Block = block, Only = only, Hollow = hollow };
    private static EditRequest Request(params EditOpDto[] ops) => new() { Label = "test", Ops = [.. ops] };

    // The plugin's reader, written out again over the bytes: u8 kind, strings, palette, ops
    private sealed class Reader(byte[] b)
    {
        public int At;
        public int U8() => b[At++];
        public int U16() { int v = b[At] | (b[At + 1] << 8); At += 2; return v; }
        public int I32() { int v = BitConverter.ToInt32(b, At); At += 4; return v; }
        public string Str() { int n = U16(); var s = Encoding.UTF8.GetString(b, At, n); At += n; return s; }
    }

    [Test]
    public void ASetAndAFill_BecomeTheCommandsLayout_WithOnePalette()
    {
        var payload = EditCodec.Build("cmd1", "alice", Request(Set(1, -60, 3, "minecraft:stone"), Fill([0, -60, 0], [3, -58, 3], "minecraft:stone", only: "minecraft:air", hollow: true)), out var blocks);
        Assert.That(blocks, Is.EqualTo(1 + 4 * 3 * 4));
        var r = new Reader(payload);
        Assert.That(r.U8(), Is.EqualTo(1), "an edit");
        Assert.That(r.Str(), Is.EqualTo("cmd1"));
        Assert.That(r.Str(), Is.EqualTo("alice"));
        Assert.That(r.Str(), Is.EqualTo("test"));
        Assert.That(r.U16(), Is.EqualTo(2), "stone is in the palette once, then air");
        Assert.That(r.Str(), Is.EqualTo("minecraft:stone"));
        Assert.That(r.Str(), Is.EqualTo("minecraft:air"));
        Assert.That(r.U16(), Is.EqualTo(2), "two steps");
        Assert.That(r.U8(), Is.EqualTo(1));
        Assert.That((r.I32(), r.I32(), r.I32(), r.U16()), Is.EqualTo((1, -60, 3, 0)));
        Assert.That(r.U8(), Is.EqualTo(2));
        Assert.That(new[] { r.I32(), r.I32(), r.I32(), r.I32(), r.I32(), r.I32() }, Is.EqualTo(new[] { 0, -60, 0, 3, -58, 3 }));
        Assert.That(r.U16(), Is.EqualTo(0), "the fill's block");
        Assert.That(r.I32(), Is.EqualTo(1), "only where it is air");
        Assert.That(r.U8(), Is.EqualTo(1), "hollow");
        Assert.That(r.At, Is.EqualTo(payload.Length), "nothing left over");
    }

    [Test]
    public void ASparseListOfBlocks_HasItsPaletteMappedIntoTheEdits()
    {
        // Two records: (0,0,0) palette 1 and (2,-1,5) palette 0, with the request's own palette [dirt, stone]
        var data = new byte[16];
        BitConverter.TryWriteBytes(data.AsSpan(6), (ushort)1);
        BitConverter.TryWriteBytes(data.AsSpan(8), (short)2); BitConverter.TryWriteBytes(data.AsSpan(10), (short)-1); BitConverter.TryWriteBytes(data.AsSpan(12), (short)5);
        BitConverter.TryWriteBytes(data.AsSpan(14), (ushort)0);
        var op = new EditOpDto { Op = "blocks", At = [10, 0, 10], Palette = ["minecraft:dirt", "minecraft:stone"], Data = Convert.ToBase64String(data) };
        var payload = EditCodec.Build("c", "e", Request(Set(0, 0, 0, "minecraft:stone"), op), out var blocks);
        Assert.That(blocks, Is.EqualTo(3));
        var r = new Reader(payload);
        r.U8(); r.Str(); r.Str(); r.Str();
        Assert.That(r.U16(), Is.EqualTo(2));
        Assert.That(r.Str(), Is.EqualTo("minecraft:stone"));
        Assert.That(r.Str(), Is.EqualTo("minecraft:dirt"));
        r.U16(); r.U8(); r.I32(); r.I32(); r.I32(); r.U16();
        Assert.That(r.U8(), Is.EqualTo(3));
        Assert.That((r.I32(), r.I32(), r.I32()), Is.EqualTo((10, 0, 10)));
        Assert.That(r.I32(), Is.EqualTo(16));
        var raw = payload[r.At..];
        Assert.That(BitConverter.ToUInt16(raw, 6), Is.EqualTo(0), "the request palette 1 (stone) is the edit palette 0");
        Assert.That(BitConverter.ToUInt16(raw, 14), Is.EqualTo(1), "the request palette 0 (dirt) is the edit palette 1");
    }

    [TestCase("minecraft:stone", true)]
    [TestCase("minecraft:oak_stairs[facing=north,half=bottom]", true)]
    [TestCase("mod_x:some/thing", true)]
    [TestCase("stone", false)]
    [TestCase("minecraft:stone[", false)]
    [TestCase("minecraft:Stone", false)]
    [TestCase("minecraft:stone\nsay hi", false)]
    [TestCase("minecraft:stone; op me", false)]
    [TestCase("minecraft:chest{Items:[]}", false)]
    [TestCase("", false)]
    public void OnlyPlainBlockDataIsAccepted(string block, bool ok) => Assert.That(EditCodec.IsBlock(block), Is.EqualTo(ok));

    [Test]
    public void RequestsThatAreNotSane_AreRefusedWithAReason()
    {
        void Refused(EditRequest r, string why) => Assert.That(() => EditCodec.Build("c", "e", r, out _), Throws.TypeOf<ArgumentException>().With.Message.Contain(why), why);
        Refused(new EditRequest(), "nothing");
        Refused(Request(Set(0, 0, 0, "bad block")), "Not a block");
        Refused(Request(Set(0, 400, 0, "minecraft:stone")), "y must be");
        Refused(Request(Set(0, -65, 0, "minecraft:stone")), "y must be");
        Refused(Request(Set(40_000_000, 0, 0, "minecraft:stone")), "out of the world");
        Refused(Request(new EditOpDto { Op = "set", Block = "minecraft:stone" }), "three numbers");
        Refused(Request(new EditOpDto { Op = "explode" }), "Unknown step");
        Refused(Request(Fill([0, 0, 0], [200, 200, 200], "minecraft:stone"), Fill([0, 0, 0], [200, 100, 200], "minecraft:stone")), "blocks in one edit");
        Refused(Request(Fill([0, 0, 0], [0, 0, 0], "minecraft:stone;x")), "Not a block");
        Refused(new EditRequest { Ops = Enumerable.Range(0, 65).Select(i => Set(i, 0, 0, "minecraft:stone")).ToList() }, "At most");
        Refused(Request(new EditOpDto { Op = "blocks", At = [0, 0, 0], Palette = ["minecraft:stone"], Data = "!!!" }), "base64");
        Refused(Request(new EditOpDto { Op = "blocks", At = [0, 0, 0], Palette = ["minecraft:stone"], Data = Convert.ToBase64String(new byte[7]) }), "whole records");
        var oob = new byte[8]; BitConverter.TryWriteBytes(oob.AsSpan(2), (short)500);
        Refused(Request(new EditOpDto { Op = "blocks", At = [0, 0, 0], Palette = ["minecraft:stone"], Data = Convert.ToBase64String(oob) }), "outside the world");
        var badIndex = new byte[8]; BitConverter.TryWriteBytes(badIndex.AsSpan(6), (ushort)3);
        Refused(Request(new EditOpDto { Op = "blocks", At = [0, 0, 0], Palette = ["minecraft:stone"], Data = Convert.ToBase64String(badIndex) }), "palette index");
    }

    [Test]
    public void AnUndo_NamesTheEdit()
    {
        var r = new Reader(EditCodec.BuildUndo("c9", "edit-7"));
        Assert.That(r.U8(), Is.EqualTo(2));
        Assert.That(r.Str(), Is.EqualTo("c9"));
        Assert.That(r.Str(), Is.EqualTo("edit-7"));
    }

    // ---- the channel ----

    [Test]
    public async Task WithNoPluginPolling_AnEditSaysSo()
    {
        var channel = new EditChannelService();
        var result = await channel.SubmitAsync("s", "c", [1], TimeSpan.FromSeconds(1));
        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Does.Contain("not connected"));
    }

    [Test]
    public async Task ACommandReachesThePlugin_AndItsResultComesBack()
    {
        var channel = new EditChannelService();
        Assert.That(channel.PluginConnected("s"), Is.False);
        // The plugin asks; with nothing waiting it is told nothing after its wait
        var idle = await channel.PollAsync("s", TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.That(idle, Is.Null);
        Assert.That(channel.PluginConnected("s"), Is.True, "asking is what makes it connected");

        var polling = channel.PollAsync("s", TimeSpan.FromSeconds(5), CancellationToken.None);
        var submit = channel.SubmitAsync("s", "cmd-1", [9, 8, 7], TimeSpan.FromSeconds(5));
        var framed = (await polling)!;
        Assert.That(BitConverter.ToUInt16(framed, 0), Is.EqualTo(1));
        Assert.That(BitConverter.ToInt32(framed, 2), Is.EqualTo(3));
        Assert.That(framed[6..], Is.EqualTo(new byte[] { 9, 8, 7 }));
        Assert.That(submit.IsCompleted, Is.False, "the caller waits for the result");

        Assert.That(channel.Complete("s", """{"id":"cmd-1","ok":true,"edit":"cmd-1","placed":12,"skipped":3,"applied":true}"""), Is.True);
        var result = await submit;
        Assert.That(result, Is.EqualTo(new EditResult(true, "cmd-1", 12, 3, true, null)));
    }

    [Test]
    public async Task SeveralCommandsWaiting_AreHandedOverTogether_InOrder()
    {
        var channel = new EditChannelService();
        await channel.PollAsync("s", TimeSpan.FromMilliseconds(10), CancellationToken.None);   // connected
        var a = channel.SubmitAsync("s", "a", [1], TimeSpan.FromSeconds(5));
        var b = channel.SubmitAsync("s", "b", [2, 2], TimeSpan.FromSeconds(5));
        var framed = (await channel.PollAsync("s", TimeSpan.FromSeconds(5), CancellationToken.None))!;
        Assert.That(BitConverter.ToUInt16(framed, 0), Is.EqualTo(2));
        Assert.That(framed[6], Is.EqualTo(1));
        Assert.That(BitConverter.ToInt32(framed, 7), Is.EqualTo(2));
        // Both signals were spent: the next poll waits for something new
        Assert.That(await channel.PollAsync("s", TimeSpan.FromMilliseconds(50), CancellationToken.None), Is.Null);
        channel.Complete("s", """{"id":"a","ok":false,"error":"nope"}""");
        channel.Complete("s", """{"id":"b","ok":true,"edit":"b","placed":1,"skipped":0,"applied":true}""");
        Assert.That((await a).Error, Is.EqualTo("nope"));
        Assert.That((await b).Ok, Is.True);
    }

    [Test]
    public async Task ASlowEdit_TimesOutForTheCaller_AndAStrangersResultIsNotTaken()
    {
        var channel = new EditChannelService();
        await channel.PollAsync("s", TimeSpan.FromMilliseconds(10), CancellationToken.None);
        var result = await channel.SubmitAsync("s", "slow", [1], TimeSpan.FromMilliseconds(100));
        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Does.Contain("did not answer"));
        Assert.That(channel.Complete("s", """{"id":"nobody","ok":true}"""), Is.False);
        Assert.That(channel.Complete("s", "not json"), Is.False);
        Assert.That(channel.Complete("s", """{"ok":true}"""), Is.False);
    }

    [Test]
    public async Task EachServerHasItsOwnQueue()
    {
        var channel = new EditChannelService();
        await channel.PollAsync("one", TimeSpan.FromMilliseconds(10), CancellationToken.None);
        await channel.PollAsync("two", TimeSpan.FromMilliseconds(10), CancellationToken.None);
        _ = channel.SubmitAsync("one", "x", [1], TimeSpan.FromSeconds(2));
        Assert.That(await channel.PollAsync("two", TimeSpan.FromMilliseconds(100), CancellationToken.None), Is.Null);
        Assert.That(await channel.PollAsync("one", TimeSpan.FromMilliseconds(100), CancellationToken.None), Is.Not.Null);
    }
}
