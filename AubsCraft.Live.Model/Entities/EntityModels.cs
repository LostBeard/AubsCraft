using System.Numerics;

namespace AubsCraft.Live.Model.Entities;

/// <summary>
/// The models of the creatures the viewer draws, in the game's own model pixels (the numbers of its model classes), and the sizes of every vanilla
/// creature for the ones it only draws as a box. A creature with no model here is a coloured box of its real size (<see cref="Fallback"/>): a mod's creature,
/// a mob the viewer does not model yet - still seen, still moving, never missing.
/// </summary>
public static class EntityModels
{
    private static readonly Dictionary<string, EntityModel> Models = Build();

    /// <summary>The model of a creature type ("cow", "minecraft:cow" and "player" all work), or null when it has none.</summary>
    public static EntityModel? Get(string type, string? variant = null)
    {
        var key = Normalise(type);
        if (!Models.TryGetValue(key, out var model)) return null;
        return model;
    }

    /// <summary>The nine skins the game gives a player who has none of their own, each in a wide (Steve) and a slim (Alex) build.</summary>
    public static readonly string[] DefaultSkinNames = ["steve", "alex", "ari", "efe", "kai", "makena", "noor", "sunny", "zuri"];

    /// <summary>The texture of the default skin the game would give this player (by UUID, as it does), in the build they use.</summary>
    public static string DefaultSkinFor(string uuid, bool slim)
    {
        // A stable hash of the UUID's hex digits (the game uses the UUID's own hash; any even spread does the same job)
        int hash = 17;
        foreach (char c in uuid) if (c != '-') hash = unchecked(hash * 31 + c);
        int index = ((hash % DefaultSkinNames.Length) + DefaultSkinNames.Length) % DefaultSkinNames.Length;
        return $"player/{(slim ? "slim" : "wide")}/{DefaultSkinNames[index]}";
    }

    /// <summary>Every texture the models and the default skins name (paths under textures/entity, no extension), for the asset tool to pack. "white" is the plain tile of the box drawn when there is no model.</summary>
    public static IEnumerable<string> TextureNames()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal) { "white" };
        foreach (var model in Models.Values)
        {
            if (model.Texture != "player") names.Add(model.Texture);
            foreach (var t in model.VariantTextures) names.Add(t);
            foreach (var part in model.Parts) if (part.Texture != null) names.Add(part.Texture);
        }
        foreach (var skin in DefaultSkinNames) { names.Add($"player/wide/{skin}"); names.Add($"player/slim/{skin}"); }
        return names;
    }

    /// <summary>"minecraft:cow" and "cow" are the same type.</summary>
    public static string Normalise(string type) => type.StartsWith("minecraft:", StringComparison.Ordinal) ? type[10..] : type;

    /// <summary>The width and height in blocks of a creature type (the game's hitbox), for name tags and for the box drawn when there is no model.</summary>
    public static (float Width, float Height) SizeOf(string type)
    {
        var key = Normalise(type);
        if (Models.TryGetValue(key, out var m)) return (m.Width, m.Height);
        return Sizes.TryGetValue(key, out var s) ? s : (0.6f, 0.8f);
    }

    /// <summary>What is not a creature worth drawing: projectiles, items, markers, display entities, effects. (They are not in the frame unless the plugin is told to send them.)</summary>
    public static bool IsNotACreature(string type) => NotCreatures.Contains(Normalise(type));

    /// <summary>A model of one box the size of the creature: the picture for a creature with no model of its own.</summary>
    public static EntityModel Fallback(string type)
    {
        var (w, h) = SizeOf(type);
        // Boxes are in model px (16 per block); the box stands on the ground (y = 24 is the feet), centred on the entity
        float pw = w * 16f, ph = h * 16f;
        return new EntityModel
        {
            Texture = "white",
            Rig = EntityRig.Static,
            Width = w,
            Height = h,
            Parts = [new ModelPart("box", PartRole.Static, -1, Vector3.Zero, Vector3.Zero, [new ModelBox(0, 0, -pw / 2f, 24f - ph, -pw / 2f, pw, ph, pw)])],
        };
    }

    private static ModelPart P(string name, PartRole role, int parent, float px, float py, float pz, ModelBox[] boxes,
        float rx = 0f, float ry = 0f, float rz = 0f, string? texture = null, bool dyed = false)
        => new(name, role, parent, new Vector3(px, py, pz), new Vector3(rx, ry, rz), boxes, texture, dyed);

    private static ModelBox B(float u, float v, float x, float y, float z, float w, float h, float d, float inflate = 0f, bool mirror = false)
        => new(u, v, x, y, z, w, h, d, inflate, mirror);

    private const float HalfPi = MathF.PI / 2f;

    private static Dictionary<string, EntityModel> Build()
    {
        var d = new Dictionary<string, EntityModel>();

        // ---- the farm: the quadruped family (the body lies on its side: a part rotated a quarter turn about x) ----
        d["pig"] = new EntityModel
        {
            Texture = "pig/temperate_pig", Rig = EntityRig.Quadruped, Width = 0.9f, Height = 0.9f,
            Parts =
            [
                P("head", PartRole.Head, -1, 0, 12, -6, [B(0, 0, -4, -4, -8, 8, 8, 8), B(16, 16, -2, 0, -9, 4, 3, 1)]),
                P("body", PartRole.Body, -1, 0, 11, 2, [B(28, 8, -5, -10, -7, 10, 16, 8)], rx: HalfPi),
                P("right_hind_leg", PartRole.RightHindLeg, -1, -3, 18, 7, [B(0, 16, -2, 0, -2, 4, 6, 4)]),
                P("left_hind_leg", PartRole.LeftHindLeg, -1, 3, 18, 7, [B(0, 16, -2, 0, -2, 4, 6, 4)]),
                P("right_front_leg", PartRole.RightFrontLeg, -1, -3, 18, -5, [B(0, 16, -2, 0, -2, 4, 6, 4)]),
                P("left_front_leg", PartRole.LeftFrontLeg, -1, 3, 18, -5, [B(0, 16, -2, 0, -2, 4, 6, 4)]),
            ],
        };
        d["cow"] = new EntityModel
        {
            Texture = "cow/temperate_cow", Rig = EntityRig.Quadruped, Width = 0.9f, Height = 1.4f,
            Parts =
            [
                P("head", PartRole.Head, -1, 0, 4, -8, [B(0, 0, -4, -4, -6, 8, 8, 6), B(22, 0, -5, -5, -4, 1, 3, 1), B(22, 0, 4, -5, -4, 1, 3, 1)]),
                P("body", PartRole.Body, -1, 0, 5, 2, [B(18, 4, -6, -10, -7, 12, 18, 10), B(52, 0, -2, 2, -8, 4, 6, 1)], rx: HalfPi),
                P("right_hind_leg", PartRole.RightHindLeg, -1, -4, 12, 7, [B(0, 16, -2, 0, -2, 4, 12, 4)]),
                P("left_hind_leg", PartRole.LeftHindLeg, -1, 4, 12, 7, [B(0, 16, -2, 0, -2, 4, 12, 4)]),
                P("right_front_leg", PartRole.RightFrontLeg, -1, -4, 12, -6, [B(0, 16, -2, 0, -2, 4, 12, 4)]),
                P("left_front_leg", PartRole.LeftFrontLeg, -1, 4, 12, -6, [B(0, 16, -2, 0, -2, 4, 12, 4)]),
            ],
        };
        d["mooshroom"] = new EntityModel
        {
            Texture = "cow/red_mooshroom", Rig = d["cow"].Rig, Width = 0.9f, Height = 1.4f, Parts = d["cow"].Parts,
        };
        d["sheep"] = new EntityModel
        {
            Texture = "sheep/sheep", Rig = EntityRig.Quadruped, Width = 0.9f, Height = 1.3f,
            Parts =
            [
                P("head", PartRole.Head, -1, 0, 6, -8, [B(0, 0, -3, -4, -6, 6, 6, 8)]),
                P("wool_head", PartRole.Head, -1, 0, 6, -8, [B(0, 0, -3, -4, -4, 6, 6, 6, inflate: 0.6f)], texture: "sheep/sheep_wool", dyed: true),
                P("body", PartRole.Body, -1, 0, 5, 2, [B(28, 8, -4, -10, -7, 8, 16, 6)], rx: HalfPi),
                P("wool_body", PartRole.Body, -1, 0, 5, 2, [B(28, 8, -4, -10, -7, 8, 16, 6, inflate: 1.75f)], rx: HalfPi, texture: "sheep/sheep_wool", dyed: true),
                P("right_hind_leg", PartRole.RightHindLeg, -1, -3, 12, 7, [B(0, 16, -2, 0, -2, 4, 12, 4)]),
                P("left_hind_leg", PartRole.LeftHindLeg, -1, 3, 12, 7, [B(0, 16, -2, 0, -2, 4, 12, 4)]),
                P("right_front_leg", PartRole.RightFrontLeg, -1, -3, 12, -5, [B(0, 16, -2, 0, -2, 4, 12, 4)]),
                P("left_front_leg", PartRole.LeftFrontLeg, -1, 3, 12, -5, [B(0, 16, -2, 0, -2, 4, 12, 4)]),
                P("wool_right_hind_leg", PartRole.RightHindLeg, -1, -3, 12, 7, [B(0, 16, -2, 0, -2, 4, 6, 4, inflate: 0.5f)], texture: "sheep/sheep_wool", dyed: true),
                P("wool_left_hind_leg", PartRole.LeftHindLeg, -1, 3, 12, 7, [B(0, 16, -2, 0, -2, 4, 6, 4, inflate: 0.5f)], texture: "sheep/sheep_wool", dyed: true),
                P("wool_right_front_leg", PartRole.RightFrontLeg, -1, -3, 12, -5, [B(0, 16, -2, 0, -2, 4, 6, 4, inflate: 0.5f)], texture: "sheep/sheep_wool", dyed: true),
                P("wool_left_front_leg", PartRole.LeftFrontLeg, -1, 3, 12, -5, [B(0, 16, -2, 0, -2, 4, 6, 4, inflate: 0.5f)], texture: "sheep/sheep_wool", dyed: true),
            ],
        };
        d["chicken"] = new EntityModel
        {
            Texture = "chicken/temperate_chicken", Rig = EntityRig.Chicken, Width = 0.4f, Height = 0.7f,
            Parts =
            [
                P("head", PartRole.Head, -1, 0, 15, -4, [B(0, 0, -2, -6, -2, 4, 6, 3), B(14, 0, -2, -4, -4, 4, 2, 2), B(14, 4, -1, -2, -3, 2, 2, 2)]),
                P("body", PartRole.Body, -1, 0, 16, 0, [B(0, 9, -3, -4, -3, 6, 8, 6)], rx: HalfPi),
                P("right_leg", PartRole.RightLeg, -1, -2, 19, 1, [B(26, 0, -1, 0, -3, 3, 5, 3)]),
                P("left_leg", PartRole.LeftLeg, -1, 1, 19, 1, [B(26, 0, -1, 0, -3, 3, 5, 3)]),
                P("right_wing", PartRole.RightWing, -1, -4, 13, 0, [B(24, 13, 0, 0, -3, 1, 4, 6)]),
                P("left_wing", PartRole.LeftWing, -1, 4, 13, 0, [B(24, 13, -1, 0, -3, 1, 4, 6)]),
            ],
        };

        // ---- monsters ----
        d["creeper"] = new EntityModel
        {
            Texture = "creeper/creeper", Rig = EntityRig.Quadruped, Width = 0.6f, Height = 1.7f,
            Parts =
            [
                P("head", PartRole.Head, -1, 0, 6, 0, [B(0, 0, -4, -8, -4, 8, 8, 8)]),
                P("body", PartRole.Body, -1, 0, 6, 0, [B(16, 16, -4, 0, -2, 8, 12, 4)]),
                P("right_hind_leg", PartRole.RightHindLeg, -1, -2, 18, 4, [B(0, 16, -2, 0, -2, 4, 6, 4)]),
                P("left_hind_leg", PartRole.LeftHindLeg, -1, 2, 18, 4, [B(0, 16, -2, 0, -2, 4, 6, 4)]),
                P("right_front_leg", PartRole.RightFrontLeg, -1, -2, 18, -4, [B(0, 16, -2, 0, -2, 4, 6, 4)]),
                P("left_front_leg", PartRole.LeftFrontLeg, -1, 2, 18, -4, [B(0, 16, -2, 0, -2, 4, 6, 4)]),
            ],
        };
        d["zombie"] = Humanoid("zombie/zombie", armsForward: true);
        d["husk"] = Humanoid("zombie/husk", armsForward: true);
        d["drowned"] = Humanoid("zombie/drowned", armsForward: true);
        d["zombified_piglin"] = Humanoid("piglin/zombified_piglin", armsForward: true);
        d["skeleton"] = Skeletal("skeleton/skeleton", 1f);
        d["stray"] = Skeletal("skeleton/stray", 1f);
        d["bogged"] = Skeletal("skeleton/bogged", 1f);
        d["wither_skeleton"] = Skeletal("skeleton/wither_skeleton", 1.2f);

        // ---- the wild and the household ----
        string[] wolfCoats = ["pale", "ashen", "black", "chestnut", "rusty", "snowy", "spotted", "striped", "woods"];
        d["wolf"] = new EntityModel
        {
            Texture = "wolf/wolf", Rig = EntityRig.Quadruped, Width = 0.6f, Height = 0.85f,
            TextureByVariant = v => v is null or "pale" ? "wolf/wolf" : $"wolf/wolf_{v}",
            VariantTextures = wolfCoats.Where(c => c != "pale").Select(c => $"wolf/wolf_{c}").ToArray(),
            Parts =
            [
                P("head", PartRole.Head, -1, -1, 13.5f, -7, [B(0, 0, -2, -3, -2, 6, 6, 4), B(16, 14, -2, -5, 0, 2, 2, 1), B(16, 14, 2, -5, 0, 2, 2, 1), B(0, 10, -0.5f, 0, -5, 3, 3, 4)]),
                P("body", PartRole.Body, -1, 0, 14, 2, [B(18, 14, -3, -2, -3, 6, 9, 6)], rx: HalfPi),
                P("mane", PartRole.Body, -1, -1, 14, -3, [B(21, 0, -3, -3, -3, 8, 6, 7)], rx: HalfPi),
                P("right_hind_leg", PartRole.RightHindLeg, -1, -2.5f, 16, 7, [B(0, 18, 0, 0, -1, 2, 8, 2)]),
                P("left_hind_leg", PartRole.LeftHindLeg, -1, 0.5f, 16, 7, [B(0, 18, 0, 0, -1, 2, 8, 2)]),
                P("right_front_leg", PartRole.RightFrontLeg, -1, -2.5f, 16, -4, [B(0, 18, 0, 0, -1, 2, 8, 2)]),
                P("left_front_leg", PartRole.LeftFrontLeg, -1, 0.5f, 16, -4, [B(0, 18, 0, 0, -1, 2, 8, 2)]),
                P("tail", PartRole.Static, -1, -1, 12, 8, [B(9, 18, 0, 0, -1, 2, 8, 2)], rx: 0.62f),
            ],
        };
        d["spider"] = SpiderModel("spider/spider", 1.4f, 0.9f, 1f);
        d["cave_spider"] = SpiderModel("spider/cave_spider", 0.7f, 0.5f, 0.7f);
        d["enderman"] = new EntityModel
        {
            Texture = "enderman/enderman", Rig = EntityRig.Biped, Width = 0.6f, Height = 2.9f,
            Parts =
            [
                P("head", PartRole.Head, -1, 0, -14, 0, [B(0, 0, -4, -8, -4, 8, 8, 8)]),
                P("hat", PartRole.Static, 0, 0, 0, 0, [B(0, 16, -4, -8, -4, 8, 8, 8, inflate: -0.5f)]),
                P("body", PartRole.Body, -1, 0, -14, 0, [B(32, 16, -4, 0, -2, 8, 12, 4)]),
                P("right_arm", PartRole.RightArm, -1, -3, -12, 0, [B(56, 0, -1, -2, -1, 2, 30, 2)]),
                P("left_arm", PartRole.LeftArm, -1, 5, -12, 0, [B(56, 0, -1, -2, -1, 2, 30, 2, mirror: true)]),
                P("right_leg", PartRole.RightLeg, -1, -2, -6, 0, [B(56, 0, -1, 0, -1, 2, 30, 2)]),
                P("left_leg", PartRole.LeftLeg, -1, 2, -6, 0, [B(56, 0, -1, 0, -1, 2, 30, 2, mirror: true)]),
            ],
        };

        // ---- people ----
        d["villager"] = new EntityModel
        {
            Texture = "villager/villager", Rig = EntityRig.Villager, Width = 0.6f, Height = 1.95f,
            Parts =
            [
                P("head", PartRole.Head, -1, 0, 0, 0, [B(0, 0, -4, -10, -4, 8, 10, 8)]),
                P("nose", PartRole.Static, 0, 0, -2, 0, [B(24, 0, -1, -1, -6, 2, 4, 2)]),
                P("body", PartRole.Body, -1, 0, 0, 0, [B(16, 20, -4, 0, -3, 8, 12, 6)]),
                P("robe", PartRole.Static, 2, 0, 0, 0, [B(0, 38, -4, 0, -3, 8, 18, 6, inflate: 0.5f)]),
                P("arms", PartRole.Static, -1, 0, 3, -1, [B(44, 22, -8, -2, -2, 4, 8, 4), B(44, 22, 4, -2, -2, 4, 8, 4, mirror: true), B(40, 38, -4, 2, -2, 8, 4, 4)], rx: -0.75f),
                P("right_leg", PartRole.RightLeg, -1, -2, 12, 0, [B(0, 22, -2, 0, -2, 4, 12, 4)]),
                P("left_leg", PartRole.LeftLeg, -1, 2, 12, 0, [B(0, 22, -2, 0, -2, 4, 12, 4, mirror: true)]),
            ],
        };
        d["player"] = Player(slim: false);
        d["player_slim"] = Player(slim: true);
        return d;
    }

    /// <summary>A spider: head, neck, body and eight legs splayed at the game's angles (the legs do not walk yet).</summary>
    private static EntityModel SpiderModel(string texture, float width, float height, float scale)
    {
        const float q = MathF.PI / 4f, e = MathF.PI / 8f;
        ModelPart Leg(string name, float px, float pz, float rz, float ry, bool right) =>
            P(name, PartRole.Static, -1, px, 15, pz, [right ? B(18, 0, -15, -1, -1, 16, 2, 2) : B(18, 0, -1, -1, -1, 16, 2, 2)], ry: ry, rz: rz);
        return new EntityModel
        {
            Texture = texture, Rig = EntityRig.Static, Width = width, Height = height, BabyScale = 0.5f,
            Parts =
            [
                P("head", PartRole.Head, -1, 0, 15, -3, [B(32, 4, -4, -4, -8, 8, 8, 8)]),
                P("neck", PartRole.Static, -1, 0, 15, 0, [B(0, 0, -3, -3, -3, 6, 6, 6)]),
                P("body", PartRole.Static, -1, 0, 15, 9, [B(0, 12, -5, -4, -6, 10, 8, 12)]),
                Leg("leg0", -4, 2, -q, q, true), Leg("leg1", 4, 2, q, -q, false),
                Leg("leg2", -4, 1, -0.58f, e, true), Leg("leg3", 4, 1, 0.58f, -e, false),
                Leg("leg4", -4, 0, -0.58f, -e, true), Leg("leg5", 4, 0, 0.58f, e, false),
                Leg("leg6", -4, -1, -q, -q, true), Leg("leg7", 4, -1, q, q, false),
            ],
        };
    }

    /// <summary>The humanoid model of the zombie family: 64 x 64 texture, the left limbs the mirror of the right.</summary>
    private static EntityModel Humanoid(string texture, bool armsForward) => new()
    {
        Texture = texture, Rig = EntityRig.Biped, Width = 0.6f, Height = 1.95f, ArmsForward = armsForward,
        Parts =
        [
            P("head", PartRole.Head, -1, 0, 0, 0, [B(0, 0, -4, -8, -4, 8, 8, 8)]),
            P("hat", PartRole.Static, 0, 0, 0, 0, [B(32, 0, -4, -8, -4, 8, 8, 8, inflate: 0.5f)]),
            P("body", PartRole.Body, -1, 0, 0, 0, [B(16, 16, -4, 0, -2, 8, 12, 4)]),
            P("right_arm", PartRole.RightArm, -1, -5, 2, 0, [B(40, 16, -3, -2, -2, 4, 12, 4)]),
            P("left_arm", PartRole.LeftArm, -1, 5, 2, 0, [B(40, 16, -1, -2, -2, 4, 12, 4, mirror: true)]),
            P("right_leg", PartRole.RightLeg, -1, -1.9f, 12, 0, [B(0, 16, -2, 0, -2, 4, 12, 4)]),
            P("left_leg", PartRole.LeftLeg, -1, 1.9f, 12, 0, [B(0, 16, -2, 0, -2, 4, 12, 4, mirror: true)]),
        ],
    };

    /// <summary>The skeleton family: thin limbs (2 px) on a 64 x 32 texture.</summary>
    private static EntityModel Skeletal(string texture, float scale) => new()
    {
        Texture = texture, Rig = EntityRig.Biped, Width = 0.6f * scale, Height = 1.99f * scale, BabyScale = 0.5f,
        Parts =
        [
            P("head", PartRole.Head, -1, 0, 0, 0, [B(0, 0, -4, -8, -4, 8, 8, 8)]),
            P("body", PartRole.Body, -1, 0, 0, 0, [B(16, 16, -4, 0, -2, 8, 12, 4)]),
            P("right_arm", PartRole.RightArm, -1, -5, 2, 0, [B(40, 16, -1, -2, -1, 2, 12, 2)]),
            P("left_arm", PartRole.LeftArm, -1, 5, 2, 0, [B(40, 16, -1, -2, -1, 2, 12, 2, mirror: true)]),
            P("right_leg", PartRole.RightLeg, -1, -2, 12, 0, [B(0, 16, -1, 0, -1, 2, 12, 2)]),
            P("left_leg", PartRole.LeftLeg, -1, 2, 12, 0, [B(0, 16, -1, 0, -1, 2, 12, 2, mirror: true)]),
        ],
    };

    /// <summary>The player: a 64 x 64 skin, separate left limb pixels, an outer layer (hat, jacket, sleeves, pants) over each part. Slim arms are 3 px wide.</summary>
    private static EntityModel Player(bool slim)
    {
        float aw = slim ? 3f : 4f;
        float ax = slim ? -2f : -3f;   // the right arm's minimum x
        float lx = -1f;               // the left arm's minimum x
        float py = slim ? 2.5f : 2f;
        return new EntityModel
        {
            Texture = "player", Rig = EntityRig.Biped, Width = 0.6f, Height = 1.8f,
            Parts =
            [
                P("head", PartRole.Head, -1, 0, 0, 0, [B(0, 0, -4, -8, -4, 8, 8, 8)]),
                P("hat", PartRole.Static, 0, 0, 0, 0, [B(32, 0, -4, -8, -4, 8, 8, 8, inflate: 0.5f)]),
                P("body", PartRole.Body, -1, 0, 0, 0, [B(16, 16, -4, 0, -2, 8, 12, 4)]),
                P("jacket", PartRole.Static, 2, 0, 0, 0, [B(16, 32, -4, 0, -2, 8, 12, 4, inflate: 0.25f)]),
                P("right_arm", PartRole.RightArm, -1, -5, py, 0, [B(40, 16, ax, -2, -2, aw, 12, 4)]),
                P("right_sleeve", PartRole.Static, 4, 0, 0, 0, [B(40, 32, ax, -2, -2, aw, 12, 4, inflate: 0.25f)]),
                P("left_arm", PartRole.LeftArm, -1, 5, py, 0, [B(32, 48, lx, -2, -2, aw, 12, 4)]),
                P("left_sleeve", PartRole.Static, 6, 0, 0, 0, [B(48, 48, lx, -2, -2, aw, 12, 4, inflate: 0.25f)]),
                P("right_leg", PartRole.RightLeg, -1, -1.9f, 12, 0, [B(0, 16, -2, 0, -2, 4, 12, 4)]),
                P("right_pants", PartRole.Static, 8, 0, 0, 0, [B(0, 32, -2, 0, -2, 4, 12, 4, inflate: 0.25f)]),
                P("left_leg", PartRole.LeftLeg, -1, 1.9f, 12, 0, [B(16, 48, -2, 0, -2, 4, 12, 4)]),
                P("left_pants", PartRole.Static, 10, 0, 0, 0, [B(0, 48, -2, 0, -2, 4, 12, 4, inflate: 0.25f)]),
            ],
        };
    }

    // The game's hitbox sizes (width, height in blocks), for name tags and the box drawn when there is no model
    private static readonly Dictionary<string, (float, float)> Sizes = new()
    {
        ["allay"] = (0.35f, 0.6f), ["armadillo"] = (0.7f, 0.65f), ["axolotl"] = (0.75f, 0.42f), ["bat"] = (0.5f, 0.9f), ["bee"] = (0.7f, 0.6f),
        ["blaze"] = (0.6f, 1.8f), ["bogged"] = (0.6f, 1.99f), ["breeze"] = (0.6f, 1.77f), ["camel"] = (1.7f, 2.375f), ["cat"] = (0.6f, 0.7f),
        ["cave_spider"] = (0.7f, 0.5f), ["cod"] = (0.5f, 0.3f), ["dolphin"] = (0.9f, 0.6f), ["donkey"] = (1.4f, 1.5f), ["elder_guardian"] = (1.9975f, 1.9975f),
        ["ender_dragon"] = (16f, 8f), ["enderman"] = (0.6f, 2.9f), ["endermite"] = (0.4f, 0.3f), ["evoker"] = (0.6f, 1.95f), ["fox"] = (0.6f, 0.7f),
        ["frog"] = (0.5f, 0.5f), ["ghast"] = (4f, 4f), ["giant"] = (3.6f, 11.7f), ["glow_squid"] = (0.8f, 0.8f), ["goat"] = (0.9f, 1.3f),
        ["guardian"] = (0.85f, 0.85f), ["hoglin"] = (1.396f, 1.4f), ["horse"] = (1.396f, 1.6f), ["illusioner"] = (0.6f, 1.95f), ["iron_golem"] = (1.4f, 2.7f),
        ["llama"] = (0.9f, 1.87f), ["magma_cube"] = (0.52f, 0.52f), ["mule"] = (1.396f, 1.6f), ["ocelot"] = (0.6f, 0.7f), ["panda"] = (1.3f, 1.25f),
        ["parrot"] = (0.5f, 0.9f), ["phantom"] = (0.9f, 0.5f), ["piglin"] = (0.6f, 1.95f), ["piglin_brute"] = (0.6f, 1.95f), ["pillager"] = (0.6f, 1.95f),
        ["polar_bear"] = (1.4f, 1.4f), ["pufferfish"] = (0.7f, 0.7f), ["rabbit"] = (0.4f, 0.5f), ["ravager"] = (1.95f, 2.2f), ["salmon"] = (0.7f, 0.4f),
        ["shulker"] = (1f, 1f), ["silverfish"] = (0.4f, 0.3f), ["skeleton_horse"] = (1.396f, 1.6f), ["slime"] = (0.52f, 0.52f), ["sniffer"] = (1.9f, 1.75f),
        ["snow_golem"] = (0.7f, 1.9f), ["spider"] = (1.4f, 0.9f), ["squid"] = (0.8f, 0.8f), ["strider"] = (0.9f, 1.7f), ["tadpole"] = (0.4f, 0.3f),
        ["trader_llama"] = (0.9f, 1.87f), ["tropical_fish"] = (0.5f, 0.4f), ["turtle"] = (1.2f, 0.4f), ["vex"] = (0.4f, 0.8f), ["vindicator"] = (0.6f, 1.95f),
        ["wandering_trader"] = (0.6f, 1.95f), ["warden"] = (0.9f, 2.9f), ["witch"] = (0.6f, 1.95f), ["wither"] = (0.9f, 3.5f), ["wolf"] = (0.6f, 0.85f),
        ["zoglin"] = (1.396f, 1.4f), ["zombie_horse"] = (1.396f, 1.6f), ["zombie_villager"] = (0.6f, 1.95f), ["creaking"] = (0.9f, 2.7f),
    };

    private static readonly HashSet<string> NotCreatures =
    [
        "item", "experience_orb", "arrow", "spectral_arrow", "trident", "snowball", "egg", "ender_pearl", "eye_of_ender", "fireball", "small_fireball",
        "wither_skull", "dragon_fireball", "potion", "experience_bottle", "firework_rocket", "fishing_bobber", "text_display", "block_display", "item_display",
        "interaction", "marker", "area_effect_cloud", "lightning_bolt", "painting", "item_frame", "glow_item_frame", "leash_knot", "falling_block", "tnt",
        "evoker_fangs", "shulker_bullet", "llama_spit", "wind_charge", "breeze_wind_charge", "ominous_item_spawner",
    ];
}
