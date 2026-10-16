using AubsCraft.Assets;
using AubsCraft.Assets.Model;
using AubsCraft.Live.Model.Entities;
using StbImageSharp;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>The entity atlas: every texture packed where the table says, intact, none over another, the same every time; and the models checked against the game's real textures.</summary>
public class EntityAtlasTests
{
    // How far right and down the painted faces of a box's layout reach: a flat box (a plane: one dimension 0) has no side faces to count
    private static (float Right, float Bottom) LayoutExtent(AubsCraft.Live.Model.Entities.ModelBox b)
    {
        (float x, float y, float w, float h)[] faces =
        [
            (b.U + b.D, b.V, b.W, b.D), (b.U + b.D + b.W, b.V, b.W, b.D),
            (b.U, b.V + b.D, b.D, b.H), (b.U + b.D, b.V + b.D, b.W, b.H), (b.U + b.D + b.W, b.V + b.D, b.D, b.H), (b.U + 2 * b.D + b.W, b.V + b.D, b.W, b.H),
        ];
        float right = 0, bottom = 0;
        foreach (var (x, y, w, h) in faces) if (w > 0 && h > 0) { right = Math.Max(right, x + w); bottom = Math.Max(bottom, y + h); }
        return (right, bottom);
    }

    private static ClientJar Jar(params (string name, int w, int h, byte r, byte g, byte b)[] textures)
    {
        var jar = new FakeJar();
        foreach (var (name, w, h, r, g, b) in textures)
            jar.Texture($"entity/{name}", w, h, (x, y) => ((byte)(r ^ x), (byte)(g ^ y), b, 255));   // every pixel differs, so a misplaced copy shows
        return jar.Build();
    }

    private static EntityAtlasResult Build(ClientJar jar, params string[] names) => new EntityAtlasBuilder(jar, "test").Build(names);

    private static (byte r, byte g, byte b, byte a) At(EntityAtlasResult r, int x, int y)
    {
        int i = (y * r.Assets.AtlasWidth + x) * 4;
        return (r.StaticRgba[i], r.StaticRgba[i + 1], r.StaticRgba[i + 2], r.StaticRgba[i + 3]);
    }

    [Test]
    public void EveryTexture_IsWhereTheTableSaysItIs_Intact()
    {
        using var jar = Jar(("pig/pig", 64, 32, 10, 20, 30), ("zombie/zombie", 64, 64, 40, 50, 60), ("cow/cow", 64, 32, 70, 80, 90), ("tiny/t", 8, 8, 1, 2, 3));
        var r = Build(jar, "pig/pig", "zombie/zombie", "cow/cow", "tiny/t");
        Assert.That(r.Assets.Textures.Keys, Is.EquivalentTo(new[] { "pig/pig", "zombie/zombie", "cow/cow", "tiny/t" }));
        foreach (var (name, w, h, rr, gg, bb) in new[] { ("pig/pig", 64, 32, 10, 20, 30), ("zombie/zombie", 64, 64, 40, 50, 60), ("cow/cow", 64, 32, 70, 80, 90), ("tiny/t", 8, 8, 1, 2, 3) })
        {
            var at = r.Assets.Textures[name];
            Assert.That(at[2], Is.EqualTo(w), name); Assert.That(at[3], Is.EqualTo(h), name);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    Assert.That(At(r, at[0] + x, at[1] + y), Is.EqualTo(((byte)(rr ^ x), (byte)(gg ^ y), (byte)bb, (byte)255)), $"{name} ({x},{y})");
        }
    }

    [Test]
    public void NoTextureOverlapsAnother_AndAllAreAboveTheSkinSlots()
    {
        using var jar = Jar(Enumerable.Range(0, 30).Select(i => ($"mob/m{i}", 16 + (i % 5) * 16, 16 + (i % 3) * 16, (byte)i, (byte)(i * 3), (byte)(i * 7))).ToArray());
        var r = Build(jar, Enumerable.Range(0, 30).Select(i => $"mob/m{i}").ToArray());
        var rects = r.Assets.Textures.Values.ToList();
        for (int i = 0; i < rects.Count; i++)
        {
            Assert.That(rects[i][0] + rects[i][2], Is.LessThanOrEqualTo(r.Assets.AtlasWidth));
            Assert.That(rects[i][1] + rects[i][3], Is.LessThanOrEqualTo(r.Assets.SkinY), "above the skin slots");
            for (int j = i + 1; j < rects.Count; j++)
            {
                bool apart = rects[i][0] + rects[i][2] <= rects[j][0] || rects[j][0] + rects[j][2] <= rects[i][0]
                          || rects[i][1] + rects[i][3] <= rects[j][1] || rects[j][1] + rects[j][3] <= rects[i][1];
                Assert.That(apart, Is.True, $"textures {i} and {j} overlap");
            }
        }
        Assert.That(r.StaticRgba.Length, Is.EqualTo(r.Assets.AtlasWidth * r.Assets.StaticHeight * 4));
        Assert.That(r.Assets.StaticHeight, Is.GreaterThanOrEqualTo(rects.Max(x => x[1] + x[3])));
    }

    [Test]
    public void TheSkinSlots_AreAGridBelow_ThatHoldsAGoodNumberOfPlayers()
    {
        using var jar = Jar(("a", 16, 16, 1, 1, 1));
        var a = Build(jar, "a").Assets;
        Assert.That(a.SkinY, Is.EqualTo(EntityAtlasBuilder.StaticLimit));
        Assert.That(a.SkinSize, Is.EqualTo(64));
        Assert.That(a.SkinY + a.SkinRows * a.SkinSize, Is.LessThanOrEqualTo(a.AtlasHeight));
        Assert.That(a.SkinColumns * a.SkinSize, Is.LessThanOrEqualTo(a.AtlasWidth));
        Assert.That(a.SkinColumns * a.SkinRows, Is.GreaterThanOrEqualTo(100));
    }

    [Test]
    public void ThePacking_IsTheSameWhateverOrderTheNamesCome()
    {
        using var jar = Jar(("a", 64, 32, 1, 2, 3), ("b", 64, 64, 4, 5, 6), ("c", 32, 32, 7, 8, 9), ("d", 64, 32, 10, 11, 12));
        var one = Build(jar, "a", "b", "c", "d");
        var two = Build(jar, "d", "c", "b", "a", "a");
        Assert.That(two.StaticRgba, Is.EqualTo(one.StaticRgba));
        foreach (var name in new[] { "a", "b", "c", "d" }) Assert.That(two.Assets.Textures[name], Is.EqualTo(one.Assets.Textures[name]), name);
    }

    [Test]
    public void AMissingTexture_IsReported_AndTheRestStillPack()
    {
        using var jar = Jar(("there", 16, 16, 1, 2, 3));
        var r = Build(jar, "there", "not/there");
        Assert.That(r.Assets.Textures.ContainsKey("there"), Is.True);
        Assert.That(r.Assets.Textures.ContainsKey("not/there"), Is.False);
        Assert.That(r.Report, Does.Contain("MISSING texture: not/there"));
    }

    [Test]
    public void TheWhiteTile_NeedsNoJarFile_AndIsOpaqueWhite()
    {
        using var jar = Jar();
        var r = Build(jar, EntityAtlasBuilder.White);
        var at = r.Assets.Textures[EntityAtlasBuilder.White];
        Assert.That(at[2] >= 1 && at[3] >= 1, Is.True);
        Assert.That(At(r, at[0], at[1]), Is.EqualTo(((byte)255, (byte)255, (byte)255, (byte)255)));
        Assert.That(At(r, at[0] + at[2] - 1, at[1] + at[3] - 1), Is.EqualTo(((byte)255, (byte)255, (byte)255, (byte)255)));
    }

    [Test]
    public void TooManyTextures_FailLoudly_NotByOverwritingTheSkinSlots()
    {
        using var jar = Jar(Enumerable.Range(0, 40).Select(i => ($"big/b{i}", 512, 128, (byte)i, (byte)i, (byte)i)).ToArray());
        var ex = Assert.Throws<InvalidOperationException>(() => Build(jar, Enumerable.Range(0, 40).Select(i => $"big/b{i}").ToArray()));
        Assert.That(ex!.Message, Does.Contain("do not fit"));
    }

    [Test]
    public void ATextureWiderThanTheAtlas_FailsLoudly()
    {
        using var jar = Jar(("wide", 2048, 8, 1, 1, 1));
        Assert.Throws<InvalidOperationException>(() => Build(jar, "wide"));
    }

    // ---- the models against the game's real textures ----

    [Test]
    public async Task EveryModelTexture_IsInTheRealJar_AndEveryBoxFitsItsTexture()
    {
        var jarPath = await JarLocator.FindOrDownloadAsync("1.21.5");
        using var jar = ClientJar.Open(jarPath);
        var sizes = new Dictionary<string, (int w, int h)>();
        (int w, int h) SizeOf(string name)
        {
            if (sizes.TryGetValue(name, out var s)) return s;
            var path = $"assets/minecraft/textures/entity/{name}.png";
            Assert.That(jar.Exists(path), Is.True, $"{path} is not in the jar");
            var image = ImageResult.FromMemory(jar.ReadBytes(path), ColorComponents.RedGreenBlueAlpha);
            return sizes[name] = (image.Width, image.Height);
        }

        foreach (var name in EntityModels.TextureNames().Where(n => n != "white")) SizeOf(name);

        foreach (var type in EntityModels.ModelledTypes.Where(t => t is not ("player" or "player_slim")))
        {
            var model = EntityModels.Get(type)!;
            foreach (var part in model.Parts)
            {
                // Every texture this part can be drawn in: its own, the variants', the model's
                var textures = new List<string>();
                if (part.Texture != null) textures.Add(part.Texture);
                else if (part.VariantTexture == null) { textures.Add(model.Texture); textures.AddRange(model.VariantTextures); }
                if (part.VariantTextureNames != null) textures.AddRange(part.VariantTextureNames);
                foreach (var texture in textures)
                {
                    var (tw, th) = SizeOf(texture);
                    foreach (var b in part.Boxes)
                    {
                        if (b.W == 0 || b.H == 0 || b.D == 0) continue;   // a plane: the game's own offsets, its second face may lie past the edge of the texture
                        var (right, bottom) = LayoutExtent(b);
                        Assert.That(right, Is.LessThanOrEqualTo(tw), $"{type}/{part.Name} in {texture}: the box's layout is wider than its {tw} px texture");
                        Assert.That(bottom, Is.LessThanOrEqualTo(th), $"{type}/{part.Name} in {texture}: the box's layout is taller than its {th} px texture");
                    }
                }
            }
        }
        // A player's skin is 64 x 64 (older skins are converted by the viewer): every default one
        foreach (var skin in EntityModels.DefaultSkinNames)
            foreach (var build in new[] { "wide", "slim" })
                Assert.That(SizeOf($"player/{build}/{skin}"), Is.EqualTo((64, 64)), $"player/{build}/{skin}");
        foreach (var type in new[] { "player", "player_slim" })
            foreach (var part in EntityModels.Get(type)!.Parts)
                foreach (var b in part.Boxes)
                {
                    Assert.That(b.U + 2 * b.D + 2 * b.W, Is.LessThanOrEqualTo(64), $"{type}/{part.Name}");
                    Assert.That(b.V + b.D + b.H, Is.LessThanOrEqualTo(64), $"{type}/{part.Name}");
                }

        var built = new EntityAtlasBuilder(jar, "1.21.5").Build(EntityModels.TextureNames());
        Assert.That(built.Report, Does.Not.Contain("MISSING"));
        Assert.That(built.Assets.Textures.Count, Is.EqualTo(EntityModels.TextureNames().Count()));
    }

    /// <summary>
    /// A box's texture layout is a cross of six faces; a wrong offset puts them on empty or foreign parts of the texture. Where the texture has transparent areas (most
    /// creature textures are mostly empty outside their layouts) most of every face must be painted. Only the creature's own texture is checked: a layer (markings,
    /// clothes) is transparent where it has nothing to add.
    /// </summary>
    // Boxes whose layout the game leaves mostly empty on purpose (a chicken's legs are thin lines, a skeleton horse has no tail)
    private static readonly HashSet<string> SparseBoxes =
    [
        "chicken/right_leg", "chicken/left_leg", "chicken_warm/right_leg", "chicken_warm/left_leg", "chicken_cold/right_leg", "chicken_cold/left_leg", "parrot/left_leg", "parrot/right_leg",
        "bee/left_antenna", "bee/right_antenna", "bee/right_wing", "bee/left_wing", "bee/front_legs", "bee/middle_legs", "bee/back_legs",
        "zombified_piglin/left_ear",   // it has one ear
        "skeleton_horse/head_parts", "skeleton_horse/mane", "skeleton_horse/tail", "skeleton_horse/left_ear", "skeleton_horse/right_ear",   // bones: no mane, no tail, no ears
        // The game's own numbers (tools/modeldump) over textures that are mostly gaps: a creaking is twigs, a breeze's brow and a guardian's tail tip are a few pixels
        "silverfish/layer0", "silverfish/layer1", "silverfish/layer2", "breeze/head", "creaking/head", "creaking/right_arm", "creaking/left_arm", "creaking/right_leg", "guardian/tail2", "elder_guardian/tail2",
    ];

    [Test]
    public async Task EveryBoxFace_OfEveryModel_LiesOnPaintedPixels()
    {
        var jarPath = await JarLocator.FindOrDownloadAsync("1.21.5");
        using var jar = ClientJar.Open(jarPath);
        var images = new Dictionary<string, ImageResult>();
        ImageResult Image(string name) => images.TryGetValue(name, out var i) ? i
            : images[name] = ImageResult.FromMemory(jar.ReadBytes($"assets/minecraft/textures/entity/{name}.png"), ColorComponents.RedGreenBlueAlpha);

        var problems = new List<string>();
        foreach (var type in EntityModels.ModelledTypes.Where(t => t is not ("player" or "player_slim")))
        {
            var model = EntityModels.Get(type)!;
            foreach (var part in model.Parts)
            {
                if (part.VariantTexture != null) continue;   // a layer
                var image = Image(part.Texture ?? model.Texture);
                bool hasTransparency = false;
                for (int i = 3; i < image.Data.Length && !hasTransparency; i += 4) hasTransparency = image.Data[i] == 0;
                if (!hasTransparency) continue;
                foreach (var b in part.Boxes)
                {
                    // A plane (a fin, a wing, a tail: one side 0) is mostly empty in the game's own texture, and its offsets may be negative: the game's numbers, not checked here
                    if (b.W == 0 || b.H == 0 || b.D == 0) continue;
                    (string face, float x, float y, float w, float h)[] faces =
                    [
                        ("top", b.U + b.D, b.V, b.W, b.D), ("bottom", b.U + b.D + b.W, b.V, b.W, b.D),
                        ("right", b.U, b.V + b.D, b.D, b.H), ("front", b.U + b.D, b.V + b.D, b.W, b.H),
                        ("left", b.U + b.D + b.W, b.V + b.D, b.D, b.H), ("back", b.U + 2 * b.D + b.W, b.V + b.D, b.W, b.H),
                    ];
                    int painted = 0, total = 0;
                    foreach (var (face, fx, fy, fw, fh) in faces)
                    {
                        if (fx < 0 || fy < 0) { problems.Add($"{type}/{part.Name} box at ({b.U},{b.V}) {b.W}x{b.H}x{b.D}: its {face} face starts at ({fx},{fy}), off the texture"); continue; }
                        for (int py = (int)fy; py < (int)Math.Ceiling(fy + fh) && py < image.Height; py++)
                            for (int px = (int)fx; px < (int)Math.Ceiling(fx + fw) && px < image.Width; px++)
                            {
                                total++;
                                if (image.Data[(py * image.Width + px) * 4 + 3] > 0) painted++;
                            }
                    }
                    // A model leaves some faces unpainted (a snout's back, a leg's bottom); a box whose whole layout is on empty texture is in the wrong place
                    if (total > 0 && painted < total * 0.85 && Environment.GetEnvironmentVariable("AUBS_SHOW_COVERAGE") != null)
                        TestContext.Progress.WriteLine($"[coverage] {type}/{part.Name} box at ({b.U},{b.V}) {b.W}x{b.H}x{b.D} infl {b.Inflate}: {painted * 100 / total}% ({painted}/{total})");
                    if (b.Inflate > 0f || SparseBoxes.Contains($"{type}/{part.Name}")) continue;   // an overlay (a hat, a jacket) is empty where it has nothing to add
                    if (total > 0 && painted < total * 0.4) problems.Add($"{type}/{part.Name} box at ({b.U},{b.V}) {b.W}x{b.H}x{b.D}: {painted} of {total} px painted");
                }
            }
        }
        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void TheDefaultSkin_IsStablePerPlayer_AndInTheRequestedBuild()
    {
        var a = EntityModels.DefaultSkinFor("01234567-89ab-cdef-fedc-ba9876543210", false);
        Assert.That(a, Does.StartWith("player/wide/"));
        Assert.That(EntityModels.DefaultSkinFor("01234567-89ab-cdef-fedc-ba9876543210", false), Is.EqualTo(a));
        Assert.That(EntityModels.DefaultSkinFor("01234567-89ab-cdef-fedc-ba9876543210", true), Does.StartWith("player/slim/"));
        Assert.That(EntityModels.DefaultSkinFor("01234567-89ab-cdef-fedc-ba9876543210", true)[12..], Is.EqualTo(a[12..]), "the same person keeps their face when the build differs");
        // A spread of players gets a spread of skins
        var skins = Enumerable.Range(0, 200).Select(i => EntityModels.DefaultSkinFor($"{i:x8}-0000-0000-0000-000000000000", false)).Distinct().Count();
        Assert.That(skins, Is.GreaterThanOrEqualTo(7));
    }
}
