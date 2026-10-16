namespace AubsCraft.Admin.Rendering;

/// <summary>
/// How far the world is worth drawing, and meshing. Fog is total from <see cref="DayLighting.FogEnd"/> blocks on (the shader blends to the
/// fog colour with the square of the distance fraction, so past the end a fragment IS the fog colour): terrain beyond it costs draws and
/// shows nothing. Everything here derives from that one distance.
///
/// Far chunks (heightmaps) keep their small GPU buffer for the whole session but only hold a MESH while they are within reach of the
/// camera: a mesh is made when a chunk comes within <see cref="MeshMargin"/> chunks of the draw radius and given up when it falls
/// <see cref="FreeMargin"/> beyond it (the gap between the two keeps a camera hovering at the border from meshing and freeing the same
/// chunks over and over).
/// </summary>
public static class FarWindow
{
    /// <summary>Chunks past the draw radius that already have a mesh, so turning towards the edge does not show a gap filling in.</summary>
    public const int MeshMargin = 2;

    /// <summary>Chunks past the draw radius at which a mesh is given up. Larger than <see cref="MeshMargin"/> on purpose.</summary>
    public const int FreeMargin = 6;

    /// <summary>The smallest draw distance the adaptive policy goes down to (chunks).</summary>
    public const int MinDrawDistance = 12;

    /// <summary>The largest: the fog distance. Past it nothing shows.</summary>
    public const int MaxDrawDistance = DayLighting.FogEndChunks;

    /// <summary>The draw distance grows at or above this frame rate (a 60 Hz display tops out at 60) and shrinks below <see cref="ShrinkBelowFps"/>; between the two it stays.</summary>
    public const float GrowAtFps = 57f;
    public const float ShrinkBelowFps = 48f;

    /// <summary>
    /// The adaptive draw distance: one chunk at a time, so a weak GPU settles where it can hold the frame rate (like a game's render distance)
    /// and a strong one stays at the fog distance.
    /// </summary>
    public static int NextDrawDistance(int current, float fps)
    {
        if (fps >= GrowAtFps) return Math.Min(current + 1, MaxDrawDistance);
        if (fps < ShrinkBelowFps) return Math.Max(current - 1, MinDrawDistance);
        return current;
    }

    /// <summary>A headset draws two eyes: its draw distance may go lower than a screen's.</summary>
    public const int MinXrDrawDistance = 8;

    /// <summary>A headset's frame is spent when the CPU needs more than this share of the display's period; with less than <see cref="XrGrowBelow"/> there is room to draw farther.</summary>
    public const float XrShrinkAbove = 0.80f;
    public const float XrGrowBelow = 0.55f;

    /// <summary>How many checks (half a second apart) in a row must find room before a headset's draw distance grows a chunk.</summary>
    public const int XrGrowAfterChecks = 12;

    /// <summary>
    /// A headset's draw distance. Its frame rate is pinned to the display's refresh (72 or 90 Hz) whatever the cost, so frame rate cannot steer it
    /// (it would grow to the fog distance and never shrink until frames were already dropping); the CPU time of a frame, against the display's period, can.
    /// </summary>
    public static int NextXrDrawDistance(int current, float cpuMs, float periodMs)
    {
        if (cpuMs > periodMs * XrShrinkAbove) return Math.Max(current - 1, MinXrDrawDistance);
        if (cpuMs < periodMs * XrGrowBelow) return Math.Min(current + 1, MaxDrawDistance);
        return current;
    }

    /// <summary>
    /// Where fog is total at this draw distance (blocks): the nearest block of a chunk at the draw radius is at least radius * 16 - 24 from the
    /// camera, so terrain at the radius is fully fogged and what lies beyond (not drawn) is indistinguishable from it. At the largest distance
    /// this is <see cref="DayLighting.FogEnd"/>: nothing changes for a machine that can draw it all.
    /// </summary>
    public static float FogEndBlocks(int drawDistanceChunks) => Math.Min(DayLighting.FogEnd, CullRadius(drawDistanceChunks) * 16f - 24f);

    /// <summary>Where fog starts at this draw distance: the same share of the way as at the full distance (250 of 450).</summary>
    public static float FogStartBlocks(int drawDistanceChunks) => FogEndBlocks(drawDistanceChunks) * (DayLighting.FogStart / DayLighting.FogEnd);

    /// <summary>The draw radius in chunks: what the camera asked for, but never past the fog (nothing there is visible).</summary>
    public static int CullRadius(int drawDistanceChunks) => Math.Clamp(drawDistanceChunks, 1, DayLighting.FogEndChunks);

    /// <summary>A chunk at this squared distance (in chunks) from the camera's chunk should have a mesh when it is at most this.</summary>
    public static long MeshRadiusSq(int drawDistanceChunks)
    {
        long r = CullRadius(drawDistanceChunks) + MeshMargin;
        return r * r;
    }

    /// <summary>A chunk farther than this squared distance (in chunks) gives its mesh up.</summary>
    public static long FreeRadiusSq(int drawDistanceChunks)
    {
        long r = CullRadius(drawDistanceChunks) + FreeMargin;
        return r * r;
    }

    public static long DistanceSq(int cx, int cz, int camCX, int camCZ)
    {
        long dx = cx - camCX, dz = cz - camCZ;
        return dx * dx + dz * dz;
    }

    public static bool ShouldMesh(int cx, int cz, int camCX, int camCZ, int drawDistanceChunks)
        => DistanceSq(cx, cz, camCX, camCZ) <= MeshRadiusSq(drawDistanceChunks);

    public static bool ShouldFree(int cx, int cz, int camCX, int camCZ, int drawDistanceChunks)
        => DistanceSq(cx, cz, camCX, camCZ) > FreeRadiusSq(drawDistanceChunks);
}
