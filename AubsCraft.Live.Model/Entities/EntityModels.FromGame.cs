using System.Numerics;

namespace AubsCraft.Live.Model.Entities;

/// <summary>
/// The creatures whose model is the game's own, taken whole from its model classes (<see cref="GameModels"/>): which model, which texture, how big the creature is, and what
/// the game's renderer does to it on top (the bat is drawn at a third of its model, the phantom lifted and moved back, the elder guardian two and a third times larger). The
/// walk and head animations come from the part names (<see cref="GameModels.RoleByName"/>).
/// </summary>
public static partial class EntityModels
{
    private static void AddFromGame(Dictionary<string, EntityModel> d)
    {
        EntityModel Make(string key, string texture, EntityRig rig, float width, float height, float scale = 1f, Vector3 offset = default,
            Func<string, PartRole>? role = null, string[]? hide = null, float baby = 0.5f, string[]? variants = null, Func<string?, string>? byVariant = null, float modelYaw = 0f, Func<ModelPart[], ModelPart[]>? afterParts = null) => new()
        {
            Texture = texture, Rig = rig, Width = width, Height = height, Scale = scale, ModelOffset = offset, ModelYawDegrees = modelYaw, BabyScale = baby,
            TextureByVariant = byVariant, VariantTextures = variants ?? Array.Empty<string>(),
            Parts = afterParts == null ? GameModels.Parts(key, role, hide == null ? null : new HashSet<string>(hide)) : afterParts(GameModels.Parts(key, role, hide == null ? null : new HashSet<string>(hide))),
        };
        // Parts that stay still however the creature moves (a frog hops, it does not swing its legs like a pig's)
        PartRole HeadOnly(string name) => name == "head" ? PartRole.Head : name == "body" ? PartRole.Body : PartRole.Static;

        // ---- in the air ----
        d["bat"] = Make("BatModel.createBodyLayer", "bat", EntityRig.Static, 0.5f, 0.9f, scale: 0.35f, role: HeadOnly);
        // The renderer lifts the phantom (translate 0, 1.3125, 0.1875) so its body hangs about the middle of its hitbox
        d["phantom"] = Make("PhantomModel.createBodyLayer", "phantom", EntityRig.Static, 0.9f, 0.5f, offset: new Vector3(0f, 1.3125f, 0.1875f), role: HeadOnly);
        d["vex"] = Make("VexModel.createBodyLayer", "illager/vex", EntityRig.Static, 0.4f, 0.8f, role: HeadOnly);
        d["allay"] = Make("AllayModel.createBodyLayer", "allay/allay", EntityRig.Static, 0.35f, 0.6f, role: HeadOnly);
        d["breeze"] = Make("BreezeModel.createBodyLayer(2)", "breeze/breeze", EntityRig.Static, 0.6f, 1.77f, role: HeadOnly, hide: ["wind_body", "eyes"]);

        // ---- four-legged ----
        d["goat"] = Make("GoatModel.createBodyLayer", "goat/goat", EntityRig.Quadruped, 0.9f, 1.3f);
        // A panda's gene (the plugin sends the one it shows: a recessive gene only when both are it): its own coat
        string[] pandaGenes = ["lazy", "worried", "playful", "brown", "weak", "aggressive"];
        d["panda"] = Make("PandaModel.createBodyLayer", "panda/panda", EntityRig.Quadruped, 1.3f, 1.25f,
            variants: pandaGenes.Select(g => $"panda/{g}_panda").ToArray(), byVariant: v => v != null && Array.IndexOf(pandaGenes, v) >= 0 ? $"panda/{v}_panda" : "panda/panda");
        d["camel"] = Make("CamelModel.createBodyLayer", "camel/camel", EntityRig.Quadruped, 1.7f, 2.375f);
        d["sniffer"] = Make("SnifferModel.createBodyLayer", "sniffer/sniffer", EntityRig.Quadruped, 1.9f, 1.75f);
        // An armadillo walks; the rolled-up ball ("cube") is the game's pose for when it is scared
        d["armadillo"] = Make("ArmadilloModel.createBodyLayer", "armadillo", EntityRig.Quadruped, 0.7f, 0.65f, hide: ["cube"]);
        d["hoglin"] = Make("HoglinModel.createBodyLayer", "hoglin/hoglin", EntityRig.Quadruped, 1.4f, 1.4f);
        d["zoglin"] = Make("HoglinModel.createBodyLayer", "hoglin/zoglin", EntityRig.Quadruped, 1.4f, 1.4f);
        d["ravager"] = Make("RavagerModel.createBodyLayer", "illager/ravager", EntityRig.Quadruped, 1.95f, 2.2f);
        string[] axolotlColours = ["lucy", "wild", "gold", "cyan", "blue"];
        d["axolotl"] = Make("AxolotlModel.createBodyLayer", "axolotl/axolotl_lucy", EntityRig.Static, 0.75f, 0.42f, role: HeadOnly,
            variants: axolotlColours.Select(c => $"axolotl/axolotl_{c}").ToArray(), byVariant: v => v != null && Array.IndexOf(axolotlColours, v) >= 0 ? $"axolotl/axolotl_{v}" : "axolotl/axolotl_lucy");

        // ---- two-legged ----
        d["strider"] = Make("StriderModel.createBodyLayer", "strider/strider", EntityRig.Biped, 0.9f, 1.7f);
        d["warden"] = Make("WardenModel.createBodyLayer", "warden/warden", EntityRig.Biped, 0.9f, 2.9f);
        d["creaking"] = Make("CreakingModel.createBodyLayer", "creaking/creaking", EntityRig.Biped, 0.9f, 2.7f);
        // The giant is a zombie six times over
        if (d.TryGetValue("zombie", out var zombie)) d["giant"] = new EntityModel
        {
            Texture = zombie.Texture, Rig = zombie.Rig, Width = 3.6f, Height = 12f, ArmsForward = zombie.ArmsForward, Scale = 6f, BabyScale = 0.5f, Parts = zombie.Parts,
        };

        // ---- the rest ----
        string[] frogKinds = ["temperate", "warm", "cold"];
        d["frog"] = Make("FrogModel.createBodyLayer", "frog/temperate_frog", EntityRig.Static, 0.5f, 0.5f, role: HeadOnly, hide: ["tongue", "croaking_body"],
            variants: frogKinds.Select(k => $"frog/{k}_frog").ToArray(), byVariant: v => v != null && Array.IndexOf(frogKinds, v) >= 0 ? $"frog/{v}_frog" : "frog/temperate_frog");
        d["tadpole"] = Make("TadpoleModel.createBodyLayer", "tadpole/tadpole", EntityRig.Static, 0.4f, 0.3f, role: HeadOnly);
        d["guardian"] = Make("GuardianModel.createBodyLayer", "guardian", EntityRig.Static, 0.85f, 0.85f, role: HeadOnly);
        d["elder_guardian"] = Make("GuardianModel.createElderGuardianLayer", "guardian_elder", EntityRig.Static, 1.9975f, 1.9975f, role: HeadOnly);   // the game scales this layer definition itself (2.35)
        string[] dyes = ["white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray", "light_gray", "cyan", "purple", "blue", "brown", "green", "red", "black"];
        d["shulker"] = Make("ShulkerModel.createBodyLayer", "shulker/shulker", EntityRig.Static, 1f, 1f, role: HeadOnly,
            variants: dyes.Select(c => $"shulker/shulker_{c}").ToArray(), byVariant: v => v != null && Array.IndexOf(dyes, v) >= 0 ? $"shulker/shulker_{v}" : "shulker/shulker");
        d["wither"] = Make("WitherBossModel.createBodyLayer(1)", "wither/wither", EntityRig.Static, 0.9f, 3.5f, scale: 2f, role: HeadOnly);   // the renderer draws it twice as large
        d["ender_dragon"] = Make("EnderDragonModel.createBodyLayer", "enderdragon/dragon", EntityRig.Static, 16f, 8f, role: HeadOnly);

        // ---- hand-made models the game's own numbers corrected: the same creature, its textures and variants kept, its geometry the game's ----
        EntityModel WithGameParts(EntityModel m, string key, Func<string, PartRole>? role = null, string[]? hide = null) => new()
        {
            Texture = m.Texture, Rig = m.Rig, Width = m.Width, Height = m.Height, ArmsForward = m.ArmsForward, BabyScale = m.BabyScale, TextureByVariant = m.TextureByVariant,
            VariantTextures = m.VariantTextures, FootY = m.FootY, Scale = m.Scale, ModelOffset = m.ModelOffset, ModelYawDegrees = m.ModelYawDegrees,
            Parts = GameModels.Parts(key, role, hide == null ? null : new HashSet<string>(hide)),
        };
        // A cow's legs and nose sit a pixel off in the hand-made model; a mooshroom is a cow
        if (d.TryGetValue("cow", out var cow)) d["cow"] = WithGameParts(cow, "CowModel.createBodyLayer");
        if (d.TryGetValue("mooshroom", out var mooshroom)) d["mooshroom"] = WithGameParts(mooshroom, "CowModel.createBodyLayer");
        // The parrot's wings, legs and beak were misplaced; its wings flap
        if (d.TryGetValue("parrot", out var parrot))
            d["parrot"] = WithGameParts(parrot, "ParrotModel.createBodyLayer", n => n switch { "right_wing" => PartRole.RightWing, "left_wing" => PartRole.LeftWing, _ => HeadOnly(n) });
        if (d.TryGetValue("dolphin", out var dolphin)) d["dolphin"] = WithGameParts(dolphin, "DolphinModel.createBodyLayer", HeadOnly);
        if (d.TryGetValue("silverfish", out var silverfish)) d["silverfish"] = WithGameParts(silverfish, "SilverfishModel.createBodyLayer", _ => PartRole.Static);
        if (d.TryGetValue("bee", out var bee)) d["bee"] = WithGameParts(bee, "BeeModel.createBodyLayer", HeadOnly);

        // Tropical fish: the game's models, a body colour on the whole fish and a pattern in another colour over it (variant "pattern|body colour|pattern colour")
        foreach (bool large in new[] { false, true })
        {
            var patterns = large ? Lists.TropicalPatternsB : Lists.TropicalPatternsA;
            string basePath = large ? "fish/tropical_b" : "fish/tropical_a";
            string? Overlay(string? v)
            {
                int index = Array.IndexOf(patterns, EntityPoser.Component(v, 0) ?? "");
                return index < 0 ? null : $"{basePath}_pattern_{index + 1}";
            }
            var body = GameModels.Parts(large ? "TropicalFishModelB.createBodyLayer(1)" : "TropicalFishModelA.createBodyLayer(1)", HeadOnly)
                .Select(p => p with { Dyed = true, DyeSlot = 1 }).ToArray();
            var pattern = Layer(body, body.Length, Overlay, Enumerable.Range(1, 6).Select(i => $"{basePath}_pattern_{i}").ToArray(), "_pattern").Select(p => p with { DyeSlot = 2 });
            d[large ? "tropical_fish_b" : "tropical_fish_a"] = new EntityModel
            {
                Texture = basePath, Rig = EntityRig.Static, Width = 0.5f, Height = 0.4f, Parts = body.Concat(pattern).ToArray(),
            };
        }

        // ---- vehicles and stands ----
        // A boat is drawn lifted 0.375 (the model offset is in the flipped space of the renderer: 1.501 - 0.375) and turned a quarter; a raft the same with its own model. The paddles stay as the model has them.
        string[] woods = ["oak", "spruce", "birch", "jungle", "acacia", "dark_oak", "mangrove", "cherry", "pale_oak"];
        var lift = new Vector3(0f, 1.501f - 0.375f, 0f);
        // The paddles of a boat nobody rows: the game tips them back (about 37 degrees) rather than leave them lying out flat
        ModelPart[] RestingPaddles(ModelPart[] parts) => parts.Select(p => p.Name.EndsWith("_paddle", StringComparison.Ordinal) ? p with { Rotation = new Vector3(-0.6545f, p.Rotation.Y, p.Rotation.Z) } : p).ToArray();
        foreach (var wood in woods)
        {
            d[$"{wood}_boat"] = Make("BoatModel.createBoatModel", $"boat/{wood}", EntityRig.Static, 1.375f, 0.5625f, offset: lift, role: _ => PartRole.Static, modelYaw: 90f, afterParts: RestingPaddles);
            d[$"{wood}_chest_boat"] = Make("BoatModel.createChestBoatModel", $"chest_boat/{wood}", EntityRig.Static, 1.375f, 0.5625f, offset: lift, role: _ => PartRole.Static, modelYaw: 90f, afterParts: RestingPaddles);
        }
        d["bamboo_raft"] = Make("RaftModel.createRaftModel", "boat/bamboo", EntityRig.Static, 1.375f, 0.5625f, offset: lift, role: _ => PartRole.Static, modelYaw: 90f, afterParts: RestingPaddles);
        d["bamboo_chest_raft"] = Make("RaftModel.createChestRaftModel", "chest_boat/bamboo", EntityRig.Static, 1.375f, 0.5625f, offset: lift, role: _ => PartRole.Static, modelYaw: 90f, afterParts: RestingPaddles);
        // Every minecart is the same cart (the block a chest or furnace cart carries is not drawn)
        foreach (var cart in new[] { "minecart", "chest_minecart", "furnace_minecart", "hopper_minecart", "tnt_minecart", "spawner_minecart", "command_block_minecart" })
            d[cart] = Make("MinecartModel.createBodyLayer", "minecart", EntityRig.Static, 0.98f, 0.7f, offset: lift, role: _ => PartRole.Static);
        // An armor stand with no arms and its base plate (the plugin does not say otherwise); its hat layer is for armour
        d["armor_stand"] = Make("ArmorStandModel.createBodyLayer", "armorstand/wood", EntityRig.Static, 0.5f, 1.975f, role: _ => PartRole.Static, hide: ["hat", "right_arm", "left_arm"]);

        // ---- fish: the game's whole models, fins and tails and all ----
        d["cod"] = Make("CodModel.createBodyLayer", "fish/cod", EntityRig.Static, 0.5f, 0.3f, role: HeadOnly);
        d["salmon"] = Make("SalmonModel.createBodyLayer", "fish/salmon", EntityRig.Static, 0.7f, 0.4f, role: HeadOnly);
        // A pufferfish is small until it puffs up (the live frame does not say when)
        d["pufferfish"] = Make("PufferfishSmallModel.createBodyLayer", "fish/pufferfish", EntityRig.Static, 0.7f, 0.7f, role: HeadOnly);
    }
}
