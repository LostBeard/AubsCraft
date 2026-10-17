using System.Numerics;
using SpawnDev.VoxelEngine.Rendering;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// What the world is drawn from: where the eye is and the matrices that take the world to the screen - the reversed-Z one the pipeline draws with, and the standard-Z one the
/// frustum is cut from. The fly camera makes one (<see cref="Of"/>); so does each eye of a headset (<see cref="FromXr"/>).
/// </summary>
public readonly record struct ViewSetup(Vector3 Position, Matrix4x4 ViewProjectionReversed, Matrix4x4 ViewProjectionStandard, float ClearAlpha = 1f, Vector3? CullCenter = null, int DrawChunks = 0, Matrix4x4? OtherEyeStandard = null, bool UseBundles = false, Vector3? HeadCenter = null)
{
    /// <summary>Where the world is streamed and drawn round: the head, for both eyes of a headset (the eyes are a few centimetres apart: at a chunk's edge each would pick another chunk and the set drawn would change from one eye to the next).</summary>
    public Vector3? ViewCenter => HeadCenter;

    /// <summary>
    /// Draw from a render bundle shared by the two eyes of a headset: <see cref="OtherEyeStandard"/> is the other eye's standard-Z view-projection, and what is drawn is what either eye sees (with a little margin, so
    /// the set holds still while the head moves a little) - recorded once and run in both eyes, and again next frame while the set does not change.
    /// </summary>
    public bool Shared => UseBundles && OtherEyeStandard != null;

    /// <summary>One eye's standard-Z view-projection (the other eye's, for <see cref="OtherEyeStandard"/>).</summary>
    public static Matrix4x4 XrStandard(Matrix4x4 worldToEye, Matrix4x4 projectionGl) => worldToEye * projectionGl * GlToStandardZ;

    /// <summary>The world is seen as a MODEL (on a table): what is drawn is a few chunks round <see cref="CullCenter"/>, not round the eye, with no fog and no water or sky of the eye's.</summary>
    public bool IsModel => CullCenter != null;

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
    public static ViewSetup FromXr(Vector3 eyeInWorld, Matrix4x4 worldToEye, Matrix4x4 projectionGl, float clearAlpha = 1f, Vector3? cullCenter = null, int drawChunks = 0, Matrix4x4? otherEyeStandard = null, bool bundles = false, Vector3? headCenter = null)
    {
        var vp = worldToEye * projectionGl;
        return new ViewSetup(eyeInWorld, vp * VrMatrixHelpers.ZFlipStandardToReversedZ, vp * GlToStandardZ, clearAlpha, cullCenter, drawChunks, otherEyeStandard, bundles, headCenter);
    }
}
