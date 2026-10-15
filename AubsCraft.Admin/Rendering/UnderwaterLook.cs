using System.Numerics;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// What the world looks like with the camera under water: everything fades into a blue-green murk a few dozen blocks away (the game's own water fog), darker at night, and the
/// frame is cleared to that colour. Whether the camera IS under water is decided by <see cref="VoxelWorldRenderer"/> (its eye cell is water with water, or the water's surface, above it).
/// </summary>
public static class UnderwaterLook
{
    /// <summary>The murk's colour at full daylight; scaled by the light of the hour.</summary>
    public static readonly Vector3 Murk = new(0.12f, 0.27f, 0.55f);

    /// <summary>Visibility: fog starts one block out and is total at this many blocks.</summary>
    public const float FogStart = 1f, FogEnd = 26f;

    /// <summary>The fog colour for an hour whose sun strength is <paramref name="sunStrength"/> (0 at night, 1 at noon): water is never fully black, never brighter than by day.</summary>
    public static Vector3 FogColor(float sunStrength) => Murk * Math.Clamp(sunStrength * 0.75f + 0.25f, 0.2f, 1f);

    /// <summary>
    /// Whether an eye at height <paramref name="eyeY"/> in a cell that holds water is under it: the water in that cell is lowered a tenth from its top when nothing
    /// is above it (a surface), so an eye in the top tenth of a surface cell is out of the water.
    /// </summary>
    public static bool IsUnder(bool cellIsWater, bool cellAboveIsWater, float eyeY)
    {
        if (!cellIsWater) return false;
        if (cellAboveIsWater) return true;
        return eyeY - MathF.Floor(eyeY) < 0.9f;
    }
}
