using System.Numerics;

namespace AubsCraft.Live.Model.Entities;

/// <summary>
/// Puts a creature's model in the world for one moment: the entity's place and heading, a walk cycle from how far it has walked, the head turned to where it
/// looks, wings flapping off the ground. The arithmetic is the game's (its renderer's matrix order, its limb swing formulas), so a model built from the game's
/// own box numbers stands, faces and walks the way it does in the game. The output is one <see cref="PartDraw"/> a box.
/// </summary>
public static class EntityPoser
{
    private const float Deg = MathF.PI / 180f;

    /// <summary>Dye colours of sheep wool (the game's texture diffuse colours), by the dye's lowercase name.</summary>
    public static Vector3 DyeColour(string? dye) => dye switch
    {
        "orange" => Rgb(0xF9801D), "magenta" => Rgb(0xC74EBD), "light_blue" => Rgb(0x3AB3DA), "yellow" => Rgb(0xFED83D), "lime" => Rgb(0x80C71F),
        "pink" => Rgb(0xF38BAA), "gray" => Rgb(0x474F52), "light_gray" => Rgb(0x9D9D97), "cyan" => Rgb(0x169C9C), "purple" => Rgb(0x8932B8),
        "blue" => Rgb(0x3C44AA), "brown" => Rgb(0x835432), "green" => Rgb(0x5E7C16), "red" => Rgb(0xB02E26), "black" => Rgb(0x1D1D21),
        _ => Rgb(0xF9FFFE),
    };

    private static Vector3 Rgb(int rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);

    /// <summary>
    /// The matrix that takes model pixels to world blocks for an entity at (x, y, z) facing <paramref name="bodyYawDegrees"/> (Minecraft's yaw: 0 = south,
    /// 90 = west): the game's renderer order - 1/16 scale, the 1.501 lift, the creature's scale, the flip of x and y (the model's y points down), the turn
    /// of 180 minus the yaw, then the place.
    /// </summary>
    public static Matrix4x4 Root(float x, float y, float z, float bodyYawDegrees, float scale) =>
        Matrix4x4.CreateScale(1f / 16f)
        * Matrix4x4.CreateTranslation(0f, -1.501f, 0f)
        * Matrix4x4.CreateScale(scale)
        * Matrix4x4.CreateScale(-1f, -1f, 1f)
        * Matrix4x4.CreateRotationY((180f - bodyYawDegrees) * Deg)
        * Matrix4x4.CreateTranslation(x, y, z);

    /// <summary>The head's turn relative to the body, in degrees: what the head looks at minus where the body faces, at most 75 either way.</summary>
    public static float HeadYawRelative(float headYaw, float bodyYaw)
    {
        float d = ((headYaw - bodyYaw) % 360f + 540f) % 360f - 180f;
        return Math.Clamp(d, -75f, 75f);
    }

    /// <summary>Append the boxes of <paramref name="model"/> as it stands at <paramref name="view"/>.</summary>
    /// <param name="ageTicks">A clock in ticks (idle sway, wing flap): any monotonic time.</param>
    /// <param name="texture">Replaces the model's own texture where it names the "player" texture (a player's skin); null leaves it.</param>
    /// <param name="coarse">A creature far away: only its head and body are drawn (limbs and overlay layers are a few pixels there), a third of the boxes.</param>
    public static void Pose(in LiveEntityView view, EntityModel model, float ageTicks, List<PartDraw> output, string? texture = null, bool coarse = false)
    {
        float scale = (view.Baby ? model.BabyScale : 1f) * model.Scale;
        var root = Root(view.X, view.Y, view.Z, view.BodyYaw, scale);
        float headYaw = HeadYawRelative(view.Yaw, view.BodyYaw) * Deg;
        float headPitch = Math.Clamp(view.Pitch, -90f, 90f) * Deg;
        float swing = view.WalkedBlocks * 4f * (view.Baby ? 3f : 1f);
        float amount = Math.Min(view.SpeedBlocksPerTick * 4f, 1f);
        var dye = DyeColour(view.Variant);

        var parts = model.Parts;
        if (coarse && !model.HasHeadOrBody) coarse = false;   // a one-box model is already as coarse as it gets
        Span<Matrix4x4> world = parts.Count <= 32 ? stackalloc Matrix4x4[parts.Count] : new Matrix4x4[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            if (coarse && part.Role is not (PartRole.Head or PartRole.Body)) continue;
            var rot = part.Rotation + Animation(model, part.Role, view, swing, amount, headYaw, headPitch, ageTicks);
            var local = Matrix4x4.CreateRotationX(rot.X) * Matrix4x4.CreateRotationY(rot.Y) * Matrix4x4.CreateRotationZ(rot.Z)
                        * Matrix4x4.CreateTranslation(part.Pivot);
            world[i] = part.Parent >= 0 ? local * world[part.Parent] : local;

            var model2 = world[i] * root;
            string? partTexture = part.Texture;
            if (part.VariantTexture != null && (partTexture = part.VariantTexture(view.Variant)) == null) continue;   // this variant has no such layer
            foreach (var box in part.Boxes)
            {
                output.Add(new PartDraw
                {
                    Model = model2,
                    Origin = new Vector3(box.X, box.Y, box.Z),
                    Inflate = box.Inflate,
                    Size = new Vector3(box.W, box.H, box.D),
                    Mirror = box.Mirror,
                    Texture = partTexture ?? (texture != null && model.Texture == "player" ? texture : model.TextureByVariant?.Invoke(view.Variant) ?? model.Texture),
                    U = box.U,
                    V = box.V,
                    Tint = part.Dyed ? dye : Vector3.One,
                });
            }
        }
    }

    // The rotation a part gets on top of its rest rotation (radians about x, y, z)
    private static Vector3 Animation(EntityModel model, PartRole role, in LiveEntityView view, float swing, float amount, float headYaw, float headPitch, float age)
    {
        float cosA = MathF.Cos(swing * 0.6662f);
        float cosB = MathF.Cos(swing * 0.6662f + MathF.PI);
        switch (role)
        {
            case PartRole.Head:
                return new Vector3(headPitch, headYaw, 0f);

            case PartRole.RightHindLeg or PartRole.LeftFrontLeg:
                return new Vector3(cosA * 1.4f * amount, 0f, 0f);
            case PartRole.LeftHindLeg or PartRole.RightFrontLeg:
                return new Vector3(cosB * 1.4f * amount, 0f, 0f);

            case PartRole.RightLeg:
                return new Vector3(cosA * 1.4f * amount * (model.Rig == EntityRig.Villager ? 0.5f : 1f), 0f, 0f);
            case PartRole.LeftLeg:
                return new Vector3(cosB * 1.4f * amount * (model.Rig == EntityRig.Villager ? 0.5f : 1f), 0f, 0f);

            case PartRole.RightArm:
                return Arm(model, cosB * amount, age, right: true);
            case PartRole.LeftArm:
                return Arm(model, cosA * amount, age, right: false);

            case PartRole.RightWing:
                return view.OnGround ? Vector3.Zero : new Vector3(0f, 0f, FlapAngle(age));
            case PartRole.LeftWing:
                return view.OnGround ? Vector3.Zero : new Vector3(0f, 0f, -FlapAngle(age));

            default:
                return Vector3.Zero;
        }
    }

    private static Vector3 Arm(EntityModel model, float swing, float age, bool right)
    {
        // The game's idle sway: arms drift a little in and out and back and forth
        float side = right ? 1f : -1f;
        float sway = MathF.Cos(age * 0.09f) * 0.05f + 0.05f;
        float sway2 = MathF.Sin(age * 0.067f) * 0.05f;
        if (model.ArmsForward)
            return new Vector3(-1.396f + sway2 * side, 0f, sway * side);   // held out in front, a little unsteady
        return new Vector3(swing * 2f * 0.5f + sway2 * side, 0f, sway * side);
    }

    private static float FlapAngle(float age) => MathF.Abs(MathF.Sin(age * 0.9f)) * 1.5f;
}
