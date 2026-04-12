using System.Numerics;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// Orbit camera for map-style viewing. Orbits around a target point.
/// Supports rotation (left drag), zoom (scroll), and pan (right drag).
/// Based on Lost Spawns Camera.cs math.
/// </summary>
public sealed class OrbitCamera
{
    public Vector3 Target { get; set; } = new(0f, 64f, 0f);
    public float Distance { get; set; } = 100f;
    public float Azimuth { get; set; } = 45f;    // horizontal angle in degrees
    public float Elevation { get; set; } = 35f;   // vertical angle in degrees (0=horizon, 90=top-down)
    public float MinDistance { get; set; } = 10f;
    public float MaxDistance { get; set; } = 500f;
    public float MinElevation { get; set; } = 5f;
    public float MaxElevation { get; set; } = 89f;
    public float RotationSensitivity { get; set; } = 0.3f;
    public float PanSensitivity { get; set; } = 0.5f;
    public float ZoomSensitivity { get; set; } = 10f;
    public float FovDegrees { get; set; } = 60f;
    public float NearPlane { get; set; } = 0.5f;
    public float FarPlane { get; set; } = 1000f;

    /// <summary>
    /// Camera position computed from orbit parameters.
    /// </summary>
    public Vector3 Position
    {
        get
        {
            float azRad = MathF.PI / 180f * Azimuth;
            float elRad = MathF.PI / 180f * Elevation;
            return Target + new Vector3(
                Distance * MathF.Cos(elRad) * MathF.Cos(azRad),
                Distance * MathF.Sin(elRad),
                Distance * MathF.Cos(elRad) * MathF.Sin(azRad));
        }
    }

    /// <summary>
    /// Process mouse drag for rotation (left button).
    /// </summary>
    public void Rotate(float dx, float dy)
    {
        Azimuth += dx * RotationSensitivity;
        Elevation = Math.Clamp(Elevation + dy * RotationSensitivity, MinElevation, MaxElevation);
    }

    /// <summary>
    /// Process mouse drag for panning (right button or middle button).
    /// Moves the target in the camera's local XZ plane.
    /// </summary>
    public void Pan(float dx, float dy)
    {
        float azRad = MathF.PI / 180f * Azimuth;
        var right = new Vector3(-MathF.Sin(azRad), 0f, MathF.Cos(azRad));
        var forward = new Vector3(-MathF.Cos(azRad), 0f, -MathF.Sin(azRad));
        float scale = PanSensitivity * (Distance / 100f);
        Target += right * (-dx * scale) + forward * (dy * scale);
    }

    /// <summary>
    /// Process scroll wheel for zoom.
    /// </summary>
    public void Zoom(float delta)
    {
        Distance = Math.Clamp(Distance - delta * ZoomSensitivity, MinDistance, MaxDistance);
    }

    public Matrix4x4 GetViewMatrix()
    {
        return Matrix4x4.CreateLookAt(Position, Target, Vector3.UnitY);
    }

    public Matrix4x4 GetProjectionMatrix(float aspectRatio)
    {
        float fovRad = FovDegrees * MathF.PI / 180f;
        return Matrix4x4.CreatePerspectiveFieldOfView(fovRad, aspectRatio, NearPlane, FarPlane);
    }

    public Matrix4x4 GetVpMatrix(float aspectRatio)
    {
        return GetViewMatrix() * GetProjectionMatrix(aspectRatio);
    }

    /// <summary>Returns the MVP matrix as a float[16] array for GPU uniform upload.</summary>
    public float[] GetMvpArray(float aspectRatio)
    {
        var vp = GetVpMatrix(aspectRatio);
        // Row-major to column-major: raw upload = natural transpose for WGSL
        return
        [
            vp.M11, vp.M12, vp.M13, vp.M14,
            vp.M21, vp.M22, vp.M23, vp.M24,
            vp.M31, vp.M32, vp.M33, vp.M34,
            vp.M41, vp.M42, vp.M43, vp.M44,
        ];
    }

    /// <summary>Zero-allocation MVP write into existing buffer.</summary>
    public void WriteMvp(float[] buffer, float aspectRatio)
    {
        var vp = GetVpMatrix(aspectRatio);
        buffer[0]  = vp.M11; buffer[1]  = vp.M12; buffer[2]  = vp.M13; buffer[3]  = vp.M14;
        buffer[4]  = vp.M21; buffer[5]  = vp.M22; buffer[6]  = vp.M23; buffer[7]  = vp.M24;
        buffer[8]  = vp.M31; buffer[9]  = vp.M32; buffer[10] = vp.M33; buffer[11] = vp.M34;
        buffer[12] = vp.M41; buffer[13] = vp.M42; buffer[14] = vp.M43; buffer[15] = vp.M44;
    }
}
