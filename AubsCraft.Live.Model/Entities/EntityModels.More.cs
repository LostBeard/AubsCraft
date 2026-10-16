using System.Numerics;

namespace AubsCraft.Live.Model.Entities;

/// <summary>
/// The rest of the vanilla creatures that have a model here: the numbers are the game's own model classes (model pixels, y down, the feet at y = 24), as the
/// first batch in <c>EntityModels</c>. What a creature varies by (a horse's coat, a cat's breed, a villager's trade) comes from the live frame's variant string.
/// </summary>
public static partial class EntityModels
{
    private const float Quarter = MathF.PI / 4f;

    // ---- shared pieces ----

    /// <summary>The villager's model, which the illagers, the witch and the wandering trader share (in a different skin).</summary>
    private static ModelPart[] VillagerParts() =>
    [
        P("head", PartRole.Head, -1, 0, 0, 0, [B(0, 0, -4, -10, -4, 8, 10, 8)]),
        P("nose", PartRole.Static, 0, 0, -2, 0, [B(24, 0, -1, -1, -6, 2, 4, 2)]),
        P("body", PartRole.Body, -1, 0, 0, 0, [B(16, 20, -4, 0, -3, 8, 12, 6)]),
        P("robe", PartRole.Static, 2, 0, 0, 0, [B(0, 38, -4, 0, -3, 8, 18, 6, inflate: 0.5f)]),
        P("arms", PartRole.Static, -1, 0, 3, -1, [B(44, 22, -8, -2, -2, 4, 8, 4), B(44, 22, 4, -2, -2, 4, 8, 4, mirror: true), B(40, 38, -4, 2, -2, 8, 4, 4)], rx: -0.75f),
        P("right_leg", PartRole.RightLeg, -1, -2, 12, 0, [B(0, 22, -2, 0, -2, 4, 12, 4)]),
        P("left_leg", PartRole.LeftLeg, -1, 2, 12, 0, [B(0, 22, -2, 0, -2, 4, 12, 4, mirror: true)]),
    ];

    /// <summary>The same parts again as a layer on top (a profession's clothes): another texture, a hair's breadth bigger so it does not fight the skin underneath.</summary>
    private static IEnumerable<ModelPart> Layer(IReadOnlyList<ModelPart> parts, int firstIndex, Func<string?, string?> texture, IReadOnlyList<string> names, string suffix)
    {
        foreach (var p in parts)
            yield return p with
            {
                Name = p.Name + suffix,
                Parent = p.Parent < 0 ? -1 : p.Parent + firstIndex,
                Boxes = p.Boxes.Select(b => b with { Inflate = b.Inflate + 0.02f }).ToArray(),
                VariantTexture = texture,
                VariantTextureNames = names,
            };
    }

    // Their own class: the fields of a partial class are initialised in no promised order across files, and Build() (a field initialiser in the other file) needs these
    private static class Lists
    {
        public static readonly string[] Professions =
            ["armorer", "butcher", "cartographer", "cleric", "farmer", "fisherman", "fletcher", "leatherworker", "librarian", "mason", "nitwit", "shepherd", "toolsmith", "weaponsmith"];
        public static readonly string[] HorseColours = ["white", "creamy", "chestnut", "brown", "black", "gray", "darkbrown"];
        public static readonly string[] HorseMarkings = ["white", "whitefield", "whitedots", "blackdots"];
        public static readonly string[] TropicalPatternsA = ["kob", "sunstreak", "snooper", "dasher", "brinely", "spotty"];
        public static readonly string[] TropicalPatternsB = ["flopper", "stripey", "glitter", "blockfish", "betty", "clayfish"];
    }

    private static EntityModel VillagerModel(string texture, string professionFolder, float width, float height, IEnumerable<ModelPart>? extra = null, bool withProfession = false)
    {
        var parts = new List<ModelPart>(VillagerParts());
        if (extra != null) parts.AddRange(extra);
        if (withProfession)
        {
            string? Pick(string? v) => v != null && Array.IndexOf(Lists.Professions, v) >= 0 ? $"{professionFolder}/{v}" : null;
            parts.AddRange(Layer(VillagerParts(), parts.Count, Pick, Lists.Professions.Select(p => $"{professionFolder}/{p}").ToArray(), "_profession"));
        }
        return new EntityModel { Texture = texture, Rig = EntityRig.Villager, Width = width, Height = height, Parts = parts };
    }

    /// <summary>A humanoid with a head of its own (the piglins, the zombie villager): arms and legs as the zombie's.</summary>
    private static EntityModel HumanoidWith(string texture, ModelPart[] head, bool armsForward, float height = 1.95f)
    {
        var parts = new List<ModelPart>(head)
        {
            P("body", PartRole.Body, -1, 0, 0, 0, [B(16, 16, -4, 0, -2, 8, 12, 4)]),
            P("right_arm", PartRole.RightArm, -1, -5, 2, 0, [B(40, 16, -3, -2, -2, 4, 12, 4)]),
            P("left_arm", PartRole.LeftArm, -1, 5, 2, 0, [B(40, 16, -1, -2, -2, 4, 12, 4, mirror: true)]),
            P("right_leg", PartRole.RightLeg, -1, -1.9f, 12, 0, [B(0, 16, -2, 0, -2, 4, 12, 4)]),
            P("left_leg", PartRole.LeftLeg, -1, 1.9f, 12, 0, [B(0, 16, -2, 0, -2, 4, 12, 4, mirror: true)]),
        };
        return new EntityModel { Texture = texture, Rig = EntityRig.Biped, Width = 0.6f, Height = height, ArmsForward = armsForward, Parts = parts };
    }

    private static string Without(string? v, string chars) => v == null ? "" : v.Replace(chars, "");

    private static void AddMore(Dictionary<string, EntityModel> d)
    {
        // ---- villagers, illagers, the witch, the trader ----
        d["villager"] = VillagerModel("villager/villager", "villager/profession", 0.6f, 1.95f, withProfession: true);
        foreach (var illager in new[] { "vindicator", "evoker", "illusioner" })
            d[illager] = VillagerModel($"illager/{illager}", "", 0.6f, 1.95f);
        {
            // The pillager always has a crossbow in its hands: its arms are two, not crossed (and its texture has no crossed pair)
            var m = VillagerModel("illager/pillager", "", 0.6f, 1.95f);
            var parts = m.Parts.Where(p => p.Name != "arms").ToList();
            parts.Add(P("right_arm", PartRole.RightArm, -1, -5, 2, 0, [B(40, 46, -3, -2, -2, 4, 12, 4)]));
            parts.Add(P("left_arm", PartRole.LeftArm, -1, 5, 2, 0, [B(40, 46, -1, -2, -2, 4, 12, 4, mirror: true)]));
            d["pillager"] = new EntityModel { Texture = m.Texture, Rig = m.Rig, Width = m.Width, Height = m.Height, Parts = parts };
        }
        d["wandering_trader"] = VillagerModel("wandering_trader", "", 0.6f, 1.95f);
        d["witch"] = VillagerModel("witch", "", 0.6f, 1.95f, extra:
        [
            // The hat: a brim and three cones, each leaning a little more than the one under it (the game's own numbers)
            P("hat", PartRole.Static, 0, -5, -10.03125f, -5, [B(0, 64, 0, 0, 0, 10, 2, 10)]),
            P("hat2", PartRole.Static, 7, 1.75f, -4, 2, [B(0, 76, 0, 0, 0, 7, 4, 7)], rx: -0.05236f, rz: 0.02618f),
            P("hat3", PartRole.Static, 8, 1.75f, -4, 2, [B(0, 87, 0, 0, 0, 4, 4, 4)], rx: -0.10472f, rz: 0.05236f),
            P("hat4", PartRole.Static, 9, 1.75f, -2, 2, [B(0, 95, 0, 0, 0, 1, 2, 1, inflate: 0.25f)], rx: -0.20944f, rz: 0.10472f),
            P("mole", PartRole.Static, 0, 0, -2, 0, [B(0, 0, 0, 3, -6.75f, 1, 1, 1, inflate: -0.25f)]),
        ]);

        // The zombie villager: a villager's head and body, a zombie's arms held out
        {
            var parts = new List<ModelPart>
            {
                P("head", PartRole.Head, -1, 0, 0, 0, [B(0, 0, -4, -10, -4, 8, 10, 8)]),
                P("nose", PartRole.Static, 0, 0, -2, 0, [B(24, 0, -1, -1, -6, 2, 4, 2)]),
                P("body", PartRole.Body, -1, 0, 0, 0, [B(16, 20, -4, 0, -3, 8, 12, 6)]),
                P("robe", PartRole.Static, 2, 0, 0, 0, [B(0, 38, -4, 0, -3, 8, 18, 6, inflate: 0.5f)]),
                P("right_arm", PartRole.RightArm, -1, -5, 2, 0, [B(44, 22, -3, -2, -2, 4, 12, 4)]),
                P("left_arm", PartRole.LeftArm, -1, 5, 2, 0, [B(44, 22, -1, -2, -2, 4, 12, 4, mirror: true)]),
                P("right_leg", PartRole.RightLeg, -1, -2, 12, 0, [B(0, 22, -2, 0, -2, 4, 12, 4)]),
                P("left_leg", PartRole.LeftLeg, -1, 2, 12, 0, [B(0, 22, -2, 0, -2, 4, 12, 4, mirror: true)]),
            };
            string? Pick(string? v) => v != null && Array.IndexOf(Lists.Professions, v) >= 0 ? $"zombie_villager/profession/{v}" : null;
            parts.AddRange(Layer(parts.ToArray(), parts.Count, Pick, Lists.Professions.Select(p => $"zombie_villager/profession/{p}").ToArray(), "_profession"));
            d["zombie_villager"] = new EntityModel { Texture = "zombie_villager/zombie_villager", Rig = EntityRig.Biped, Width = 0.6f, Height = 1.95f, ArmsForward = true, Parts = parts };
        }

        // ---- piglins ----
        ModelPart[] PiglinHead(bool bigTusks) =>
        [
            P("head", PartRole.Head, -1, 0, 0, 0, [B(0, 0, -5, -8, -4, 10, 8, 8), B(31, 1, -2, -4, -5, 4, 4, 1), B(2, 4, -3, -2, -5, 1, 2, 1), B(2, 0, 2, -2, -5, 1, 2, 1)]),
            P("right_ear", PartRole.Static, 0, -4.5f, -6, 0, [B(51, 6, -1, 0, -2, 1, 5, 4)], rz: 0.5236f),
            P("left_ear", PartRole.Static, 0, 4.5f, -6, 0, [B(39, 6, 0, 0, -2, 1, 5, 4)], rz: -0.5236f),
        ];
        d["piglin"] = HumanoidWith("piglin/piglin", PiglinHead(false), armsForward: false);
        d["piglin_brute"] = HumanoidWith("piglin/piglin_brute", PiglinHead(true), armsForward: false);
        d["zombified_piglin"] = HumanoidWith("piglin/zombified_piglin", PiglinHead(false), armsForward: true);

        // ---- golems ----
        d["iron_golem"] = new EntityModel
        {
            Texture = "iron_golem/iron_golem", Rig = EntityRig.Biped, Width = 1.4f, Height = 2.7f, BabyScale = 0.5f,
            Parts =
            [
                P("head", PartRole.Head, -1, 0, -7, -2, [B(0, 0, -4, -12, -5.5f, 8, 10, 8), B(24, 0, -1, -5, -7.5f, 2, 4, 2)]),
                P("body", PartRole.Body, -1, 0, -7, 0, [B(0, 40, -9, -2, -6, 18, 12, 11), B(0, 70, -4.5f, 10, -3, 9, 5, 6, inflate: 0.5f)]),
                P("right_arm", PartRole.RightArm, -1, 0, -7, 0, [B(60, 21, -13, -2.5f, -3, 4, 30, 6)]),
                P("left_arm", PartRole.LeftArm, -1, 0, -7, 0, [B(60, 58, 9, -2.5f, -3, 4, 30, 6)]),
                P("right_leg", PartRole.RightLeg, -1, -4, 11, 0, [B(37, 0, -3.5f, -3, -3, 6, 16, 5)]),
                P("left_leg", PartRole.LeftLeg, -1, 5, 11, 0, [B(60, 0, -3.5f, -3, -3, 6, 16, 5, mirror: true)]),
            ],
        };
        d["snow_golem"] = new EntityModel
        {
            Texture = "snow_golem", Rig = EntityRig.Static, Width = 0.7f, Height = 1.9f,
            Parts =
            [
                P("head", PartRole.Head, -1, 0, 4, 0, [B(0, 0, -4, -8, -4, 8, 8, 8, inflate: -0.5f)]),
                P("upper_body", PartRole.Body, -1, 0, 13, 0, [B(0, 16, -5, -10, -5, 10, 10, 10, inflate: -0.5f)]),
                P("lower_body", PartRole.Static, -1, 0, 24, 0, [B(0, 36, -6, -12, -6, 12, 12, 12, inflate: -0.5f)]),
                P("left_arm", PartRole.Static, -1, 5, 6, 1, [B(32, 0, -1, 0, -1, 12, 2, 2, inflate: -0.5f)], rz: 1f),
                P("right_arm", PartRole.Static, -1, -5, 6, -1, [B(32, 0, -1, 0, -1, 12, 2, 2, inflate: -0.5f)], ry: MathF.PI, rz: -1f),
            ],
        };

        // ---- horses and their kin ----
        AddHorses(d);
        AddWater(d);
        AddSmall(d);

        // ---- llamas ----
        string[] llamaColours = ["creamy", "white", "brown", "gray"];
        foreach (var kind in new[] { "llama", "trader_llama" })
            d[kind] = new EntityModel
            {
                Texture = "llama/creamy", Rig = EntityRig.Quadruped, Width = 0.9f, Height = 1.87f, BabyScale = 0.5f,
                TextureByVariant = v => v != null && Array.IndexOf(llamaColours, v) >= 0 ? $"llama/{v}" : "llama/creamy",
                VariantTextures = llamaColours.Select(c => $"llama/{c}").ToArray(),
                Parts =
                [
                    P("head", PartRole.Head, -1, 0, 7, -6, [B(0, 0, -2, -14, -10, 4, 4, 9), B(0, 14, -4, -16, -6, 8, 18, 6), B(17, 0, -4, -19, -4, 3, 3, 2), B(17, 0, 1, -19, -4, 3, 3, 2)]),
                    P("body", PartRole.Body, -1, 0, 5, 2, [B(29, 0, -6, -10, -7, 12, 18, 10)], rx: HalfPi),
                    P("right_hind_leg", PartRole.RightHindLeg, -1, -3.5f, 10, 6, [B(29, 29, -2, 0, -2, 4, 14, 4)]),
                    P("left_hind_leg", PartRole.LeftHindLeg, -1, 3.5f, 10, 6, [B(29, 29, -2, 0, -2, 4, 14, 4)]),
                    P("right_front_leg", PartRole.RightFrontLeg, -1, -3.5f, 10, -5, [B(29, 29, -2, 0, -2, 4, 14, 4)]),
                    P("left_front_leg", PartRole.LeftFrontLeg, -1, 3.5f, 10, -5, [B(29, 29, -2, 0, -2, 4, 14, 4)]),
                ],
            };

        // ---- cats ----
        string[] catBreeds = ["tabby", "black", "red", "siamese", "british_shorthair", "calico", "persian", "ragdoll", "white", "jellie", "all_black"];
        ModelPart[] CatParts() =>
        [
            P("head", PartRole.Head, -1, 0, 15, -9, [B(0, 0, -2.5f, -2, -3, 5, 4, 5), B(0, 24, -1.5f, -0.001f, -4, 3, 2, 2), B(0, 10, -2, -3, 0, 1, 1, 2), B(6, 10, 1, -3, 0, 1, 1, 2)]),
            P("body", PartRole.Body, -1, 0, 12, -10, [B(20, 0, -2, 3, -8, 4, 16, 6)], rx: HalfPi),
            P("tail1", PartRole.Static, -1, 0, 15, 8, [B(0, 15, -0.5f, 0, 0, 1, 8, 1)], rx: 0.9f),
            P("tail2", PartRole.Static, -1, 0, 20, 14, [B(4, 15, -0.5f, 0, 0, 1, 8, 1)], rx: 1.7278f),   // held out behind, as the game rests it
            P("back_left_leg", PartRole.LeftHindLeg, -1, 1.1f, 18, 5, [B(8, 13, -1, 0, 1, 2, 6, 2)]),
            P("back_right_leg", PartRole.RightHindLeg, -1, -1.1f, 18, 5, [B(8, 13, -1, 0, 1, 2, 6, 2)]),
            P("front_left_leg", PartRole.LeftFrontLeg, -1, 1.2f, 14.1f, -5, [B(40, 0, -1, 0, 0, 2, 10, 2)]),
            P("front_right_leg", PartRole.RightFrontLeg, -1, -1.2f, 14.1f, -5, [B(40, 0, -1, 0, 0, 2, 10, 2)]),
        ];
        d["cat"] = new EntityModel
        {
            Texture = "cat/tabby", Rig = EntityRig.Quadruped, Width = 0.6f, Height = 0.7f, Scale = 0.8f, BabyScale = 0.5f,
            TextureByVariant = v => v != null && Array.IndexOf(catBreeds, v) >= 0 ? $"cat/{v}" : "cat/tabby",
            VariantTextures = catBreeds.Select(b => $"cat/{b}").ToArray(),
            Parts = CatParts(),
        };
        d["ocelot"] = new EntityModel { Texture = "cat/ocelot", Rig = EntityRig.Quadruped, Width = 0.6f, Height = 0.7f, BabyScale = 0.5f, Parts = CatParts() };

        // ---- rabbits ----
        var rabbitTextures = new Dictionary<string, string>
        {
            ["brown"] = "rabbit/brown", ["white"] = "rabbit/white", ["black"] = "rabbit/black", ["black_and_white"] = "rabbit/white_splotched",
            ["gold"] = "rabbit/gold", ["salt_and_pepper"] = "rabbit/salt", ["the_killer_bunny"] = "rabbit/caerbannog",
            // the names a Fabric server gives them
            ["white_splotched"] = "rabbit/white_splotched", ["salt"] = "rabbit/salt", ["evil"] = "rabbit/caerbannog",
        };
        d["rabbit"] = new EntityModel
        {
            Texture = "rabbit/brown", Rig = EntityRig.Quadruped, Width = 0.4f, Height = 0.5f, BabyScale = 0.5f,
            TextureByVariant = v => v != null && rabbitTextures.TryGetValue(v, out var t) ? t : "rabbit/brown",
            VariantTextures = rabbitTextures.Values.Distinct().ToArray(),
            Parts =
            [
                P("left_hind_foot", PartRole.LeftHindLeg, -1, 3, 17.5f, 3.7f, [B(26, 24, -1, 5.5f, -3.7f, 2, 1, 7)]),
                P("right_hind_foot", PartRole.RightHindLeg, -1, -3, 17.5f, 3.7f, [B(8, 24, -1, 5.5f, -3.7f, 2, 1, 7)]),
                P("left_haunch", PartRole.Static, -1, 3, 17.5f, 3.7f, [B(30, 15, -1, 0, 0, 2, 4, 5)], rx: -0.34907f),
                P("right_haunch", PartRole.Static, -1, -3, 17.5f, 3.7f, [B(16, 15, -1, 0, 0, 2, 4, 5)], rx: -0.34907f),
                P("body", PartRole.Body, -1, 0, 19, 8, [B(0, 0, -3, -2, -10, 6, 5, 10)], rx: -0.34907f),
                P("left_front_leg", PartRole.LeftFrontLeg, -1, 3, 17, -1, [B(8, 15, -1, 0, -1, 2, 7, 2)], rx: -0.17453f),
                P("right_front_leg", PartRole.RightFrontLeg, -1, -3, 17, -1, [B(0, 15, -1, 0, -1, 2, 7, 2)], rx: -0.17453f),
                P("head", PartRole.Head, -1, 0, 16, -1, [B(32, 0, -2.5f, -4, -5, 5, 4, 5), B(32, 9, -0.5f, -2.5f, -5.5f, 1, 1, 1)]),
                P("right_ear", PartRole.Static, 7, 0, 0, 0, [B(52, 0, -2.5f, -9, -1, 2, 5, 1)], ry: -0.2618f),
                P("left_ear", PartRole.Static, 7, 0, 0, 0, [B(58, 0, 0.5f, -9, -1, 2, 5, 1)], ry: 0.2618f),
                P("tail", PartRole.Static, -1, 0, 20, 7, [B(52, 6, -1.5f, -1.5f, 0, 3, 3, 2)], rx: -0.34907f),
            ],
        };

        // ---- foxes ----
        d["fox"] = new EntityModel
        {
            Texture = "fox/fox", Rig = EntityRig.Quadruped, Width = 0.6f, Height = 0.7f, BabyScale = 0.5f,
            TextureByVariant = v => v == "snow" ? "fox/snow_fox" : "fox/fox",
            VariantTextures = ["fox/snow_fox"],
            Parts =
            [
                P("head", PartRole.Head, -1, -1, 16.5f, -3, [B(1, 5, -3, -2, -5, 8, 6, 6), B(8, 1, -3, -4, -4, 2, 2, 1), B(15, 1, 3, -4, -4, 2, 2, 1), B(6, 18, -1, 2.01f, -8, 4, 2, 3)]),
                P("body", PartRole.Body, -1, 0, 16, -6, [B(24, 15, -3, 3, -3, 6, 11, 6)], rx: HalfPi),
                P("right_hind_leg", PartRole.RightHindLeg, -1, -5, 17.5f, 7, [B(13, 24, 2, 0.5f, -1, 2, 6, 2)]),
                P("left_hind_leg", PartRole.LeftHindLeg, -1, -1, 17.5f, 7, [B(4, 24, 2, 0.5f, -1, 2, 6, 2)]),
                P("right_front_leg", PartRole.RightFrontLeg, -1, -5, 17.5f, 0, [B(13, 24, 2, 0.5f, -1, 2, 6, 2)]),
                P("left_front_leg", PartRole.LeftFrontLeg, -1, -1, 17.5f, 0, [B(4, 24, 2, 0.5f, -1, 2, 6, 2)]),
                P("tail", PartRole.Static, -1, -4, 17, 9, [B(30, 0, 2, 0, -1, 4, 9, 5)], rx: HalfPi - 0.05236f),
            ],
        };

        // ---- the bear ----
        d["polar_bear"] = new EntityModel
        {
            Texture = "bear/polarbear", Rig = EntityRig.Quadruped, Width = 1.4f, Height = 1.4f, Scale = 1.2f, BabyScale = 0.5f,
            Parts =
            [
                P("head", PartRole.Head, -1, 0, 10, -16, [B(0, 0, -3.5f, -3, -3, 7, 7, 7), B(0, 44, -2.5f, 1, -6, 5, 3, 3), B(26, 0, -4.5f, -4, -1, 2, 2, 1), B(26, 0, 2.5f, -4, -1, 2, 2, 1)]),
                P("body", PartRole.Body, -1, -2, 9, 12, [B(0, 19, -5, -13, -7, 14, 14, 11), B(39, 0, -4, -25, -7, 12, 12, 10)], rx: HalfPi),
                P("right_hind_leg", PartRole.RightHindLeg, -1, -4.5f, 14, 6, [B(50, 22, -2, 0, -2, 4, 10, 8)]),
                P("left_hind_leg", PartRole.LeftHindLeg, -1, 4.5f, 14, 6, [B(50, 22, -2, 0, -2, 4, 10, 8)]),
                P("right_front_leg", PartRole.RightFrontLeg, -1, -3.5f, 14, -8, [B(50, 40, -2, 0, -2, 4, 10, 6)]),
                P("left_front_leg", PartRole.LeftFrontLeg, -1, 3.5f, 14, -8, [B(50, 40, -2, 0, -2, 4, 10, 6)]),
            ],
        };

        // ---- the nether and the end ----
        var ghastTentacles = new List<ModelPart> { P("body", PartRole.Body, -1, 0, 17, 0, [B(0, 0, -8, -8, -8, 16, 16, 16)]) };
        for (int i = 0; i < 9; i++)
        {
            float x = (i % 3 - 1) * 5f, z = (i / 3 - 1) * 5f;
            int length = 8 + (i * 5) % 4;   // the game gives each a length from 8 to 11
            ghastTentacles.Add(P("tentacle" + i, PartRole.Static, -1, x, 25f, z, [B(0, 0, -1, 0, -1, 2, length, 2)]));
        }
        d["ghast"] = new EntityModel { Texture = "ghast/ghast", Rig = EntityRig.Static, Width = 4f, Height = 4f, Scale = 4.5f, Parts = ghastTentacles };

        var blaze = new List<ModelPart> { P("head", PartRole.Head, -1, 0, 0, 0, [B(0, 0, -4, -4, -4, 8, 8, 8)]) };
        for (int i = 0; i < 12; i++)
        {
            float a, r, y;
            if (i < 4) { a = i * HalfPi; r = 9f; y = -2f; }
            else if (i < 8) { a = Quarter + (i - 4) * HalfPi; r = 7f; y = 2f; }
            else { a = 0.47123889f + (i - 8) * HalfPi; r = 5f; y = 11f; }
            blaze.Add(P("rod" + i, PartRole.Static, -1, MathF.Cos(a) * r, y, MathF.Sin(a) * r, [B(0, 16, 0, 0, 0, 2, 8, 2)]));
        }
        d["blaze"] = new EntityModel { Texture = "blaze", Rig = EntityRig.Static, Width = 0.6f, Height = 1.8f, Parts = blaze };

        d["slime"] = new EntityModel
        {
            Texture = "slime/slime", Rig = EntityRig.Static, Width = 0.52f, Height = 0.52f,
            Parts =
            [
                // The game draws a clear outer shell over the core and its face; the viewer has no blending in the creature pass, so the shell would hide the face: the core is
                // drawn a little fuller and the face stands out of it
                P("cube", PartRole.Body, -1, 0, 0, 0, [B(0, 16, -3, 17, -3, 6, 6, 6, inflate: 0.9f)]),
                P("right_eye", PartRole.Static, -1, 0, 0, 0, [B(32, 0, -3.25f, 18, -4.4f, 2, 2, 2)]),
                P("left_eye", PartRole.Static, -1, 0, 0, 0, [B(32, 4, 1.25f, 18, -4.4f, 2, 2, 2)]),
                P("mouth", PartRole.Static, -1, 0, 0, 0, [B(32, 8, 0, 21, -4.4f, 1, 1, 1)]),
            ],
        };
        {
            // The magma cube is eight thin slices of one cube and a core inside (the texture is the slices' layouts)
            var parts = new List<ModelPart> { P("core", PartRole.Body, -1, 0, 0, 0, [B(24, 40, -2, 18, -2, 4, 4, 4)]) };
            for (int i = 0; i < 8; i++)
                parts.Add(P("slice" + i, PartRole.Static, -1, 0, 0, 0, [B(i == 2 ? 24 : i == 3 ? 24 : 0, i == 2 ? 10 : i == 3 ? 19 : i, -4, 16 + i, -4, 8, 1, 8)]));
            d["magma_cube"] = new EntityModel { Texture = "slime/magmacube", Rig = EntityRig.Static, Width = 0.52f, Height = 0.52f, Parts = parts };
        }

        // ---- the small and the winged ----
        d["bee"] = new EntityModel
        {
            Texture = "bee/bee", Rig = EntityRig.Static, Width = 0.7f, Height = 0.6f, BabyScale = 0.5f,
            Parts =
            [
                P("body", PartRole.Body, -1, 0, 19, 0, [B(0, 0, -3.5f, -4, -5, 7, 7, 10), B(26, 7, 0, -1, 5, 0, 1, 2)]),
                P("left_antenna", PartRole.Static, 0, 0, -2, -5, [B(2, 3, 1.5f, -2, -3, 1, 2, 1)]),
                P("right_antenna", PartRole.Static, 0, 0, -2, -5, [B(2, 3, -2.5f, -2, -3, 1, 2, 1)]),
                P("right_wing", PartRole.RightWing, 0, -1.5f, -4, -3, [B(0, 18, -9, 0, 0, 9, 0, 6)], rx: 0f, ry: -0.2618f),
                P("left_wing", PartRole.LeftWing, 0, 1.5f, -4, -3, [B(0, 18, 0, 0, 0, 9, 0, 6)], rx: 0f, ry: 0.2618f),
                P("front_legs", PartRole.Static, 0, 1.5f, 3, -2, [B(26, 1, -5, 0, 0, 7, 2, 0)]),
                P("middle_legs", PartRole.Static, 0, 1.5f, 3, 0, [B(26, 3, -5, 0, 0, 7, 2, 0)]),
                P("back_legs", PartRole.Static, 0, 1.5f, 3, 2, [B(26, 5, -5, 0, 0, 7, 2, 0)]),
            ],
        };

        string[] parrots = ["red", "blue", "green", "cyan", "gray"];
        var parrotTexture = new Dictionary<string, string>
        {
            ["red"] = "parrot/parrot_red_blue", ["blue"] = "parrot/parrot_blue", ["green"] = "parrot/parrot_green", ["cyan"] = "parrot/parrot_yellow_blue", ["gray"] = "parrot/parrot_grey",
            // the names a Fabric server gives them
            ["red_blue"] = "parrot/parrot_red_blue", ["yellow_blue"] = "parrot/parrot_yellow_blue",
        };
        d["parrot"] = new EntityModel
        {
            Texture = "parrot/parrot_red_blue", Rig = EntityRig.Flying, Width = 0.5f, Height = 0.9f, BabyScale = 0.5f,
            TextureByVariant = v => v != null && parrotTexture.TryGetValue(v, out var t) ? t : "parrot/parrot_red_blue",
            VariantTextures = parrotTexture.Values.Distinct().ToArray(),
            Parts =
            [
                P("head", PartRole.Head, -1, 0, 15.69f, -2.76f, [B(2, 2, -1, -1.5f, -1, 2, 3, 2), B(10, 0, -1, -2, -3, 2, 1, 4), B(11, 7, -0.5f, -1.5f, -3, 1, 2, 1)]),
                P("body", PartRole.Body, -1, 0, 16.5f, -3, [B(2, 8, -1.5f, 0, -1.5f, 3, 6, 3)], rx: 0.4937f),
                P("tail", PartRole.Static, -1, 0, 21.07f, 1.16f, [B(22, 1, -1.5f, -1, -1, 3, 4, 1)], rx: 1.015f),
                P("left_wing", PartRole.LeftWing, -1, 1.5f, 16.94f, -2.76f, [B(19, 8, -0.5f, 0, -1.5f, 1, 5, 3)], rx: -0.6981f),
                P("right_wing", PartRole.RightWing, -1, -1.5f, 16.94f, -2.76f, [B(19, 8, -0.5f, 0, -1.5f, 1, 5, 3)], rx: -0.6981f),
                P("left_leg", PartRole.LeftLeg, -1, 1.5f, 22, -1.5f, [B(14, 18, -0.5f, 0, -0.5f, 1, 2, 1)]),
                P("right_leg", PartRole.RightLeg, -1, -1.5f, 22, -1.5f, [B(14, 18, -0.5f, 0, -0.5f, 1, 2, 1)]),
            ],
        };
    }

    /// <summary>"chestnut/white_dots" -> its two halves with the underscores out ("chestnut", "whitedots"); either may be empty.</summary>
    private static (string colour, string marking) HorseVariant(string? variant)
    {
        if (variant == null) return ("", "");
        int slash = variant.IndexOf('/');
        return slash < 0 ? (Without(variant, "_"), "") : (Without(variant[..slash], "_"), Without(variant[(slash + 1)..], "_"));
    }

    private static void AddHorses(Dictionary<string, EntityModel> d)
    {
        // The horse's own parts; the donkey and the mule have long ears, the undead no markings
        ModelPart[] Parts(bool longEars, string? markingFolder)
        {
            var list = new List<ModelPart>
            {
                P("body", PartRole.Body, -1, 0, 11, 5, [B(0, 32, -5, -8, -17, 10, 10, 22)]),
                P("head_parts", PartRole.Head, -1, 0, 4, -12, [B(0, 35, -2.05f, -6, -2, 4, 12, 7)], rx: 0.5236f),
                P("head", PartRole.Static, 1, 0, 0, 0, [B(0, 13, -3, -11, -2, 6, 5, 7)]),
                P("mane", PartRole.Static, 1, 0, 0, 0, [B(56, 36, -1, -11, 5.01f, 2, 16, 2)]),
                P("upper_mouth", PartRole.Static, 1, 0, 0, 0, [B(0, 25, -2, -11, -7, 4, 5, 5)]),
                P("left_hind_leg", PartRole.LeftHindLeg, -1, 4, 14, 7, [B(48, 21, -3, -1.01f, -1, 4, 11, 4, mirror: true)]),
                P("right_hind_leg", PartRole.RightHindLeg, -1, -4, 14, 7, [B(48, 21, -1, -1.01f, -1, 4, 11, 4)]),
                P("left_front_leg", PartRole.LeftFrontLeg, -1, 4, 14, -12, [B(48, 21, -3, -1.01f, -1, 4, 11, 4, mirror: true)]),
                P("right_front_leg", PartRole.RightFrontLeg, -1, -4, 14, -12, [B(48, 21, -1, -1.01f, -1, 4, 11, 4)]),
                P("tail", PartRole.Static, -1, 0, 3, 14, [B(42, 36, -1.5f, 0, 0, 3, 14, 4)], rx: 0.5236f),
            };
            if (longEars)
            {
                // The donkey and the mule have no mane and tall ears, leaning out
                list.RemoveAll(p => p.Name == "mane");
                list.Add(P("left_ear", PartRole.Static, 1, 1.25f, -10, 4, [B(0, 12, -1, -7, 0, 2, 7, 1)], rx: 0.2618f, rz: 0.2618f));
                list.Add(P("right_ear", PartRole.Static, 1, -1.25f, -10, 4, [B(0, 12, -1, -7, 0, 2, 7, 1, mirror: true)], rx: 0.2618f, rz: -0.2618f));
            }
            else
            {
                list.Add(P("left_ear", PartRole.Static, 1, 0, 0, 0, [B(19, 16, 0.55f, -13, 4, 2, 3, 1)]));
                list.Add(P("right_ear", PartRole.Static, 1, 0, 0, 0, [B(19, 16, -2.55f, -13, 4, 2, 3, 1)]));
            }
            if (markingFolder != null)
            {
                // The markings: the same body, head and legs again in the markings' skin, where the horse has any
                string? Pick(string? v) { var (_, m) = HorseVariant(v); return Array.IndexOf(Lists.HorseMarkings, m) >= 0 ? $"{markingFolder}/horse_markings_{m}" : null; }
                list.AddRange(Layer(list.Where(p => p.Name != "tail").ToArray(), list.Count, Pick, Lists.HorseMarkings.Select(m => $"{markingFolder}/horse_markings_{m}").ToArray(), "_markings"));
            }
            return list.ToArray();
        }

        d["horse"] = new EntityModel
        {
            Texture = "horse/horse_brown", Rig = EntityRig.Quadruped, Width = 1.396f, Height = 1.6f, Scale = 1.1f, BabyScale = 0.5f,
            TextureByVariant = v => { var (c, _) = HorseVariant(v); return Array.IndexOf(Lists.HorseColours, c) >= 0 ? $"horse/horse_{c}" : "horse/horse_brown"; },
            VariantTextures = Lists.HorseColours.Select(c => $"horse/horse_{c}").ToArray(),
            Parts = Parts(false, "horse"),
        };
        d["donkey"] = new EntityModel { Texture = "horse/donkey", Rig = EntityRig.Quadruped, Width = 1.396f, Height = 1.5f, Scale = 0.87f, BabyScale = 0.5f, Parts = Parts(true, null) };
        d["mule"] = new EntityModel { Texture = "horse/mule", Rig = EntityRig.Quadruped, Width = 1.396f, Height = 1.6f, Scale = 0.92f, BabyScale = 0.5f, Parts = Parts(true, null) };
        d["zombie_horse"] = new EntityModel { Texture = "horse/horse_zombie", Rig = EntityRig.Quadruped, Width = 1.396f, Height = 1.6f, Scale = 1.1f, BabyScale = 0.5f, Parts = Parts(false, null) };
        d["skeleton_horse"] = new EntityModel { Texture = "horse/horse_skeleton", Rig = EntityRig.Quadruped, Width = 1.396f, Height = 1.6f, Scale = 1.1f, BabyScale = 0.5f, Parts = Parts(false, null) };
    }

    private static void AddWater(Dictionary<string, EntityModel> d)
    {
        // ---- squids: a body and eight tentacles hanging from it (the body is centred on the hitbox) ----
        foreach (var (name, texture) in new[] { ("squid", "squid/squid"), ("glow_squid", "squid/glow_squid") })
        {
            var parts = new List<ModelPart> { P("body", PartRole.Body, -1, 0, 17.6f, 0, [B(0, 0, -6, -8, -6, 12, 16, 12)]) };
            for (int i = 0; i < 8; i++)
            {
                float angle = i * MathF.PI * 2f / 8f;
                parts.Add(P("tentacle" + i, PartRole.Static, -1, MathF.Cos(angle) * 5f, 24.6f, MathF.Sin(angle) * 5f, [B(48, 0, -1, 0, -1, 2, 18, 2)]));
            }
            d[name] = new EntityModel { Texture = texture, Rig = EntityRig.Static, Width = 0.8f, Height = 0.8f, Parts = parts };
        }

        // ---- fish ----
        // Fish: the body and the head. (The game's thin fins and tails are not drawn: they are a few painted pixels each, and the box numbers for them are easy to get wrong.)
        d["cod"] = new EntityModel
        {
            Texture = "fish/cod", Rig = EntityRig.Static, Width = 0.5f, Height = 0.3f,
            Parts =
            [
                P("body", PartRole.Body, -1, 0, 22, 0, [B(0, 0, -1, -2, 0, 2, 4, 7)]),
                P("head", PartRole.Head, -1, 0, 22, 0, [B(11, 0, -1, -2, -3, 2, 4, 3), B(0, 0, -1, -2, -4, 2, 3, 1)]),
            ],
        };
        d["salmon"] = new EntityModel
        {
            Texture = "fish/salmon", Rig = EntityRig.Static, Width = 0.7f, Height = 0.4f,
            Parts =
            [
                P("body_front", PartRole.Body, -1, 0, 20, 0, [B(0, 0, -1.5f, -2.5f, 0, 3, 5, 8)]),
                P("body_back", PartRole.Static, -1, 0, 20, 8, [B(0, 13, -1.5f, -2.5f, 0, 3, 5, 8)]),
                P("head", PartRole.Head, -1, 0, 20, 0, [B(22, 0, -1, -2, -3, 2, 4, 3)]),
            ],
        };
        // The pufferfish: the game's full-size body, drawn smaller (a puffed one is the hitbox's 0.7 across, a calm one about half that)
        d["pufferfish"] = new EntityModel
        {
            Texture = "fish/pufferfish", Rig = EntityRig.Static, Width = 0.7f, Height = 0.7f, Scale = 0.8f,
            Parts = [P("body", PartRole.Body, -1, 0, 24, 0, [B(0, 0, -4, -8, -4, 8, 8, 8)])],
        };

        // Tropical fish: a small and a large model, a body colour and a pattern in another (variant "pattern|body colour|pattern colour")
        foreach (bool large in new[] { false, true })
        {
            var patterns = large ? Lists.TropicalPatternsB : Lists.TropicalPatternsA;
            string basePath = large ? "fish/tropical_b" : "fish/tropical_a";
            string? Overlay(string? v)
            {
                int index = Array.IndexOf(patterns, EntityPoser.Component(v, 0) ?? "");
                return index < 0 ? null : $"{basePath}_pattern_{index + 1}";
            }
            ModelPart[] Fish(bool overlay)
            {
                int slot = overlay ? 2 : 1;
                Func<string?, string?>? vt = overlay ? Overlay : null;
                IReadOnlyList<string>? names = overlay ? Enumerable.Range(1, 6).Select(i => $"{basePath}_pattern_{i}").ToArray() : null;
                var box = large ? B(0, 20, -1, -3, -3, 2, 6, 6) : B(0, 0, -1, -1.5f, -3, 2, 3, 6);
                return [P(overlay ? "body_pattern" : "body", PartRole.Body, -1, 0, large ? 19 : 22, 0, [box with { Inflate = overlay ? 0.02f : 0f }], 0, 0, 0, null, true, vt, names, slot)];
            }
            d[large ? "tropical_fish_b" : "tropical_fish_a"] = new EntityModel
            {
                Texture = basePath, Rig = EntityRig.Static, Width = 0.5f, Height = 0.4f, Parts = Fish(false).Concat(Fish(true)).ToArray(),
            };
        }

        // ---- the dolphin and the turtle ----
        d["dolphin"] = new EntityModel
        {
            Texture = "dolphin", Rig = EntityRig.Static, Width = 0.9f, Height = 0.6f,
            Parts =
            [
                P("body", PartRole.Body, -1, 0, 22, -5, [B(22, 0, -4, -7, 0, 8, 7, 13)]),
                P("back_fin", PartRole.Static, 0, 0, -7, 0, [B(51, 0, -0.5f, 0, 8, 1, 4, 5)], rx: 0.6981317f),
                P("left_fin", PartRole.Static, 0, 4, -1, 2, [B(48, 20, -0.5f, 0, 0, 1, 4, 7)], rx: 0.6981317f, rz: 2.0943952f),
                P("right_fin", PartRole.Static, 0, -4, -1, 2, [B(48, 20, -0.5f, 0, 0, 1, 4, 7, mirror: true)], rx: 0.6981317f, rz: -2.0943952f),
                P("head", PartRole.Head, 0, 0, -4, -3, [B(0, 0, -4, -3, -3, 8, 7, 6), B(0, 13, -1, 2, -7, 2, 2, 4)]),
                P("tail", PartRole.Static, 0, 0, -2.5f, 11, [B(0, 19, -2, -2.5f, 0, 4, 5, 11)]),
                P("tail_fin", PartRole.Static, 5, 0, 0, 9, [B(19, 20, -5, -0.5f, 0, 10, 1, 6)]),
            ],
        };
        d["turtle"] = new EntityModel
        {
            Texture = "turtle/big_sea_turtle", Rig = EntityRig.Quadruped, Width = 1.2f, Height = 0.4f, BabyScale = 0.5f,
            Parts =
            [
                P("head", PartRole.Head, -1, 0, 19, -10, [B(3, 0, -3, -1, -3, 6, 5, 6)]),
                P("body", PartRole.Body, -1, 0, 11, -10, [B(7, 37, -9.5f, 3, -10, 19, 20, 6), B(31, 1, -5.5f, 3, -13, 11, 18, 3)], rx: HalfPi),
                P("right_hind_leg", PartRole.RightHindLeg, -1, -3.5f, 22, 11, [B(1, 23, -2, 0, 0, 4, 1, 10)]),
                P("left_hind_leg", PartRole.LeftHindLeg, -1, 3.5f, 22, 11, [B(1, 12, -2, 0, 0, 4, 1, 10)]),
                P("right_front_leg", PartRole.RightFrontLeg, -1, -5, 21, -4, [B(27, 30, -13, 0, -2, 13, 1, 5)]),
                P("left_front_leg", PartRole.LeftFrontLeg, -1, 5, 21, -4, [B(27, 24, 0, 0, -2, 13, 1, 5)]),
            ],
        };
    }

    private static void AddSmall(Dictionary<string, EntityModel> d)
    {
        // ---- the crawlers: segments of a body in a row ----
        ModelPart[] Segments(string prefix, (float w, float h, float dpt, float u, float v)[] sizes)
        {
            var list = new List<ModelPart>();
            float z = -3.5f;
            for (int i = 0; i < sizes.Length; i++)
            {
                var (w, h, dpt, u, v) = sizes[i];
                list.Add(P($"{prefix}{i}", i == 0 ? PartRole.Head : PartRole.Static, -1, 0, 24 - h, z, [B(u, v, -w / 2f, 0, -dpt / 2f, w, h, dpt)]));
                if (i + 1 < sizes.Length) z += (dpt + sizes[i + 1].dpt) * 0.5f;
            }
            return list.ToArray();
        }
        d["silverfish"] = new EntityModel
        {
            Texture = "silverfish", Rig = EntityRig.Static, Width = 0.4f, Height = 0.3f,
            Parts = Segments("segment", [(3, 2, 2, 0, 0), (4, 3, 2, 0, 4), (6, 4, 3, 0, 9), (3, 3, 3, 0, 16), (2, 2, 3, 0, 22), (2, 1, 2, 11, 0), (1, 1, 2, 13, 4)]),
        };
        d["endermite"] = new EntityModel
        {
            Texture = "endermite", Rig = EntityRig.Static, Width = 0.4f, Height = 0.3f,
            Parts = Segments("segment", [(4, 3, 2, 0, 0), (6, 4, 5, 0, 5), (3, 3, 1, 0, 14), (1, 2, 1, 0, 18)]),
        };

    }
}
