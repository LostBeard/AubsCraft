using System.Numerics;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// Extracts frustum planes from a View-Projection matrix and tests
/// axis-aligned bounding boxes for visibility. Uses the Gribb-Hartmann
/// method for plane extraction.
/// Adapted from Lost Spawns voxel engine.
/// </summary>
public static class FrustumCuller
{
    public struct Frustum
    {
        public Vector4 Left, Right, Bottom, Top, Near, Far;
    }

    public static Frustum ExtractPlanes(Matrix4x4 vp)
    {
        Frustum f;
        f.Left = new Vector4(vp.M14 + vp.M11, vp.M24 + vp.M21, vp.M34 + vp.M31, vp.M44 + vp.M41);
        f.Right = new Vector4(vp.M14 - vp.M11, vp.M24 - vp.M21, vp.M34 - vp.M31, vp.M44 - vp.M41);
        f.Bottom = new Vector4(vp.M14 + vp.M12, vp.M24 + vp.M22, vp.M34 + vp.M32, vp.M44 + vp.M42);
        f.Top = new Vector4(vp.M14 - vp.M12, vp.M24 - vp.M22, vp.M34 - vp.M32, vp.M44 - vp.M42);
        f.Near = new Vector4(vp.M14 + vp.M13, vp.M24 + vp.M23, vp.M34 + vp.M33, vp.M44 + vp.M43);
        f.Far = new Vector4(vp.M14 - vp.M13, vp.M24 - vp.M23, vp.M34 - vp.M33, vp.M44 - vp.M43);
        f.Left = NormalizePlane(f.Left);
        f.Right = NormalizePlane(f.Right);
        f.Bottom = NormalizePlane(f.Bottom);
        f.Top = NormalizePlane(f.Top);
        f.Near = NormalizePlane(f.Near);
        f.Far = NormalizePlane(f.Far);
        return f;
    }

    public static bool IsBoxVisible(in Frustum frustum, Vector3 min, Vector3 max)
    {
        if (!TestPlane(frustum.Left, min, max)) return false;
        if (!TestPlane(frustum.Right, min, max)) return false;
        if (!TestPlane(frustum.Bottom, min, max)) return false;
        if (!TestPlane(frustum.Top, min, max)) return false;
        if (!TestPlane(frustum.Near, min, max)) return false;
        if (!TestPlane(frustum.Far, min, max)) return false;
        return true;
    }

    private static bool TestPlane(Vector4 plane, Vector3 min, Vector3 max)
    {
        float px = plane.X >= 0 ? max.X : min.X;
        float py = plane.Y >= 0 ? max.Y : min.Y;
        float pz = plane.Z >= 0 ? max.Z : min.Z;
        return plane.X * px + plane.Y * py + plane.Z * pz + plane.W >= 0;
    }

    private static Vector4 NormalizePlane(Vector4 plane)
    {
        float len = MathF.Sqrt(plane.X * plane.X + plane.Y * plane.Y + plane.Z * plane.Z);
        if (len < 1e-8f) return plane;
        return plane / len;
    }
}
