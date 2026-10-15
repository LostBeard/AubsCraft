using AubsCraft.Live.Model;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// The live frame: parsed from the bytes the Java plugin's own encoder wrote (TestData/live-frame-v1.bin, from LivePlugin/make-fixture.sh), written
/// back identically by the C# encoder, and refused when it is truncated, lying about a count, or not ours.
/// </summary>
public class LiveFrameCodecTests
{
    private static byte[] Fixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "live-frame-v1.bin"));

    // The frame FixtureGen.java encodes, as C# values
    private static LiveFrame Expected() => new()
    {
        FullTime = 123456789L, DayTime = 6000L, Raining = true, Thundering = true,
        Worlds =
        [
            new LiveWorld
            {
                Name = "world",
                Entities =
                [
                    new LiveEntity(101, "cow", 10.5f, 64f, -20.25f, 90f, -10f, 85f, LiveFlags.OnGround, 0f, 0f, 0f, null, null),
                    new LiveEntity(102, "sheep", -3f, 70.5f, 8f, 180f, 0f, 180f, LiveFlags.OnGround | LiveFlags.Baby, 0.05f, 0f, -0.1f, "Dolly", "pink"),
                    new LiveEntity(7, "player", 100f, 80f, 100f, -90f, 30f, -92f, LiveFlags.Player | LiveFlags.Sneaking | LiveFlags.OnGround, 0f, -0.0784f, 0f, null, null),
                    new LiveEntity(103, "mymod:thing", 0f, 120f, 0f, 0f, 0f, 0f, 0, 0f, 0f, 0f, "Café über", null),
                ],
                Players = [new LivePlayer(7, "01234567-89ab-cdef-fedc-ba9876543210", "Steve", "http://textures.minecraft.net/texture/abc123", true, 1, "diamond_sword", 200 / 255f)],
            },
            new LiveWorld { Name = "world_nether", Entities = [], Players = [] },
        ],
    };

    [Test]
    public void TheJavaEncodersFrame_ParsesToWhatWasEncoded()
    {
        var f = LiveFrameCodec.Parse(Fixture());
        Assert.That(f.FullTime, Is.EqualTo(123456789L));
        Assert.That(f.DayTime, Is.EqualTo(6000L));
        Assert.That((f.Raining, f.Thundering), Is.EqualTo((true, true)));
        Assert.That(f.Worlds.Select(w => w.Name), Is.EqualTo(new[] { "world", "world_nether" }));

        var world = f.Worlds[0];
        Assert.That(world.Entities, Has.Count.EqualTo(4));
        var cow = world.Entities[0];
        Assert.That((cow.Id, cow.Type, cow.X, cow.Y, cow.Z), Is.EqualTo((101, "cow", 10.5f, 64f, -20.25f)));
        Assert.That(cow.Yaw, Is.EqualTo(90f), "64 of 256 turns");
        Assert.That(cow.Pitch, Is.EqualTo(-7 * 360f / 256f).Within(1e-4), "-10 degrees in 256ths: -7");
        Assert.That(cow.OnGround, Is.True);
        Assert.That(cow.Name, Is.Null);
        Assert.That(cow.Variant, Is.Null);

        var sheep = world.Entities[1];
        Assert.That(sheep.Baby, Is.True);
        Assert.That(sheep.Name, Is.EqualTo("Dolly"));
        Assert.That(sheep.Variant, Is.EqualTo("pink"));
        Assert.That((sheep.Vx, sheep.Vy, sheep.Vz), Is.EqualTo((0.05f, 0f, -0.1f)));

        var steve = world.Entities[2];
        Assert.That(steve.IsPlayer, Is.True);
        Assert.That(steve.Sneaking, Is.True);
        Assert.That(steve.Yaw, Is.EqualTo(-90f));
        Assert.That(steve.Vy, Is.EqualTo(-627 / 8000f).Within(1e-6), "falling: -0.0784 blocks a tick in 1/8000ths");

        Assert.That(world.Entities[3].Type, Is.EqualTo("mymod:thing"), "another namespace keeps it");
        Assert.That(world.Entities[3].Name, Is.EqualTo("Café über"), "UTF-8 survives");

        Assert.That(world.Players, Has.Count.EqualTo(1));
        var p = world.Players[0];
        Assert.That((p.EntityId, p.Name, p.Slim, p.GameMode, p.HeldItem), Is.EqualTo((7, "Steve", true, 1, "diamond_sword")));
        Assert.That(p.Uuid, Is.EqualTo("01234567-89ab-cdef-fedc-ba9876543210"));
        Assert.That(p.SkinUrl, Is.EqualTo("http://textures.minecraft.net/texture/abc123"));
        Assert.That(p.Health, Is.EqualTo(200 / 255f).Within(1e-6));
        Assert.That(f.Worlds[1].Entities, Is.Empty);
    }

    [Test]
    public void TheCSharpEncoder_WritesTheSameBytesAsTheJavaOne()
    {
        // Two implementations of one format in two languages: the same frame must be the same bytes
        Assert.That(LiveFrameCodec.Write(Expected()), Is.EqualTo(Fixture()));
    }

    [Test]
    public void AFrameSurvivesWritingAndReading()
    {
        var parsed = LiveFrameCodec.Parse(LiveFrameCodec.Write(Expected()));
        var again = LiveFrameCodec.Parse(LiveFrameCodec.Write(parsed));
        Assert.That(again.Worlds[0].Entities, Is.EqualTo(parsed.Worlds[0].Entities), "once quantised, a frame no longer changes");
        Assert.That(again.Worlds[0].Players, Is.EqualTo(parsed.Worlds[0].Players));
    }

    [Test]
    public void EveryTruncationOfAFrame_IsAnInvalidDataException_NothingElse()
    {
        var bytes = Fixture();
        for (int n = 0; n < bytes.Length; n++)
        {
            int length = n;
            Assert.Throws<InvalidDataException>(() => LiveFrameCodec.Parse(bytes.AsSpan(0, length)), $"the first {length} bytes");
        }
    }

    [Test]
    public void ATrailingByte_IsRefused()
    {
        var bytes = Fixture().Append((byte)0).ToArray();
        Assert.Throws<InvalidDataException>(() => LiveFrameCodec.Parse(bytes));
    }

    [Test]
    public void BadMagic_AndAnotherVersion_AreRefused()
    {
        var bytes = Fixture();
        var wrongMagic = (byte[])bytes.Clone(); wrongMagic[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => LiveFrameCodec.Parse(wrongMagic));
        var wrongVersion = (byte[])bytes.Clone(); wrongVersion[4] = 2;
        var ex = Assert.Throws<InvalidDataException>(() => LiveFrameCodec.Parse(wrongVersion))!;
        Assert.That(ex.Message, Does.Contain("format 2"));
    }

    [Test]
    public void ACountThatCannotFit_IsCaughtBeforeAnythingIsAllocated()
    {
        // A header with no types, one world named "w" that claims 65535 entities in the 3 bytes that follow
        var evil = new List<byte> { (byte)'A', (byte)'C', (byte)'L', (byte)'1', 1 };
        evil.AddRange(new byte[8 + 8 + 1]);       // times, weather
        evil.AddRange(new byte[] { 0, 0 });       // no types
        evil.AddRange(new byte[] { 1 });          // one world
        evil.AddRange(new byte[] { 1, (byte)'w' });
        evil.AddRange(new byte[] { 0xFF, 0xFF }); // 65535 entities...
        evil.AddRange(new byte[] { 1, 2, 3 });    // ...and 3 bytes
        var ex = Assert.Throws<InvalidDataException>(() => LiveFrameCodec.Parse(evil.ToArray()))!;
        Assert.That(ex.Message, Does.Contain("ends early"));
    }

    [Test]
    public void AnEntityThatNamesAMissingType_IsRefused()
    {
        // A frame by hand: one type ("cow"), one world, one entity whose type index is 5
        var b = new List<byte> { (byte)'A', (byte)'C', (byte)'L', (byte)'1', 1 };
        b.AddRange(new byte[8 + 8 + 1]);                       // times, weather
        b.AddRange(new byte[] { 1, 0, 3, (byte)'c', (byte)'o', (byte)'w' });   // one type: cow
        b.AddRange(new byte[] { 1, 1, (byte)'w' });            // one world "w"
        b.AddRange(new byte[] { 1, 0 });                       // one entity
        b.AddRange(BitConverter.GetBytes(42));                 // id
        b.AddRange(new byte[] { 5, 0 });                       // type index 5
        b.AddRange(new byte[12 + 3 + 2 + 6]);                  // position, angles, flags, velocity
        b.AddRange(new byte[] { 0, 0, 0, 0, 0, 0 });           // no names, variants, players
        var ex = Assert.Throws<InvalidDataException>(() => LiveFrameCodec.Parse(b.ToArray()))!;
        Assert.That(ex.Message, Does.Contain("type 5"));
        // and the same frame with type index 0 is fine: the fault is the index, nothing else
        b[b.Count - (12 + 3 + 2 + 6) - 6 - 2] = 0;
        Assert.That(LiveFrameCodec.Parse(b.ToArray()).Worlds[0].Entities[0].Type, Is.EqualTo("cow"));
    }

    [Test]
    public void LongStrings_AreCutAtACharacter_NotInTheMiddleOfOne()
    {
        // 200 two-byte characters = 400 bytes: a name is at most 255, and 255 would split a character
        string name = new string('é', 200);
        var f = new LiveFrame
        {
            Worlds = [new LiveWorld { Name = "w", Players = [], Entities = [new LiveEntity(1, "cow", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, name, null)] }],
        };
        var back = LiveFrameCodec.Parse(LiveFrameCodec.Write(f)).Worlds[0].Entities[0].Name!;
        Assert.That(back.Length, Is.EqualTo(127), "127 whole characters = 254 bytes");
        Assert.That(back.All(c => c == 'é'), Is.True);
    }

    [Test]
    public void AnglesAreExactAtEverySignedByteStep_AndAWholeTurnIsNothing()
    {
        for (int step = -128; step < 128; step++)
        {
            float degrees = step * 360f / 256f;
            var f = new LiveFrame { Worlds = [new LiveWorld { Name = "w", Players = [], Entities = [new LiveEntity(1, "cow", 0, 0, 0, degrees, 0, 0, 0, 0, 0, 0, null, null)] }] };
            var back = LiveFrameCodec.Parse(LiveFrameCodec.Write(f)).Worlds[0].Entities[0].Yaw;
            Assert.That(back, Is.EqualTo(degrees).Within(1e-3), $"step {step}");
        }
        var turn = new LiveFrame { Worlds = [new LiveWorld { Name = "w", Players = [], Entities = [new LiveEntity(1, "cow", 0, 0, 0, 360f, 0, 0, 0, 0, 0, 0, null, null)] }] };
        Assert.That(LiveFrameCodec.Parse(LiveFrameCodec.Write(turn)).Worlds[0].Entities[0].Yaw, Is.EqualTo(0f));
    }

    [Test]
    public void ATypeUsedByManyEntities_IsTabledOnce_SoFramesStaySmall()
    {
        var entities = Enumerable.Range(0, 1000).Select(i => new LiveEntity(i, i % 2 == 0 ? "cow" : "pig", i, 64, i, 0, 0, 0, 0, 0, 0, 0, null, null)).ToList();
        var bytes = LiveFrameCodec.Write(new LiveFrame { Worlds = [new LiveWorld { Name = "world", Players = [], Entities = entities }] });
        // 29 bytes an entity + a few dozen of header: under 31 bytes each on average
        Assert.That(bytes.Length, Is.LessThan(1000 * 31));
        Assert.That(LiveFrameCodec.Parse(bytes).Worlds[0].Entities.Count(e => e.Type == "pig"), Is.EqualTo(500));
    }
}
