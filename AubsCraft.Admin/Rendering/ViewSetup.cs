using System.Numerics;
using SpawnDev.VoxelEngine.Rendering;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// What the world is drawn from: where the eye is and the matrices that take the world to the screen - the reversed-Z one the pipeline draws with, and the standard-Z one the
/// frustum is cut from. The fly camera makes one (<see cref="Of"/>); so does each eye of a headset (<see cref="FromXr"/>).
/// </summary>
public readonly record struct ViewSetup(Vector3 Position, Matrix4x4 ViewProjectionReversed, Matrix4x4 ViewProjectionStandard, float ClearAlpha = 1f)
{
    /// <summary>The fly camera.</summary>
    public static ViewSetup Of(FpsCamera camera, float aspect)
    {
        float fovRad = camera.FovDegrees * MathF.PI / 180f;
        return new ViewSetup(camera.Position,
            camera.GetViewMatrix() * ReversedZHelper.CreateInfinitePerspective(fovRad, aspect, camera.NearPlane),
            camera.GetVpMatrix(aspect));
    }

    /// <summary>
    /// Standard (D3D, z 0..1) from an OpenGL clip space (z -1..1): z' = (z + w) / 2. Row-vector form: the matrix that follows a projection of WebXR.
    /// </summary>
    public static readonly Matrix4x4 GlToStandardZ = new(
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 0.5f, 0,
        0, 0, 0.5f, 1);

    /// <summary>
    /// One eye. <paramref name="worldToEye"/> takes world points to the eye's space (row-vector, like everything here); <paramref name="projectionGl"/> is the eye's WebXR projection as a row-vector
    /// matrix (<see cref="VrMatrixHelpers.GlColumnMajorToDotNetMatrix"/> of XRView.projectionMatrix); <paramref name="eyeInWorld"/> is where the eye is.
    /// </summary>
    public static ViewSetup FromXr(Vector3 eyeInWorld, Matrix4x4 worldToEye, Matrix4x4 projectionGl, float clearAlpha = 1f)
    {
        var vp = worldToEye * projectionGl;
        return new ViewSetup(eyeInWorld, vp * VrMatrixHelpers.ZFlipStandardToReversedZ, vp * GlToStandardZ, clearAlpha);
    }
}
