using System.Numerics;
using AubsCraft.Live.Model;
using AubsCraft.Live.Model.Entities;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// A creature in the world: it faces where it should, stands on the ground, swings its legs in the game's diagonal pairs, turns its head, shrinks as a
/// baby, wears its dye. The models are the game's own box numbers, so these tests pin what the renderer's matrix order does with them.
/// </summary>
public class EntityPoserTests
{
    private static LiveEntityView View(string type = "pig", float x = 10, float y = 64, float z = 10, float yaw = 0, float body = 0, float pitch = 0,
        int flags = LiveFlags.OnGround, float speed = 0, float walked = 0, string? variant = null) =>
        new(1, type, x, y, z, yaw, pitch, body, flags, speed, walked, null, variant);

    private static List<PartDraw> Pose(string type, LiveEntityView view, float age = 0, string? texture = null)
    {
        var model = EntityModels.Get(type) ?? throw new InvalidOperationException(type);
        var list = new List<PartDraw>();
        EntityPoser.Pose(view, model, age, list, texture);
        return list;
    }

    /// <summary>A model-space point of a draw (px) in the world.</summary>
    private static Vector3 ToWorld(in PartDraw d, Vector3 modelPoint) => Vector3.Transform(modelPoint, d.Model);

    /// <summary>The centre of the box in the world.</summary>
    private static Vector3 Centre(in PartDraw d) => ToWorld(d, d.Origin + d.Size / 2f);

    private static (float MinY, float MaxY) VerticalExtent(List<PartDraw> draws)
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (var d in draws)
            for (int i = 0; i < 8; i++)
            {
                var corner = d.Origin + new Vector3((i & 1) * d.Size.X, ((i >> 1) & 1) * d.Size.Y, ((i >> 2) & 1) * d.Size.Z);
                float y = ToWorld(d, corner).Y;
                min = Math.Min(min, y); max = Math.Max(max, y);
            }
        return (min, max);
    }

    // ---- placement ----

    [Test]
    public void ACreature_FacesWhereItsYawSays()
    {
        // Minecraft: yaw 0 faces south (+z), 90 faces west (-x), 180 north (-z), -90 east (+x). The pig's snout is its most forward box.
        var cases = new[] { (0f, Vector3.UnitZ), (90f, -Vector3.UnitX), (180f, -Vector3.UnitZ), (-90f, Vector3.UnitX) };
        foreach (var (yaw, forward) in cases)
        {
            var draws = Pose("pig", View(yaw: yaw, body: yaw));
            // head part: the first draw is the head box, the second the snout; the snout is in front of the head's centre
            var head = Centre(draws[0]);
            var snout = Centre(draws[1]);
            var ahead = Vector3.Dot(snout - head, forward);
            Assert.That(ahead, Is.GreaterThan(0.2f), $"yaw {yaw}: the snout is {ahead} blocks ahead of the head's centre, expected clearly ahead");
        }
    }

    [Test]
    public void ACreature_StandsOnTheGround_AtItsPosition()
    {
        foreach (var type in new[] { "pig", "cow", "sheep", "chicken", "creeper", "zombie", "skeleton", "villager", "player", "wolf", "enderman" })
        {
            var (min, max) = VerticalExtent(Pose(type, View(type, y: 70)));
            Assert.That(min, Is.EqualTo(70f).Within(0.03f), $"{type}: its lowest point");
            var model = EntityModels.Get(type)!;
            Assert.That(max - 70f, Is.EqualTo(model.Height).Within(0.45f), $"{type}: its highest point is about its height");
        }
    }

    [Test]
    public void ACreature_IsCentredOnItsPosition()
    {
        // The body (the third draw after head and hat) is over the entity's position, whichever way it faces
        foreach (float yaw in new[] { 0f, 90f, 200f })
        {
            var body = Centre(Pose("zombie", View("zombie", x: 100, z: -40, yaw: yaw, body: yaw))[2]);
            Assert.That(body.X, Is.EqualTo(100f).Within(0.02f), $"yaw {yaw}");
            Assert.That(body.Z, Is.EqualTo(-40f).Within(0.02f), $"yaw {yaw}");
        }
    }

    [Test]
    public void ABaby_IsHalfTheSize_StillOnTheGround()
    {
        var adult = VerticalExtent(Pose("cow", View("cow", y: 64)));
        var baby = VerticalExtent(Pose("cow", View("cow", y: 64, flags: LiveFlags.OnGround | LiveFlags.Baby)));
        Assert.That(baby.MaxY - 64f, Is.EqualTo((adult.MaxY - 64f) * 0.5f).Within(0.02f));
        Assert.That(baby.MinY, Is.EqualTo(64f).Within(0.03f));
    }

    // ---- the walk cycle ----

    private static float LegAngle(List<PartDraw> draws, EntityModel model, string partName)
    {
        // The leg's swing shows as how far its box centre has moved forward of the rest pose: compare to a still pose
        int index = 0;
        foreach (var part in model.Parts)
        {
            if (part.Name == partName) return Centre(draws[index]).Z;
            index += part.Boxes.Length;
        }
        throw new InvalidOperationException(partName);
    }

    [Test]
    public void TheLegs_SwingInDiagonalPairs_AndStillWhenStill()
    {
        var model = EntityModels.Get("pig")!;
        var still = Pose("pig", View(speed: 0, walked: 7.3f));
        var walking = Pose("pig", View(speed: 0.1f, walked: 7.3f));

        float Moved(string leg) => LegAngle(walking, model, leg) - LegAngle(still, model, leg);
        float rh = Moved("right_hind_leg"), lh = Moved("left_hind_leg"), rf = Moved("right_front_leg"), lf = Moved("left_front_leg");
        Assert.That(MathF.Abs(rh), Is.GreaterThan(0.05f), "a walking pig moves its legs");
        // Diagonal pairs: right hind with left front, left hind with right front; the pairs are opposite
        Assert.That(rh, Is.EqualTo(lf).Within(0.001f));
        Assert.That(lh, Is.EqualTo(rf).Within(0.001f));
        Assert.That(rh * lh, Is.LessThan(0f), "the two pairs swing opposite ways");
        // No speed, no swing, however far it walked earlier
        foreach (var leg in new[] { "right_hind_leg", "left_hind_leg", "right_front_leg", "left_front_leg" })
            Assert.That(LegAngle(still, model, leg), Is.EqualTo(LegAngle(Pose("pig", View()), model, leg)).Within(0.0001f), leg);
    }

    [Test]
    public void TheSwing_FollowsTheDistanceWalked()
    {
        var model = EntityModels.Get("pig")!;
        var rest = LegAngle(Pose("pig", View(speed: 0f)), model, "right_hind_leg");
        float At(float walked) => LegAngle(Pose("pig", View(speed: 0.1f, walked: walked)), model, "right_hind_leg") - rest;
        // One whole cycle is 2 pi / (0.6662 * 4) = 2.36 blocks: the leg is where it was, and half a cycle on it is on the other side
        Assert.That(At(2.3576f), Is.EqualTo(At(0f)).Within(0.002f));
        Assert.That(At(0f) * At(1.1788f), Is.LessThan(0f));
        Assert.That(MathF.Abs(At(0.5f) - At(0f)), Is.GreaterThan(0.01f), "it moves as the creature walks");
    }

    [Test]
    public void TheSwingAngle_IsTheGames_AndItsAmountSaturates()
    {
        var model = EntityModels.Get("pig")!;
        float Dz(float speed)
        {
            var rest = LegAngle(Pose("pig", View(speed: 0f)), model, "right_front_leg");
            // walked = 0: cos = 1 for the right hind and left front legs, the front right leg is at the opposite phase: angle = -1.4 * amount
            return MathF.Abs(LegAngle(Pose("pig", View(speed: speed, walked: 0f)), model, "right_front_leg") - rest);
        }
        // The leg's box centre is 3 px below its pivot: it swings by 3 * sin(angle) px, angle = 1.4 * min(4 * blocks per tick, 1)
        Assert.That(Dz(0.1f), Is.EqualTo(3f * MathF.Sin(1.4f * 0.4f) / 16f).Within(0.003f));
        Assert.That(Dz(0.3f), Is.EqualTo(3f * MathF.Sin(1.4f) / 16f).Within(0.003f));
        Assert.That(Dz(2.0f), Is.EqualTo(Dz(0.3f)).Within(0.0005f), "a sprint does not swing further than the cap");
    }

    [Test]
    public void AVillager_WalksWithShorterStrides_ThanABiped()
    {
        float Dz(string type)
        {
            var model = EntityModels.Get(type)!;
            var rest = LegAngle(Pose(type, View(type, speed: 0f)), model, "right_leg");
            return MathF.Abs(LegAngle(Pose(type, View(type, speed: 0.1f, walked: 0f)), model, "right_leg") - rest);
        }
        // Both legs are 12 px, centre 6 px below the pivot: 6 * sin(angle); the villager's angle is half
        Assert.That(Dz("zombie"), Is.EqualTo(6f * MathF.Sin(1.4f * 0.4f) / 16f).Within(0.003f));
        Assert.That(Dz("villager"), Is.EqualTo(6f * MathF.Sin(0.7f * 0.4f) / 16f).Within(0.003f));
    }

    [Test]
    public void AFarCreature_IsDrawnAsHeadAndBodyOnly_InTheSamePlace()
    {
        foreach (var type in new[] { "pig", "cow", "sheep", "zombie", "villager", "player", "enderman" })
        {
            var model = EntityModels.Get(type)!;
            var full = new List<PartDraw>();
            var coarse = new List<PartDraw>();
            EntityPoser.Pose(View(type), model, 3f, full);
            EntityPoser.Pose(View(type), model, 3f, coarse, coarse: true);
            int expected = model.Parts.Where(p => p.Role is PartRole.Head or PartRole.Body && p.VariantTexture == null).Sum(p => p.Boxes.Length);   // a layer only exists for a variant that has it
            Assert.That(coarse.Count, Is.EqualTo(expected), type);
            Assert.That(coarse.Count, Is.LessThan(full.Count), type);
            // Each coarse box is one of the full pose's, unmoved
            foreach (var c in coarse)
                Assert.That(full.Any(f => f.Model == c.Model && f.Origin == c.Origin && f.U == c.U && f.V == c.V && f.Texture == c.Texture), Is.True, type);
        }
    }

    [Test]
    public void AOneBoxModel_IsNotReducedFurther()
    {
        var list = new List<PartDraw>();
        EntityPoser.Pose(View("ghast"), EntityModels.Fallback("ghast"), 0, list, coarse: true);
        Assert.That(list.Count, Is.EqualTo(1));
    }

    [Test]
    public void ALayer_MovesWithItsParent()
    {
        var model = EntityModels.Get("player")!;
        var draws = Pose("player", View("player", speed: 0.15f, walked: 2.2f, yaw: 30, body: 10), age: 7f);
        int IndexOf(string name) { int i = 0; foreach (var p in model.Parts) { if (p.Name == name) return i; i += p.Boxes.Length; } throw new Exception(name); }
        foreach (var (layer, parent) in new[] { ("hat", "head"), ("jacket", "body"), ("right_sleeve", "right_arm"), ("left_sleeve", "left_arm"), ("right_pants", "right_leg"), ("left_pants", "left_leg") })
            Assert.That(Vector3.Distance(Centre(draws[IndexOf(layer)]), Centre(draws[IndexOf(parent)])), Is.LessThan(0.002f), $"{layer} over {parent}");
    }

    [Test]
    public void ABiped_SwingsArmsAgainstLegs()
    {
        var model = EntityModels.Get("skeleton")!;
        var still = Pose("skeleton", View("skeleton", speed: 0, walked: 3f));
        var walk = Pose("skeleton", View("skeleton", speed: 0.1f, walked: 3f));
        float Moved(string part) => LegAngle(walk, model, part) - LegAngle(still, model, part);
        // Right arm with left leg (opposite the right leg)
        Assert.That(Moved("right_leg") * Moved("right_arm"), Is.LessThan(0f), "an arm swings against its own side's leg");
        Assert.That(Moved("right_leg") * Moved("left_leg"), Is.LessThan(0f));
    }

    [Test]
    public void AZombie_HoldsItsArmsOut()
    {
        var model = EntityModels.Get("zombie")!;
        var draws = Pose("zombie", View("zombie"));
        int idx = 0;
        foreach (var p in model.Parts) { if (p.Name == "right_arm") break; idx += p.Boxes.Length; }
        // The arm's box centre is well in front of the body (z = +) and not hanging straight down
        var arm = Centre(draws[idx]);
        Assert.That(arm.Z - 10f, Is.GreaterThan(0.2f), "the arm points forward (the creature faces +z at yaw 0)");
    }

    [Test]
    public void AChickenInTheAir_FlapsItsWings_OnTheGroundItDoesNot()
    {
        var model = EntityModels.Get("chicken")!;
        int idx = 0;
        foreach (var p in model.Parts) { if (p.Name == "right_wing") break; idx += p.Boxes.Length; }
        var ground = Centre(Pose("chicken", View("chicken"), age: 5f)[idx]);
        var ground2 = Centre(Pose("chicken", View("chicken"), age: 7f)[idx]);
        var air = Centre(Pose("chicken", View("chicken", flags: 0), age: 5f)[idx]);
        var air2 = Centre(Pose("chicken", View("chicken", flags: 0), age: 7f)[idx]);
        Assert.That(Vector3.Distance(ground, ground2), Is.LessThan(0.001f), "folded on the ground, whatever the time");
        Assert.That(Vector3.Distance(air, air2), Is.GreaterThan(0.02f), "flapping in the air");
    }

    // ---- the head ----

    [Test]
    public void TheHead_TurnsToWhereItLooks_RelativeToTheBody()
    {
        var straight = Pose("cow", View("cow", yaw: 0, body: 0));
        var looking = Pose("cow", View("cow", yaw: 40, body: 0));
        // The first box of the head part and of the body part, wherever the model lists them
        int Box(PartRole role) { int n = 0; foreach (var part in EntityModels.Get("cow")!.Parts) { if (part.Role == role) return n; n += part.Boxes.Length; } throw new Exception("no " + role); }
        int head = Box(PartRole.Head), body = Box(PartRole.Body);
        Assert.That(Vector3.Distance(Centre(straight[head]), Centre(looking[head])), Is.GreaterThan(0.05f), "the head turned");
        Assert.That(Vector3.Distance(Centre(straight[body]), Centre(looking[body])), Is.LessThan(0.001f), "the body did not");

        // Body and head turned together: the whole creature is the same, turned about the vertical through its place
        var turned = Pose("cow", View("cow", yaw: 40, body: 40));
        var about = Matrix4x4.CreateRotationY(-40f * MathF.PI / 180f);
        var pos = new Vector3(10, 64, 10);
        for (int i = 0; i < straight.Count; i++)
        {
            var expected = Vector3.Transform(Centre(straight[i]) - pos, about) + pos;
            Assert.That(Vector3.Distance(Centre(turned[i]), expected), Is.LessThan(0.002f), $"box {i}");
        }
    }

    [Test]
    public void TheHeadTurn_IsLimited_AndTakesTheShortWayRound()
    {
        Assert.That(EntityPoser.HeadYawRelative(170f, -170f), Is.EqualTo(-20f).Within(0.001f), "170 and -170 are 20 degrees apart, not 340");
        Assert.That(EntityPoser.HeadYawRelative(-170f, 170f), Is.EqualTo(20f).Within(0.001f));
        Assert.That(EntityPoser.HeadYawRelative(200f, 0f), Is.EqualTo(-75f).Within(0.001f), "no further than 75 either way");
        Assert.That(EntityPoser.HeadYawRelative(0f, 90f), Is.EqualTo(-75f).Within(0.001f));
        Assert.That(EntityPoser.HeadYawRelative(30f, 20f), Is.EqualTo(10f).Within(0.001f));
    }

    [Test]
    public void LookingUpAndDown_TiltsTheHead()
    {
        var level = Centre(Pose("pig", View(pitch: 0))[1]);   // the snout
        var down = Centre(Pose("pig", View(pitch: 60))[1]);
        var up = Centre(Pose("pig", View(pitch: -60))[1]);
        Assert.That(down.Y, Is.LessThan(level.Y - 0.05f), "pitch is positive looking down: the snout dips");
        Assert.That(up.Y, Is.GreaterThan(level.Y + 0.05f));
    }

    // ---- dress ----

    [Test]
    public void SheepWool_TakesTheDyeColour_TheBareBodyDoesNot()
    {
        var model = EntityModels.Get("sheep")!;
        var draws = Pose("sheep", View("sheep", variant: "pink"));
        int i = 0;
        foreach (var part in model.Parts)
            foreach (var _ in part.Boxes)
            {
                if (part.Dyed) Assert.That(draws[i].Tint, Is.EqualTo(EntityPoser.DyeColour("pink")), part.Name);
                else Assert.That(draws[i].Tint, Is.EqualTo(Vector3.One), part.Name);
                if (part.Dyed) Assert.That(draws[i].Texture, Is.EqualTo("sheep/sheep_wool"));
                i++;
            }
        Assert.That(EntityPoser.DyeColour("pink"), Is.Not.EqualTo(EntityPoser.DyeColour("white")));
        Assert.That(EntityPoser.DyeColour(null), Is.EqualTo(EntityPoser.DyeColour("white")));
        Assert.That(EntityPoser.DyeColour("not_a_dye"), Is.EqualTo(EntityPoser.DyeColour("white")));
    }

    [Test]
    public void APlayer_WearsItsSkin_AndOtherCreaturesKeepTheirOwnTexture()
    {
        var skin = Pose("player", View("player"), texture: "skin:abc");
        Assert.That(skin.All(d => d.Texture == "skin:abc"), Is.True);
        var pig = Pose("pig", View("pig"), texture: "skin:abc");
        Assert.That(pig.All(d => d.Texture == "pig/temperate_pig"), Is.True, "a skin applies to the player model only");
    }

    [Test]
    public void ASlimPlayer_HasNarrowerArms()
    {
        var wide = EntityModels.Get("player")!.Parts.First(p => p.Name == "right_arm").Boxes[0];
        var slim = EntityModels.Get("player_slim")!.Parts.First(p => p.Name == "right_arm").Boxes[0];
        Assert.That(wide.W, Is.EqualTo(4f));
        Assert.That(slim.W, Is.EqualTo(3f));
    }

    // ---- the models themselves ----

    [Test]
    public void EveryModel_IsWellFormed()
    {
        foreach (var type in new[] { "pig", "cow", "mooshroom", "sheep", "chicken", "creeper", "zombie", "husk", "drowned", "zombified_piglin", "skeleton", "stray", "bogged",
                     "wither_skeleton", "villager", "wolf", "spider", "cave_spider", "enderman", "player", "player_slim" })
        {
            var model = EntityModels.Get(type) ?? throw new Exception(type);
            Assert.That(model.Parts, Is.Not.Empty, type);
            Assert.That(model.Parts.Select(p => p.Name).Distinct().Count(), Is.EqualTo(model.Parts.Count), $"{type}: part names are unique");
            for (int i = 0; i < model.Parts.Count; i++)
            {
                var part = model.Parts[i];
                Assert.That(part.Parent, Is.LessThan(i), $"{type}/{part.Name}: a parent comes before its children");
                Assert.That(part.Boxes, Is.Not.Empty, $"{type}/{part.Name}");
                foreach (var b in part.Boxes)
                {
                    Assert.That(b.W > 0 && b.H > 0 && b.D > 0, Is.True, $"{type}/{part.Name}: a box with a size");
                    Assert.That(b.U >= 0 && b.V >= 0, Is.True, $"{type}/{part.Name}: texture offsets");
                    // The box's whole cross layout fits a 64 x 64 texture
                    Assert.That(b.U + 2 * b.D + 2 * b.W, Is.LessThanOrEqualTo(64.5f), $"{type}/{part.Name}: u extent");
                    Assert.That(b.V + b.D + b.H, Is.LessThanOrEqualTo(64.5f), $"{type}/{part.Name}: v extent");
                }
            }
        }
    }

    [Test]
    public void AWolfsCoat_FollowsItsVariant()
    {
        string TextureOf(string? variant) => Pose("wolf", View("wolf", variant: variant))[0].Texture;
        Assert.That(TextureOf(null), Is.EqualTo("wolf/wolf"));
        Assert.That(TextureOf("pale"), Is.EqualTo("wolf/wolf"));
        Assert.That(TextureOf("ashen"), Is.EqualTo("wolf/wolf_ashen"));
        Assert.That(EntityModels.TextureNames(), Does.Contain("wolf/wolf_ashen").And.Contain("wolf/wolf_woods"), "every coat is packed into the atlas");
    }

    [Test]
    public void AnAxolotl_AFrog_APanda_AndAShulker_WearTheirOwnColours()
    {
        string TextureOf(string type, string? variant) => Pose(type, View(type, variant: variant))[0].Texture;
        Assert.That(TextureOf("axolotl", "gold"), Is.EqualTo("axolotl/axolotl_gold"));
        Assert.That(TextureOf("axolotl", null), Is.EqualTo("axolotl/axolotl_lucy"));
        Assert.That(TextureOf("frog", "cold"), Is.EqualTo("frog/cold_frog"));
        Assert.That(TextureOf("frog", "warm"), Is.EqualTo("frog/warm_frog"));
        Assert.That(TextureOf("frog", "mymod:odd"), Is.EqualTo("frog/temperate_frog"));
        Assert.That(TextureOf("panda", "aggressive"), Is.EqualTo("panda/aggressive_panda"));
        Assert.That(TextureOf("panda", "normal"), Is.EqualTo("panda/panda"));
        Assert.That(TextureOf("shulker", "light_blue"), Is.EqualTo("shulker/shulker_light_blue"));
        Assert.That(TextureOf("shulker", null), Is.EqualTo("shulker/shulker"));
        var names = EntityModels.TextureNames().ToList();
        Assert.That(names, Does.Contain("axolotl/axolotl_blue").And.Contain("frog/warm_frog").And.Contain("panda/weak_panda").And.Contain("shulker/shulker_red"), "every colour is packed into the atlas");
    }

    [Test]
    public void ATypeWithNoModel_IsABoxOfItsRealSize_StandingOnTheGround()
    {
        Assert.That(EntityModels.Get("mymod:thing"), Is.Null, "a creature nobody has a model for");
        var (w, h) = EntityModels.SizeOf("ghast");
        Assert.That((w, h), Is.EqualTo((4f, 4f)));
        var model = EntityModels.Fallback("ghast");
        var list = new List<PartDraw>();
        EntityPoser.Pose(View("ghast", y: 50), model, 0, list);
        Assert.That(list.Count, Is.EqualTo(1));
        var (min, max) = VerticalExtent(list);
        Assert.That(min, Is.EqualTo(50f).Within(0.02f));
        Assert.That(max, Is.EqualTo(54f).Within(0.02f));
        Assert.That(EntityModels.SizeOf("mymod:thing"), Is.EqualTo((0.6f, 0.8f)), "a creature nobody has heard of still gets a size");
    }

    [Test]
    public void NamespacesAreIgnoredForVanilla()
    {
        Assert.That(EntityModels.Get("minecraft:cow"), Is.SameAs(EntityModels.Get("cow")));
        Assert.That(EntityModels.IsNotACreature("minecraft:arrow"), Is.True);
        Assert.That(EntityModels.IsNotACreature("arrow"), Is.True);
        Assert.That(EntityModels.IsNotACreature("cow"), Is.False);
        Assert.That(EntityModels.IsNotACreature("mymod:thing"), Is.False);
    }

    // ---- every model ----

    /// <summary>Kinds that do not stand on the ground, by how far their lowest box is from it (blocks, negative = below): they hover, or hang their tentacles.</summary>
    private static readonly Dictionary<string, (float min, float max)> Hovering = new()
    {
        ["ghast"] = (-3.5f, -2.4f), ["blaze"] = (0.1f, 0.5f),
        ["squid"] = (-1.3f, -1.0f), ["glow_squid"] = (-1.3f, -1.0f),   // the body is centred on the hitbox, the tentacles hang below it
        ["salmon"] = (-0.05f, 0.15f), ["tropical_fish_b"] = (-0.2f, 0.2f),   // swimmers: their bodies are a little above the hitbox's floor
        ["allay"] = (-0.15f, 0.1f), ["axolotl"] = (-0.15f, 0.05f), ["guardian"] = (-0.3f, 0.05f), ["elder_guardian"] = (-0.8f, 0.1f),   // the game's models reach a little below their hitbox
        ["breeze"] = (0.5f, 0.8f), ["wither"] = (0.2f, 0.6f), ["ender_dragon"] = (-3f, 0f), ["bat"] = (-0.3f, 0.7f), ["phantom"] = (-0.3f, 0.7f), ["vex"] = (-0.3f, 0.7f),
    };

    [Test]
    public void EveryModel_StandsOnTheGround_AndIsAboutAsTallAsItsHitbox()
    {
        var report = new List<string>();
        var problems = new List<string>();
        foreach (var type in EntityModels.ModelledTypes.Where(t => t is not ("player" or "player_slim")).OrderBy(t => t))
        {
            var model = EntityModels.Get(type)!;
            var draws = Pose(type, View(type, y: 64));
            var (minY, maxY) = VerticalExtent(draws);
            float feet = minY - 64f, top = maxY - 64f;
            report.Add($"{type}: feet {feet:F2}, top {top:F2}, hitbox {model.Height:F2}");
            if (Hovering.TryGetValue(type, out var range))
            {
                if (feet < range.min || feet > range.max) problems.Add($"{type} should hang between {range.min} and {range.max} below its feet; it is {feet:F2}");
            }
            else if (feet < -0.08f || feet > 0.08f) problems.Add($"{type}: its lowest box is {feet:F2} from the ground");
            // The model is as tall as the game's hitbox says, give or take (a head held high, ears, hats)
            if (type is not ("rabbit" or "pufferfish" or "tropical_fish_a" or "bat" or "ender_dragon") && (top < model.Height * 0.6f || top > model.Height * 1.45f + 0.25f)) problems.Add($"{type}: {top:F2} blocks tall against a hitbox of {model.Height:F2}");
        }
        TestContext.Progress.WriteLine(string.Join(Environment.NewLine, report));
        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void AHorse_WearsItsCoat_AndItsMarkingsOnlyWhereItHasThem()
    {
        var textures = (string? variant) => Pose("horse", View("horse", variant: variant)).Select(d => d.Texture).Distinct().ToList();
        Assert.That(textures("chestnut/white_dots"), Is.EquivalentTo(new[] { "horse/horse_chestnut", "horse/horse_markings_whitedots" }));
        Assert.That(textures("dark_brown/white_field"), Is.EquivalentTo(new[] { "horse/horse_darkbrown", "horse/horse_markings_whitefield" }));
        Assert.That(textures("black/none"), Is.EquivalentTo(new[] { "horse/horse_black" }), "no markings: no markings layer");
        Assert.That(textures("gray"), Is.EquivalentTo(new[] { "horse/horse_gray" }));
        Assert.That(textures(null), Is.EquivalentTo(new[] { "horse/horse_brown" }), "an unknown coat is the brown one");
    }

    [Test]
    public void AVillager_WearsItsTradesClothes_AndNoneWhenUnemployed()
    {
        var textures = (string? variant) => Pose("villager", View("villager", variant: variant)).Select(d => d.Texture).Distinct().ToList();
        Assert.That(textures("farmer"), Is.EquivalentTo(new[] { "villager/villager", "villager/profession/farmer" }));
        Assert.That(textures("none"), Is.EquivalentTo(new[] { "villager/villager" }));
        Assert.That(textures(null), Is.EquivalentTo(new[] { "villager/villager" }));
        var layers = Pose("villager", View("villager", variant: "mason"));
        Assert.That(layers.Count, Is.GreaterThan(Pose("villager", View("villager")).Count));
    }

    [Test]
    public void VariantsPickTheirTextures()
    {
        string Tex(string type, string? variant) => Pose(type, View(type, variant: variant))[0].Texture;
        Assert.That(Tex("cat", "siamese"), Is.EqualTo("cat/siamese"));
        Assert.That(Tex("cat", null), Is.EqualTo("cat/tabby"));
        Assert.That(Tex("cat", "something_new"), Is.EqualTo("cat/tabby"));
        Assert.That(Tex("rabbit", "black_and_white"), Is.EqualTo("rabbit/white_splotched"));
        Assert.That(Tex("rabbit", "white_splotched"), Is.EqualTo("rabbit/white_splotched"));
        Assert.That(Tex("fox", "snow"), Is.EqualTo("fox/snow_fox"));
        Assert.That(Tex("fox", "red"), Is.EqualTo("fox/fox"));
        Assert.That(Tex("llama", "gray"), Is.EqualTo("llama/gray"));
        Assert.That(Tex("trader_llama", "white"), Is.EqualTo("llama/white"));
        Assert.That(Tex("parrot", "yellow_blue"), Is.EqualTo("parrot/parrot_yellow_blue"));
        Assert.That(Tex("parrot", "cyan"), Is.EqualTo("parrot/parrot_yellow_blue"));
        Assert.That(Tex("mooshroom", "brown"), Is.EqualTo("cow/brown_mooshroom"));
        Assert.That(Tex("mooshroom", null), Is.EqualTo("cow/red_mooshroom"));
    }

    [Test]
    public void AGhast_IsDrawnAsBigAsTheGameDrawsIt()
    {
        // 16 px of body at 4.5 times: 4.5 blocks across
        var draws = Pose("ghast", View("ghast"));
        var body = draws[0];
        var a = ToWorld(body, body.Origin);
        var b = ToWorld(body, body.Origin + new Vector3(body.Size.X, 0, 0));
        Assert.That(Vector3.Distance(a, b), Is.EqualTo(4.5f).Within(0.01f));
    }
}
