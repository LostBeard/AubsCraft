using System.Numerics;

namespace AubsCraft.Live.Model.Entities;

/// <summary>
/// One textured box of a model, in Minecraft's model pixels (y points DOWN, a block is 16 px): <c>(U, V)</c> is where its texture layout starts in the
/// texture, <c>(X, Y, Z)</c> its minimum corner relative to the part's pivot, <c>(W, H, D)</c> its size. <see cref="Inflate"/> pushes every face outward
/// (an overlay layer); <see cref="Mirror"/> uses the mirror image of the texture (the left limbs share the right limbs' pixels).
/// </summary>
public readonly record struct ModelBox(float U, float V, float X, float Y, float Z, float W, float H, float D, float Inflate = 0f, bool Mirror = false);

/// <summary>What a part does when the creature moves: which animation drives it.</summary>
public enum PartRole
{
    Static, Body, Head,
    RightArm, LeftArm, RightLeg, LeftLeg,
    RightHindLeg, LeftHindLeg, RightFrontLeg, LeftFrontLeg,
    RightWing, LeftWing,
}

/// <summary>
/// A node of a model: a pivot (relative to its parent, model px), a rest rotation (radians, applied as Rz * Ry * Rx like the game), the boxes drawn at it
/// and a <see cref="PartRole"/>. A part with a <see cref="Parent"/> moves with it (a hat with the head, a sleeve with the arm). <see cref="Texture"/> overrides
/// the model's texture for this part (sheep wool); <see cref="Dyed"/> multiplies the creature's dye colour in. <see cref="VariantTexture"/> picks this part's texture
/// by the creature's variant and returns null to leave the part out (a horse's markings, a villager's profession clothes); <see cref="VariantTextureNames"/> lists what it can answer.
/// <see cref="PoseScale"/> scales the part and everything under it about its pivot (the game's own: an elder guardian's whole model is its guardian's at 2.35); null = 1.
/// A <see cref="Dyed"/> part takes its dye from the whole variant (a sheep's colour), or from the <see cref="DyeSlot"/>th of its "|"-separated parts (a tropical fish: pattern|body|pattern colour).
/// </summary>
public sealed record ModelPart(
    string Name, PartRole Role, int Parent, Vector3 Pivot, Vector3 Rotation, ModelBox[] Boxes,
    string? Texture = null, bool Dyed = false, Func<string?, string?>? VariantTexture = null, IReadOnlyList<string>? VariantTextureNames = null, int DyeSlot = 0,
    Vector3? PoseScale = null);

/// <summary>The animation a model's parts get: walk cycle, head look, wing flap.</summary>
public enum EntityRig { Static, Quadruped, Biped, Chicken, Villager, Flying }

/// <summary>
/// A creature's model: parts (parents before children) and the texture (a path under textures/entity, no extension). <see cref="Width"/> and
/// <see cref="Height"/> are the creature's size in blocks (a name tag floats above <see cref="Height"/>; the fallback box uses both).
/// </summary>
public sealed class EntityModel
{
    public required string Texture { get; init; }
    public required IReadOnlyList<ModelPart> Parts { get; init; }
    public EntityRig Rig { get; init; } = EntityRig.Static;
    public float Width { get; init; } = 0.6f;
    public float Height { get; init; } = 1.8f;
    /// <summary>Arms held out in front (a zombie's).</summary>
    public bool ArmsForward { get; init; }
    /// <summary>The scale of a baby of this kind.</summary>
    public float BabyScale { get; init; } = 0.5f;
    /// <summary>Picks the texture by the creature's variant (a wolf's coat, a cat's breed); null = <see cref="Texture"/> always.</summary>
    public Func<string?, string>? TextureByVariant { get; init; }
    /// <summary>Every texture <see cref="TextureByVariant"/> can answer, for the asset tool to pack.</summary>
    public IReadOnlyList<string> VariantTextures { get; init; } = Array.Empty<string>();
    /// <summary>Whether any part is a head or a body: what a far-away creature is reduced to.</summary>
    public bool HasHeadOrBody => _hasHeadOrBody ??= Parts.Any(p => p.Role is PartRole.Head or PartRole.Body);
    private bool? _hasHeadOrBody;
    /// <summary>The model's feet: the y (model px) the lowest part reaches; the game places it so this is the ground.</summary>
    public float FootY { get; init; } = 24f;
    /// <summary>The size the game draws this kind at, on top of its model (a ghast is 4.5 times its model, a donkey 0.87).</summary>
    public float Scale { get; init; } = 1f;
    /// <summary>What the game's renderer moves the model by before it draws it (blocks; the phantom is lifted 1.3125 and set back 0.1875), in the scaled frame.</summary>
    public Vector3 ModelOffset { get; init; }
    /// <summary>A turn about the vertical, in degrees, the renderer gives the model before anything else (a boat is drawn turned a quarter).</summary>
    public float ModelYawDegrees { get; init; }
    /// <summary>A projectile: drawn flying the way it points, turned by its yaw (about the vertical) and its pitch (up positive), not stood on the ground like a creature.</summary>
    public bool Projectile { get; init; }
}

/// <summary>One box ready to draw: its place in the world and where its texture is. The renderer turns the texture NAME into an atlas position.</summary>
public struct PartDraw
{
    /// <summary>Model pixels to world blocks (System.Numerics row-vector convention: <c>v * Model</c>).</summary>
    public Matrix4x4 Model;
    public Vector3 Origin;
    public float Inflate;
    public Vector3 Size;
    public bool Mirror;
    public string Texture;
    public float U, V;
    public Vector3 Tint;
}
