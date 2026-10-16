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
}
