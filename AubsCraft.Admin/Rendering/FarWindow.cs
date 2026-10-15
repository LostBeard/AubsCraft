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
