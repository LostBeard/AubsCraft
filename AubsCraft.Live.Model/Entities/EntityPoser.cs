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

    /// <summary>The <paramref name="slot"/>th "|"-separated part of a variant (0 = the first: the pattern), or null.</summary>
    public static string? Component(string? variant, int slot)
    {
        if (variant == null) return null;
        var parts = variant.Split('|');
        return slot < parts.Length ? parts[slot] : null;
    }

    private static Vector3 Rgb(int rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);

    /// <summary>
    /// The matrix that takes model pixels to world blocks for an entity at (x, y, z) facing <paramref name="bodyYawDegrees"/> (Minecraft's yaw: 0 = south,
    /// 90 = west): the game's renderer order - 1/16 scale, the 1.501 lift, the creature's scale, the flip of x and y (the model's y points down), the turn
    /// of 180 minus the yaw, then the place.
    /// </summary>
    public static Matrix4x4 Root(float x, float y, float z, float bodyYawDegrees, float scale, Vector3 modelOffset = default, float modelYawDegrees = 0f, Matrix4x4? entityFrame = null) =>
        Matrix4x4.CreateScale(1f / 16f)
        * Matrix4x4.CreateRotationY(modelYawDegrees * Deg)
        * Matrix4x4.CreateTranslation(modelOffset.X, modelOffset.Y - 1.501f, modelOffset.Z)
        * Matrix4x4.CreateScale(scale)
        * Matrix4x4.CreateScale(-1f, -1f, 1f)
        * (entityFrame ?? Matrix4x4.Identity)
        * Matrix4x4.CreateRotationY((180f - bodyYawDegrees) * Deg)
        * Matrix4x4.CreateTranslation(x, y, z);

    /// <summary>How a creature stands or lies, from its flags.</summary>
    public enum Stance { Upright, Sneaking, Riding, Swimming, Gliding, Sleeping, Dead }

    /// <summary>The stance the flags say (a dead creature is on its side whatever else it was doing).</summary>
    public static Stance StanceOf(in LiveEntityView view, EntityModel model)
    {
        bool humanoid = model.Rig == EntityRig.Biped;
        if (view.Dead) return Stance.Dead;
        if (view.Sleeping && humanoid) return Stance.Sleeping;
        if (view.Swimming && humanoid) return Stance.Swimming;
        if (view.Gliding && humanoid) return Stance.Gliding;
        if (view.Riding && humanoid) return Stance.Riding;
        if (view.Sneaking && humanoid) return Stance.Sneaking;
        return Stance.Upright;
    }

    // What the renderer does to the whole creature (in its own frame, after the model is flipped and before it is turned to face): lie it down, tip it over
    private static Matrix4x4? FrameOf(Stance stance, in LiveEntityView view) => stance switch
    {
        // The game swims a creature flat: down a block and back a third, then pitched to its look
        Stance.Swimming => Matrix4x4.CreateTranslation(0f, -1f, 0.3f) * Matrix4x4.CreateRotationX((-90f - Math.Clamp(view.Pitch, -90f, 90f)) * Deg),
        Stance.Gliding => Matrix4x4.CreateRotationX((-90f - Math.Clamp(view.Pitch, -90f, 90f)) * Deg),
        // On its side
        Stance.Dead => Matrix4x4.CreateRotationZ(90f * Deg),
        // On its back (its head toward the head of the bed it is in)
        Stance.Sleeping => Matrix4x4.CreateRotationY(270f * Deg) * Matrix4x4.CreateRotationZ(90f * Deg),
        _ => null,
    };

    // Where a part of a humanoid sits and turns in a stance: (a move of its pivot in model px, a turn added to its own, whether the walk's swing still applies)
    private static (Vector3 shift, Vector3 turn, bool swings) Tweak(Stance stance, PartRole role, float age)
    {
        switch (stance)
        {
            case Stance.Sneaking:
                // HumanoidModel crouching: the body leans (0.5), the arms go with it, the legs step back and the head and arms drop with the body
                return role switch
                {
                    PartRole.Head => (new Vector3(0f, 4.2f, 0f), Vector3.Zero, true),
                    PartRole.Body => (new Vector3(0f, 3.2f, 0f), new Vector3(0.5f, 0f, 0f), true),
                    PartRole.RightArm or PartRole.LeftArm => (new Vector3(0f, 3.2f, 0f), new Vector3(0.4f, 0f, 0f), true),
                    PartRole.RightLeg or PartRole.LeftLeg => (new Vector3(0f, 0.2f, 3.9f), Vector3.Zero, true),
                    _ => (Vector3.Zero, Vector3.Zero, true),
                };
            case Stance.Riding:
                // Sitting: the legs out in front and a little apart, the arms a little forward
                return role switch
                {
                    PartRole.RightLeg => (Vector3.Zero, new Vector3(-1.4137f, 0.3142f, 0f), false),
                    PartRole.LeftLeg => (Vector3.Zero, new Vector3(-1.4137f, -0.3142f, 0f), false),
                    PartRole.RightArm or PartRole.LeftArm => (Vector3.Zero, new Vector3(-0.6283f, 0f, 0f), false),
                    _ => (Vector3.Zero, Vector3.Zero, false),
                };
            case Stance.Swimming:
                // The crawl: the arms reach over the head in turn, the legs kick
                return role switch
                {
                    PartRole.RightArm => (Vector3.Zero, new Vector3(-2.9f + 0.7f * MathF.Cos(age * 0.35f), 0f, 0f), false),
                    PartRole.LeftArm => (Vector3.Zero, new Vector3(-2.9f + 0.7f * MathF.Cos(age * 0.35f + MathF.PI), 0f, 0f), false),
                    PartRole.RightLeg => (Vector3.Zero, new Vector3(0.5f * MathF.Cos(age * 0.7f), 0f, 0f), false),
                    PartRole.LeftLeg => (Vector3.Zero, new Vector3(0.5f * MathF.Cos(age * 0.7f + MathF.PI), 0f, 0f), false),
                    _ => (Vector3.Zero, Vector3.Zero, false),
                };
            case Stance.Gliding:
            case Stance.Sleeping:
                return (Vector3.Zero, Vector3.Zero, false);
            default:
                return (Vector3.Zero, Vector3.Zero, true);
        }
    }

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
    /// <param name="sleepYaw">A creature asleep lies along its bed: the yaw the bed faces (the game's own numbers: south 0, west 90, north 180, east 270); null: along the way it faces.</param>
    /// <param name="holding">The creature holds something in its right hand: the arm is raised a little to carry it.</param>
    /// <returns>The matrix that takes the right arm's own space (model pixels, the origin at its shoulder) to the world, for whatever is held in that hand; null when the model has no right arm (or it was left out).</returns>
    /// <param name="heldItem">What it holds (its name), when that decides how it stands: a bow is drawn with both arms forward.</param>
    public static Matrix4x4? Pose(in LiveEntityView view, EntityModel model, float ageTicks, List<PartDraw> output, string? texture = null, bool coarse = false, float? sleepYaw = null, bool holding = false, string? heldItem = null)
    {
        Matrix4x4? rightArm = null;
        float scale = (view.Baby ? model.BabyScale : 1f) * model.Scale;
        var stance = StanceOf(in view, model);
        // A player who sneaks is drawn an eighth lower
        float lift = stance == Stance.Sneaking && view.IsPlayer ? -0.125f : 0f;
        // On a bed the turn is the bed's, not the creature's: the game turns a sleeper to the bed (south 90, west 0, north 270, east 180) where it would turn it to 180 minus its yaw
        float bodyYaw = view.BodyYaw;
        if (stance == Stance.Sleeping && sleepYaw is { } facing) bodyYaw = 180f - ((90f - facing) % 360f + 360f) % 360f;
        float x = view.X, z = view.Z;
        if (stance == Stance.Sleeping && sleepYaw is { } bed)
        {
            // The game moves a sleeper back along the bed by the height of its eyes, so its head, not its feet, is where the entity is
            float turn = bed * Deg;
            x += MathF.Sin(turn) * 1.52f;
            z -= MathF.Cos(turn) * 1.52f;
        }
        var root = model.Projectile
            ? Matrix4x4.CreateScale(1f / 16f) * Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationZ(view.Pitch * Deg) * Matrix4x4.CreateRotationY((view.Yaw - 90f) * Deg) * Matrix4x4.CreateTranslation(x, view.Y, z)
            : Root(x, view.Y + lift, z, bodyYaw, scale, model.ModelOffset, model.ModelYawDegrees, FrameOf(stance, in view));
        float headYaw = HeadYawRelative(view.Yaw, view.BodyYaw) * Deg;
        float headPitch = Math.Clamp(view.Pitch, -90f, 90f) * Deg;
        float swing = view.WalkedBlocks * 4f * (view.Baby ? 3f : 1f);
        float amount = Math.Min(view.SpeedBlocksPerTick * 4f, 1f);
        if (stance is Stance.Riding or Stance.Gliding or Stance.Sleeping or Stance.Swimming) amount = 0f;
        var dye = DyeColour(view.Variant);
        // A player's armor (the variant of a player says it); a far player is not worth the extra boxes
        var worn = model.Texture == "player" && !coarse ? WornArmor.Parse(view.Variant) : default;

        var parts = model.Parts;
        if (coarse && !model.HasHeadOrBody) coarse = false;   // a one-box model is already as coarse as it gets
        Span<Matrix4x4> world = parts.Count <= 32 ? stackalloc Matrix4x4[parts.Count] : new Matrix4x4[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            if (coarse && part.Role is not (PartRole.Head or PartRole.Body)) continue;
            var (shift, turn, swings) = stance == Stance.Upright || stance == Stance.Dead ? (Vector3.Zero, Vector3.Zero, true) : Tweak(stance, part.Role, ageTicks);
            var rot = part.Rotation + Animation(model, part.Role, view, swings ? swing : 0f, swings ? amount : 0f, headYaw, headPitch, ageTicks) + turn;
            // The body is already pitched to the look of a swimmer or a glider: its head does not tip again
            if (part.Role == PartRole.Head && stance is Stance.Swimming or Stance.Gliding) rot.X = part.Rotation.X;
            // An arm that carries something is held a little forward, its swing halved
            if (holding && part.Role == PartRole.RightArm && stance is Stance.Upright or Stance.Sneaking) rot.X = rot.X * 0.5f - 0.3142f;
            if (stance is Stance.Upright or Stance.Sneaking && part.Role is PartRole.RightArm or PartRole.LeftArm) rot = Action(rot, part.Role, view, heldItem, headPitch, headYaw, ageTicks);
            var local = Matrix4x4.CreateRotationX(rot.X) * Matrix4x4.CreateRotationY(rot.Y) * Matrix4x4.CreateRotationZ(rot.Z)
                        * Matrix4x4.CreateTranslation(part.Pivot + shift);
            if (part.PoseScale is { } poseScale) local = Matrix4x4.CreateScale(poseScale) * local;   // the game scales before it rotates and moves
            world[i] = part.Parent >= 0 ? local * world[part.Parent] : local;

            var model2 = world[i] * root;
            if (part.Role == PartRole.RightArm && part.Parent < 0) rightArm = model2;
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
                    Tint = part.Dyed ? (part.DyeSlot == 0 ? dye : DyeColour(Component(view.Variant, part.DyeSlot))) : Vector3.One,
                });
            }
            if (worn.Any && part.Parent < 0 && part.Role is PartRole.Head or PartRole.Body or PartRole.RightArm or PartRole.LeftArm or PartRole.RightLeg or PartRole.LeftLeg)
                ArmorLook.Add(output, model2, part.Role, worn);
        }
        return rightArm;
    }

    private static bool IsBow(string? item) => item is "bow" or "crossbow" or "minecraft:bow" or "minecraft:crossbow";

    // What an arm does while its creature hits or uses what it holds. A swing is the game's own (HumanoidModel.setupAttackAnimation, the progress looped over six ticks, the time a swing takes);
    // a drawn bow has both arms out along the look; anything else used (food, a potion) is brought up toward the face.
    private static Vector3 Action(Vector3 rot, PartRole role, in LiveEntityView view, string? heldItem, float headPitch, float headYaw, float age)
    {
        bool right = role == PartRole.RightArm;
        if (view.Using)
        {
            if (IsBow(heldItem)) return right ? new Vector3(-MathF.PI / 2f + headPitch, -0.1f + headYaw, rot.Z) : new Vector3(-MathF.PI / 2f + headPitch, 0.5f + headYaw, rot.Z);
            if (right) return new Vector3(-1.25f - headPitch * 0.3f, -0.4f, rot.Z);
            return rot;
        }
        if (view.Swinging && right)
        {
            float t = age % 6f / 6f;
            float f = 1f - (1f - t) * (1f - t) * (1f - t);
            float f1 = MathF.Sin(f * MathF.PI);
            float f2 = MathF.Sin(t * MathF.PI) * -(headPitch - 0.7f) * 0.75f;
            return new Vector3(rot.X - (f1 * 1.2f + f2), rot.Y, rot.Z - MathF.Sin(t * MathF.PI) * 0.4f);
        }
        return rot;
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
