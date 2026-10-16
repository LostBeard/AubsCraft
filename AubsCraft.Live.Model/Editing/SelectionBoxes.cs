using System.Numerics;
using AubsCraft.Live.Model.Entities;

namespace AubsCraft.Live.Model.Editing;

/// <summary>The boxes that draw an outline round a block (or a box of blocks): twelve thin bars, as the entity pipeline draws any box, in the flat "white" texture tinted to a colour.</summary>
public static class SelectionBoxes
{
    /// <summary>The bar's thickness in model pixels (16 to a block).</summary>
    public const float Bar = 0.7f;

    /// <summary>Append the outline of the blocks from (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) over <paramref name="w"/> x <paramref name="h"/> x <paramref name="d"/> blocks.</summary>
    public static void Outline(float x, float y, float z, int w, int h, int d, Vector3 tint, List<PartDraw> output, float bar = Bar)
    {
        float W = w * 16f, H = h * 16f, D = d * 16f, t = bar / 2f;
        var model = Matrix4x4.CreateScale(1f / 16f) * Matrix4x4.CreateTranslation(x, y, z);
        void Box(float ox, float oy, float oz, float sx, float sy, float sz) => output.Add(new PartDraw
        {
            Model = model,
            Origin = new Vector3(ox, oy, oz),
            Size = new Vector3(sx, sy, sz),
            Texture = "white",
            Tint = tint,
        });
        foreach (float yy in new[] { 0f, H })
            foreach (float zz in new[] { 0f, D })
                Box(-t, yy - t, zz - t, W + bar, bar, bar);          // along x
        foreach (float xx in new[] { 0f, W })
            foreach (float zz in new[] { 0f, D })
                Box(xx - t, -t, zz - t, bar, H + bar, bar);          // along y
        foreach (float xx in new[] { 0f, W })
            foreach (float yy in new[] { 0f, H })
                Box(xx - t, yy - t, -t, bar, bar, D + bar);          // along z
    }

    /// <summary>
    /// A thin bar from <paramref name="origin"/> along <paramref name="direction"/> (unit) for <paramref name="length"/> blocks: the beam of a controller in a headset, in the flat "white" texture tinted to a colour.
    /// <paramref name="thickness"/> is in blocks.
    /// </summary>
    public static void Beam(Vector3 origin, Vector3 direction, float length, float thickness, Vector3 tint, List<PartDraw> output)
    {
        if (length <= 0f || direction.LengthSquared() < 1e-6f) return;
        direction = Vector3.Normalize(direction);
        // Pointing nearly straight up or down, "up" must be something else
        var up = MathF.Abs(direction.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
        // CreateWorld sends the object's -Z to its "forward": the bar runs along +Z, so give it the way back
        var world = Matrix4x4.CreateWorld(origin, -direction, up);
        float t = thickness * 16f / 2f;
        output.Add(new PartDraw
        {
            Model = Matrix4x4.CreateScale(1f / 16f) * world,
            Origin = new Vector3(-t, -t, 0f),
            Size = new Vector3(2f * t, 2f * t, length * 16f),
            Texture = "white",
            Tint = tint,
        });
    }

    /// <summary>A small box for a hand: <paramref name="size"/> blocks across, centred on <paramref name="center"/>.</summary>
    public static void Cube(Vector3 center, float size, Vector3 tint, List<PartDraw> output)
    {
        float s = size * 16f;
        output.Add(new PartDraw
        {
            Model = Matrix4x4.CreateScale(1f / 16f) * Matrix4x4.CreateTranslation(center),
            Origin = new Vector3(-s / 2f, -s / 2f, -s / 2f),
            Size = new Vector3(s, s, s),
            Texture = "white",
            Tint = tint,
        });
    }
}