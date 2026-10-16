using System.Numerics;
using AubsCraft.Live.Model.Entities;

namespace AubsCraft.Live.Model.BlockEntities;

/// <summary>
/// The models of the blocks the game draws with a renderer of their own, put in the world the way those renderers put them: each kind's model from the game's own classes
/// (<see cref="GameModels"/>) under the same sequence of moves its renderer makes (translate to the middle of the block, turn about the vertical, flip and scale for the models that are
/// authored upside down). The result is a list of boxes like a creature's (<see cref="PartDraw"/>), drawn with the creatures' pipeline.
/// </summary>
public static class BlockEntityModels
{
    // The moves of a game renderer's pose stack, in the order its code makes them: the LAST one made is applied to the model first. Built as a row-vector matrix (v * M).
    private sealed class Stack
    {
        public Matrix4x4 M = Matrix4x4.Identity;
        public Stack Translate(float x, float y, float z) { M = Matrix4x4.CreateTranslation(x, y, z) * M; return this; }
        public Stack Scale(float x, float y, float z) { M = Matrix4x4.CreateScale(x, y, z) * M; return this; }
        public Stack RotateX(float degrees) { M = Matrix4x4.CreateRotationX(degrees * MathF.PI / 180f) * M; return this; }
        public Stack RotateY(float degrees) { M = Matrix4x4.CreateRotationY(degrees * MathF.PI / 180f) * M; return this; }
        public Stack RotateZ(float degrees) { M = Matrix4x4.CreateRotationZ(degrees * MathF.PI / 180f) * M; return this; }
    }

    private const float Step = 22.5f;   // a sixteenth of a turn

    /// <summary>The textures the block entity models wear, for the asset tool to pack (paths under textures/entity).</summary>
    public static IEnumerable<string> TextureNames()
    {
        foreach (var t in new[] { "normal", "normal_left", "normal_right", "trapped", "trapped_left", "trapped_right", "ender" }) yield return $"chest/{t}";
        foreach (var dye in BlockEntityKinds.Dyes) { yield return $"bed/{dye}"; yield return $"shulker/shulker_{dye}"; }
        yield return "shulker/shulker";
        foreach (var wood in BlockEntityKinds.Woods) { yield return $"signs/{wood}"; yield return $"signs/hanging/{wood}"; }
        yield return "banner_base";
        foreach (var t in BannerPatterns.TextureNames()) yield return t;
        yield return "skeleton/skeleton"; yield return "skeleton/wither_skeleton"; yield return "zombie/zombie"; yield return "creeper/creeper";
    }

    private static readonly Dictionary<string, ModelPart[]> Cache = new();

    private static ModelPart[] Parts(string key)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var parts)) Cache[key] = parts = GameModels.Parts(key, _ => PartRole.Static);
            return parts;
        }
    }

    /// <summary>Append the boxes of <paramref name="spot"/> (a block whose lowest corner is at <paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/> in the world).</summary>
    public static void Pose(in BlockEntitySpot spot, float x, float y, float z, List<PartDraw> output)
    {
        float yaw = spot.Orient * Step;   // 0 south, 90 west, 180 north, 270 east
        switch (spot.Kind)
        {
            case BlockEntityKind.Chest:
            case BlockEntityKind.TrappedChest:
            case BlockEntityKind.EnderChest:
            {
                string family = spot.Kind switch { BlockEntityKind.TrappedChest => "trapped", BlockEntityKind.EnderChest => "ender", _ => "normal" };
                string texture, key;
                if (spot.Kind == BlockEntityKind.EnderChest || spot.Variant == 0) { texture = spot.Kind == BlockEntityKind.EnderChest ? "chest/ender" : $"chest/{family}"; key = "ChestModel.createSingleBodyLayer"; }
                else if (spot.Variant == 1) { texture = $"chest/{family}_left"; key = "ChestModel.createDoubleBodyLeftLayer"; }
                else { texture = $"chest/{family}_right"; key = "ChestModel.createDoubleBodyRightLayer"; }
                // ChestRenderer: translate (.5, .5, .5), turn by minus the facing's yaw, translate (-.5, -.5, -.5)
                var s = new Stack().Translate(0.5f, 0.5f, 0.5f).RotateY(-yaw).Translate(-0.5f, -0.5f, -0.5f);
                Add(Parts(key), s.M, x, y, z, texture, Vector3.One, output);
                break;
            }
            case BlockEntityKind.Bed:
            {
                // BedRenderer: translate (0, 9/16, 0), lay the model flat (x 90), then turn it in its own plane about its middle by 180 + the facing's yaw, translate (-.5, -.5, -.5)
                var s = new Stack().Translate(0f, 0.5625f, 0f).RotateX(90f).Translate(0.5f, 0.5f, 0.5f).RotateZ(180f + yaw).Translate(-0.5f, -0.5f, -0.5f);
                string key = spot.Part == 1 ? "BedRenderer.createHeadLayer" : "BedRenderer.createFootLayer";
                Add(Parts(key), s.M, x, y, z, $"bed/{BlockEntityKinds.Dyes[spot.Variant % 16]}", Vector3.One, output);
                break;
            }
            case BlockEntityKind.ShulkerBox:
            {
                // ShulkerBoxRenderer: translate (.5, .5, .5), a hair smaller (0.9995), turn to its facing, flip (1, -1, -1), translate (0, -1, 0)
                var s = new Stack().Translate(0.5f, 0.5f, 0.5f).Scale(0.9995f, 0.9995f, 0.9995f);
                switch (spot.Orient)
                {
                    case BlockEntityKinds.FacingDown: s.RotateX(180f); break;
                    case 0: s.RotateX(90f); break;       // south
                    case 8: s.RotateX(-90f); break;      // north
                    case 4: s.RotateZ(90f); break;       // west
                    case 12: s.RotateZ(-90f); break;     // east
                }
                s.Scale(1f, -1f, -1f).Translate(0f, -1f, 0f);
                string texture = spot.Variant >= 16 ? "shulker/shulker" : $"shulker/shulker_{BlockEntityKinds.Dyes[spot.Variant]}";
                Add(Parts("ShulkerModel.createBoxLayer"), s.M, x, y, z, texture, Vector3.One, output);
                break;
            }
            case BlockEntityKind.Sign:
            {
                // SignRenderer: translate (.5, .5, .5), turn by minus the sign's yaw; a wall sign is moved back to its wall (0, -0.3125, -0.4375); everything scaled (2/3, -2/3, -2/3)
                var s = new Stack().Translate(0.5f, 0.5f, 0.5f).RotateY(-yaw);
                if (spot.Part == 1) s.Translate(0f, -0.3125f, -0.4375f);
                s.Scale(0.6666667f, -0.6666667f, -0.6666667f);
                Add(Parts(spot.Part == 1 ? "SignRenderer.createSignLayer(1)" : "SignRenderer.createSignLayer(1)#1"), s.M, x, y, z, $"signs/{BlockEntityKinds.Woods[spot.Variant % 12]}", Vector3.One, output);
                break;
            }
            case BlockEntityKind.HangingSign:
            {
                // HangingSignRenderer: translate (.5, 15/16, .5), turn by minus the sign's yaw, translate (0, -0.3125, 0), flip (1, -1, -1); the model for how it is held (wall, chains, a V of chains)
                var s = new Stack().Translate(0.5f, 0.9375f, 0.5f).RotateY(-yaw).Translate(0f, -0.3125f, 0f).Scale(1f, -1f, -1f);
                string key = spot.Part switch { 1 => "HangingSignRenderer.createHangingSignLayer(1)", 2 => "HangingSignRenderer.createHangingSignLayer(1)#2", _ => "HangingSignRenderer.createHangingSignLayer(1)#1" };
                Add(Parts(key), s.M, x, y, z, $"signs/hanging/{BlockEntityKinds.Woods[spot.Variant % 12]}", Vector3.One, output);
                break;
            }
            case BlockEntityKind.Banner:
            {
                // BannerRenderer: translate (.5, 0, .5), turn by minus the banner's yaw, scale (2/3, -2/3, -2/3): the pole and bar, and the flag under it, the flag's wave not drawn
                var s = new Stack().Translate(0.5f, 0f, 0.5f).RotateY(-yaw).Scale(0.6666667f, -0.6666667f, -0.6666667f);
                bool standing = spot.Part == 0;
                var tint = EntityPoser.DyeColour(BlockEntityKinds.Dyes[spot.Variant % 16]);
                Add(Parts(standing ? "BannerModel.createBodyLayer(1)#1" : "BannerModel.createBodyLayer(1)"), s.M, x, y, z, "banner_base", Vector3.One, output);
                Add(Parts(standing ? "BannerFlagModel.createFlagLayer(1)#1" : "BannerFlagModel.createFlagLayer(1)"), s.M, x, y, z, "banner_base", tint, output);
                break;
            }
            case BlockEntityKind.Skull:
            {
                // SkullBlockRenderer: a head on the ground stands at the block's foot (.5, 0, .5), one on a wall is moved a quarter toward the wall and up a quarter; the model is upside down (-1, -1, 1)
                // and its head turned by the block's yaw. Piglin and dragon heads have models of their own that are not drawn.
                string key, texture;
                switch (spot.Variant)
                {
                    case 0: key = "SkullModel.createMobHeadLayer"; texture = "skeleton/skeleton"; break;
                    case 1: key = "SkullModel.createMobHeadLayer"; texture = "skeleton/wither_skeleton"; break;
                    case 2: key = "SkullModel.createHumanoidHeadLayer"; texture = "zombie/zombie"; break;
                    case 3: key = "SkullModel.createMobHeadLayer"; texture = "creeper/creeper"; break;
                    case 6: key = "SkullModel.createHumanoidHeadLayer"; texture = "player/wide/steve"; break;
                    default: return;
                }
                var s = new Stack();
                if (spot.Part == 1)
                {
                    // Facing a direction, the head hangs on the wall behind it: toward the opposite side of where it looks
                    float dx = -MathF.Round(MathF.Sin(yaw * MathF.PI / 180f)), dz = MathF.Round(MathF.Cos(yaw * MathF.PI / 180f));   // the direction it faces
                    s.Translate(0.5f - dx * 0.25f, 0.25f, 0.5f - dz * 0.25f);
                }
                else s.Translate(0.5f, 0f, 0.5f);
                s.Scale(-1f, -1f, 1f).RotateY(yaw);
                Add(Parts(key), s.M, x, y, z, texture, Vector3.One, output);
                break;
            }
        }
    }

    /// <summary>
    /// The layers of a banner's patterns, over its base: each the flag's boxes again in the layer's pattern texture, tinted the layer's dye, a hair thicker than the one before so it lies over it.
    /// </summary>
    public static void PoseBannerLayers(in BlockEntitySpot spot, float x, float y, float z, (byte Pattern, byte Color)[] layers, List<PartDraw> output)
    {
        float yaw = spot.Orient * Step;
        var s = new Stack().Translate(0.5f, 0f, 0.5f).RotateY(-yaw).Scale(0.6666667f, -0.6666667f, -0.6666667f);
        bool standing = spot.Part == 0;
        var flag = Parts(standing ? "BannerFlagModel.createFlagLayer(1)#1" : "BannerFlagModel.createFlagLayer(1)");
        for (int i = 0; i < layers.Length; i++)
        {
            if (layers[i].Pattern >= BannerPatterns.Names.Length) continue;
            int before = output.Count;
            Add(flag, s.M, x, y, z, BannerPatterns.TextureOf(layers[i].Pattern), EntityPoser.DyeColour(BlockEntityKinds.Dyes[layers[i].Color % 16]), output);
            for (int d = before; d < output.Count; d++) { var draw = output[d]; draw.Inflate += 0.015f * (i + 1); output[d] = draw; }
        }
    }

    /// <summary>
    /// The quad that carries the writing on a sign's board: a flat box with no depth, just in front of the board's front face, <paramref name="texture"/> its picture (four picture pixels to a model pixel:
    /// 96 x 48 on a standing or wall sign's board, 56 x 40 on a hanging sign's). Its back face reads the picture's clear right half, so the other side of the sign shows nothing.
    /// </summary>
    public static void PoseSignText(in BlockEntitySpot spot, float x, float y, float z, string texture, List<PartDraw> output)
    {
        float yaw = spot.Orient * Step;
        Stack s;
        Vector3 origin, size;
        if (spot.Kind == BlockEntityKind.HangingSign)
        {
            s = new Stack().Translate(0.5f, 0.9375f, 0.5f).RotateY(-yaw).Translate(0f, -0.3125f, 0f).Scale(1f, -1f, -1f);
            origin = new Vector3(-28f, 0f, -4.2f);
            size = new Vector3(56f, 40f, 0f);
        }
        else
        {
            s = new Stack().Translate(0.5f, 0.5f, 0.5f).RotateY(-yaw);
            if (spot.Part == 1) s.Translate(0f, -0.3125f, -0.4375f);
            s.Scale(0.6666667f, -0.6666667f, -0.6666667f);
            origin = new Vector3(-48f, -56f, -4.2f);
            size = new Vector3(96f, 48f, 0f);
        }
        output.Add(new PartDraw
        {
            Model = Matrix4x4.CreateScale(0.25f) * Matrix4x4.CreateScale(1f / 16f) * s.M * Matrix4x4.CreateTranslation(x, y, z),
            Origin = origin, Size = size, Texture = texture, Tint = Vector3.One,
        });
    }

    private static void Add(ModelPart[] parts, Matrix4x4 stack, float x, float y, float z, string texture, Vector3 tint, List<PartDraw> output)
    {
        var world = new Matrix4x4[parts.Length];
        // model pixels -> blocks, then the renderer's moves, then the block's place
        var toWorld = Matrix4x4.CreateScale(1f / 16f) * stack * Matrix4x4.CreateTranslation(x, y, z);
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var local = Matrix4x4.CreateRotationX(part.Rotation.X) * Matrix4x4.CreateRotationY(part.Rotation.Y) * Matrix4x4.CreateRotationZ(part.Rotation.Z) * Matrix4x4.CreateTranslation(part.Pivot);
            if (part.PoseScale is { } poseScale) local = Matrix4x4.CreateScale(poseScale) * local;
            world[i] = part.Parent >= 0 ? local * world[part.Parent] : local;
            foreach (var box in part.Boxes)
                output.Add(new PartDraw
                {
                    Model = world[i] * toWorld,
                    Origin = new Vector3(box.X, box.Y, box.Z),
                    Inflate = box.Inflate,
                    Size = new Vector3(box.W, box.H, box.D),
                    Mirror = box.Mirror,
                    Texture = texture,
                    U = box.U,
                    V = box.V,
                    Tint = tint,
                });
        }
    }
}
