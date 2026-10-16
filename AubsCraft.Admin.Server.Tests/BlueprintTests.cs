using AubsCraft.Admin.Server.Services;
using AubsCraft.Live.Model.Editing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>A piece of the world kept and put down again: taken from chunks, trimmed, stored, pasted turned.</summary>
public class BlueprintTests
{
    private sealed class World
    {
        private readonly Dictionary<(int, int), ChunkResult> _chunks = new();

        public World Set(int x, int y, int z, string name)
        {
            int cx = x >> 4, cz = z >> 4;
            if (!_chunks.TryGetValue((cx, cz), out var chunk)) _chunks[(cx, cz)] = chunk = new ChunkResult(new ushort[98304], ["minecraft:air"]);
            int id = chunk.Palette.IndexOf(name);
            if (id < 0) { id = chunk.Palette.Count; chunk.Palette.Add(name); }
            chunk.Blocks[(x & 15) + (z & 15) * 16 + (y + 64) * 256] = (ushort)id;
            return this;
        }

        public ChunkResult? At(int cx, int cz) => _chunks.GetValueOrDefault((cx, cz));
    }

    // A small house across a chunk border: a 3 x 2 x 2 wall of stone with a stair on the north end facing north, at x 14..16, y 5..6, z 3..4
    private static World House() => new World()
        .Set(14, 5, 3, "minecraft:stone").Set(15, 5, 3, "minecraft:stone").Set(16, 5, 3, "minecraft:stone")
        .Set(14, 5, 4, "minecraft:stone").Set(16, 5, 4, "minecraft:oak_stairs[facing=north,half=bottom]")
        .Set(14, 6, 3, "minecraft:glass");

    [Test]
    public void ABoxOfTheWorld_BecomesABlueprint_TrimmedToWhatIsInIt()
    {
        var w = House();
        var raw = Blueprint.FromWorld(w.At, 10, 0, 0, 30, 20, 10, "house", "alice", trim: false);
        Assert.That((raw.Width, raw.Height, raw.Depth), Is.EqualTo((21, 21, 11)));
        var bp = Blueprint.FromWorld(w.At, 10, 0, 0, 30, 20, 10, "house", "alice");
        Assert.That((bp.Width, bp.Height, bp.Depth), Is.EqualTo((3, 2, 2)), "only the house is kept");
        Assert.That(bp.Count, Is.EqualTo(6));
        Assert.That(bp.Name, Is.EqualTo("house"));
        Assert.That(bp.Author, Is.EqualTo("alice"));
        // (x, y, z) of the blueprint: the west end is x 0
        string At(int x, int y, int z) => bp.Palette[bp.Blocks[(y * bp.Depth + z) * bp.Width + x]];
        Assert.That(At(0, 0, 0), Is.EqualTo("minecraft:stone"));
        Assert.That(At(2, 0, 1), Is.EqualTo("minecraft:oak_stairs[facing=north,half=bottom]"));
        Assert.That(At(0, 1, 0), Is.EqualTo("minecraft:glass"));
        Assert.That(At(1, 0, 1), Is.EqualTo("minecraft:air"));
        Assert.That(bp.Palette[0], Is.EqualTo("minecraft:air"));
    }

    [Test]
    public void TheCornersMayBeGivenInAnyOrder_AndUnloadedColumnsAreAir()
    {
        var w = House();
        var a = Blueprint.FromWorld(w.At, 10, 0, 0, 30, 20, 10, "a", "x");
        var b = Blueprint.FromWorld(w.At, 30, 20, 10, 10, 0, 0, "b", "x");
        Assert.That(b.Blocks, Is.EqualTo(a.Blocks));
        var empty = Blueprint.FromWorld(w.At, 500, 0, 500, 510, 5, 510, "e", "x");
        Assert.That((empty.Width, empty.Height, empty.Depth, empty.Count), Is.EqualTo((1, 1, 1, 0)), "nothing there: one air block");
    }

    [Test]
    public void ATooBigBox_IsRefused()
    {
        Assert.That(() => Blueprint.FromWorld(new World().At, 0, -64, 0, 400, 319, 400, "big", "x"), Throws.TypeOf<ArgumentException>().With.Message.Contain("at most"));
    }

    [Test]
    public void ABlueprintSurvivesItsFile()
    {
        var bp = Blueprint.FromWorld(House().At, 10, 0, 0, 30, 20, 10, "house", "alice");
        var back = Blueprint.FromBytes(bp.ToBytes());
        Assert.That((back.Name, back.Author, back.Width, back.Height, back.Depth), Is.EqualTo((bp.Name, bp.Author, bp.Width, bp.Height, bp.Depth)));
        Assert.That(back.Palette, Is.EqualTo(bp.Palette));
        Assert.That(back.Blocks, Is.EqualTo(bp.Blocks));
        Assert.That(back.CreatedUtc, Is.EqualTo(bp.CreatedUtc));
        Assert.That(bp.ToBytes().Length, Is.LessThan(2000), "mostly air: small");
    }

    [Test]
    public void ABrokenFile_IsRefused()
    {
        var bytes = Blueprint.FromWorld(House().At, 10, 0, 0, 30, 20, 10, "house", "alice").ToBytes();
        Assert.Throws<InvalidDataException>(() => Blueprint.FromBytes([]));
        Assert.Throws<InvalidDataException>(() => Blueprint.FromBytes([1, 2, 3, 4, 5]));
        Assert.Throws<InvalidDataException>(() => Blueprint.FromBytes(bytes[..(bytes.Length / 2)]));
    }

    // ---- pasting ----

    private static (int X, int Y, int Z, string Block)[] Placed(EditRequest request)
    {
        var op = request.Ops.Single();
        var data = Convert.FromBase64String(op.Data!);
        var list = new List<(int, int, int, string)>();
        for (int i = 0; i < data.Length; i += 8)
            list.Add((op.At![0] + BitConverter.ToInt16(data, i), op.At[1] + BitConverter.ToInt16(data, i + 2), op.At[2] + BitConverter.ToInt16(data, i + 4), op.Palette![BitConverter.ToUInt16(data, i + 6)]));
        return list.ToArray();
    }

    [Test]
    public void APaste_PutsEachBlockAtItsPlace_AndLeavesTheAirOut()
    {
        var bp = Blueprint.FromWorld(House().At, 10, 0, 0, 30, 20, 10, "house", "alice");
        var placed = Placed(bp.ToPaste(100, 10, 200));
        Assert.That(placed.Length, Is.EqualTo(6), "air is left out");
        Assert.That(placed, Does.Contain((100, 10, 200, "minecraft:stone")));
        Assert.That(placed, Does.Contain((102, 10, 201, "minecraft:oak_stairs[facing=north,half=bottom]")));
        Assert.That(placed, Does.Contain((100, 11, 200, "minecraft:glass")));
        Assert.That(Placed(bp.ToPaste(0, 0, 0, withAir: true)).Length, Is.EqualTo(12), "with air: the whole box");
    }

    [Test]
    public void APasteTurnedAQuarter_TurnsThePlacesAndTheStates()
    {
        var bp = Blueprint.FromWorld(House().At, 10, 0, 0, 30, 20, 10, "house", "alice");
        // 3 wide, 2 deep: after a quarter turn 2 wide, 3 deep; the north-west stone (0,0) goes to the north-east (1,0); the stair at (2,1) to (0,2), facing east
        var placed = Placed(bp.ToPaste(0, 0, 0, quarterTurns: 1));
        Assert.That(placed, Does.Contain((1, 0, 0, "minecraft:stone")));
        Assert.That(placed, Does.Contain((0, 0, 2, "minecraft:oak_stairs[facing=east,half=bottom]")));
        Assert.That(placed.All(p => p.X is >= 0 and <= 1 && p.Z is >= 0 and <= 2), Is.True, "inside the turned box");
        Assert.That(bp.SizeAfter(1), Is.EqualTo((2, 2, 3)));
        Assert.That(bp.SizeAfter(2), Is.EqualTo((3, 2, 2)));
    }

    [Test]
    public void APasteMirrored_FlipsThePlaces()
    {
        var bp = Blueprint.FromWorld(House().At, 10, 0, 0, 30, 20, 10, "house", "alice");
        var placed = Placed(bp.ToPaste(0, 0, 0, mirrorX: true));
        Assert.That(placed, Does.Contain((2, 0, 0, "minecraft:stone")), "the west stone is on the east now");
        Assert.That(placed, Does.Contain((0, 0, 1, "minecraft:oak_stairs[facing=north,half=bottom]")), "the stair faced north: unchanged by an east-west flip");
    }

    [Test]
    public void ThePasteIsAnEditThePanelAccepts()
    {
        var bp = Blueprint.FromWorld(House().At, 10, 0, 0, 30, 20, 10, "house", "alice");
        var payload = EditCodec.Build("c", "alice", bp.ToPaste(0, 0, 0, 1), out var blocks);
        Assert.That(blocks, Is.EqualTo(6));
        Assert.That(payload.Length, Is.GreaterThan(50));
        // Below the world: refused by the codec
        Assert.That(() => EditCodec.Build("c", "alice", bp.ToPaste(0, -64, 0).Also(r => r.Ops[0].At![1] = -70), out _), Throws.TypeOf<ArgumentException>());
        // An all-air blueprint has nothing to paste
        var air = Blueprint.FromWorld(new World().At, 0, 0, 0, 3, 3, 3, "air", "x");
        Assert.That(() => air.ToPaste(0, 0, 0), Throws.TypeOf<ArgumentException>());
    }

    // ---- the store ----

    private static BlueprintStore Store(out string folder)
    {
        folder = TestUtil.NewTempDir();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Blueprints:Path"] = folder }).Build();
        return new BlueprintStore(config, NullLogger<BlueprintStore>.Instance);
    }

    [Test]
    public void TheStoreKeepsListsLoadsAndDeletes_PerServer()
    {
        var store = Store(out _);
        var bp = Blueprint.FromWorld(House().At, 10, 0, 0, 30, 20, 10, "My House", "alice");
        store.Save("alpha", bp);
        store.Save("bravo", Blueprint.FromWorld(House().At, 10, 0, 0, 30, 20, 10, "Other", "bob"));
        Assert.That(store.List("alpha").Select(i => i.Name), Is.EqualTo(new[] { "My House" }));
        Assert.That(store.List("bravo").Select(i => i.Name), Is.EqualTo(new[] { "Other" }), "each server has its own");
        var info = store.List("alpha").Single();
        Assert.That((info.Width, info.Height, info.Depth, info.Blocks, info.Author), Is.EqualTo((3, 2, 2, 6, "alice")));
        Assert.That(store.Load("alpha", "My House")!.Blocks, Is.EqualTo(bp.Blocks));
        Assert.That(store.Load("alpha", "my house")!.Name, Is.EqualTo("My House"), "found by name, any case (the file system is not case sensitive here)");
        Assert.That(store.Load("alpha", "Other"), Is.Null);
        Assert.That(store.Delete("alpha", "My House"), Is.True);
        Assert.That(store.Delete("alpha", "My House"), Is.False);
        Assert.That(store.List("alpha"), Is.Empty);
    }

    [Test]
    public void ASavedNameReplacesTheOldOne_AndRenameKeepsTheBlocks()
    {
        var store = Store(out _);
        var first = Blueprint.FromWorld(House().At, 10, 0, 0, 30, 20, 10, "tower", "a");
        store.Save("s", first);
        store.Save("s", Blueprint.FromWorld(House().At, 14, 5, 3, 14, 5, 3, "tower", "b"));
        Assert.That(store.List("s").Single().Blocks, Is.EqualTo(1));
        Assert.That(store.Rename("s", "tower", "castle"), Is.True);
        Assert.That(store.List("s").Select(i => i.Name), Is.EqualTo(new[] { "castle" }));
        Assert.That(store.Rename("s", "castle", "castle"), Is.False, "the new name is taken");
        Assert.That(store.Rename("s", "nothing", "x"), Is.False);
    }

    [Test]
    public void NamesCannotPointOutsideTheFolder()
    {
        var store = Store(out var folder);
        Assert.That(BlueprintStore.CleanName("../../etc/passwd"), Is.EqualTo("etcpasswd"));
        Assert.That(BlueprintStore.CleanName("  ..  "), Is.Null);
        Assert.That(BlueprintStore.CleanName("a<b>c|d:e"), Is.EqualTo("abcde"));
        Assert.That(BlueprintStore.CleanName(new string('x', 200))!.Length, Is.EqualTo(BlueprintStore.MaxNameLength));
        Assert.That(BlueprintStore.CleanName(null), Is.Null);
        var bp = new Blueprint { Name = "..\\..\\evil", Width = 1, Height = 1, Depth = 1, Blocks = [0] };
        store.Save("..\\..\\server", bp);
        Assert.That(Directory.GetFiles(folder, "*.abp", SearchOption.AllDirectories).All(f => Path.GetFullPath(f).StartsWith(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public void TheClipboardIsEachAdminsOwn()
    {
        var store = Store(out _);
        var a = Blueprint.FromWorld(House().At, 10, 0, 0, 30, 20, 10, "x", "a");
        store.SetClipboard("s", "alice", a);
        Assert.That(store.Clipboard("s", "alice"), Is.SameAs(a));
        Assert.That(store.Clipboard("s", "bob"), Is.Null);
        Assert.That(store.Clipboard("t", "alice"), Is.Null);
    }
}

internal static class FluentExtensions
{
    public static T Also<T>(this T value, Action<T> act) { act(value); return value; }
}
