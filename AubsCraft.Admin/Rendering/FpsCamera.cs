using System.Numerics;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// First-person camera with yaw/pitch mouse look and WASD movement.
/// Adapted directly from Lost Spawns Camera.cs.
/// </summary>
public sealed class FpsCamera
{
    public Vector3 Position { get; set; } = new(0f, 80f, 0f);
    public float Yaw { get; set; } = -90f;
    public float Pitch { get; set; } = -15f;
    public float MovementSpeed { get; set; } = 20f;
    public float MouseSensitivity { get; set; } = 0.15f;
    public float FovDegrees { get; set; } = 70f;
    public float NearPlane { get; set; } = 0.1f;
    public float FarPlane { get; set; } = 1000f;

    public Vector3 Front
    {
        get
        {
            float yawRad = MathF.PI / 180f * Yaw;
            float pitchRad = MathF.PI / 180f * Pitch;
            return Vector3.Normalize(new Vector3(
                MathF.Cos(yawRad) * MathF.Cos(pitchRad),
                MathF.Sin(pitchRad),
                MathF.Sin(yawRad) * MathF.Cos(pitchRad)));
        }
    }

    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Front, Vector3.UnitY));

    public void ProcessMouseMovement(float dx, float dy)
    {
        Yaw += dx * MouseSensitivity;
        Pitch -= dy * MouseSensitivity;
        Pitch = Math.Clamp(Pitch, -89f, 89f);
    }

    public void ProcessKeyboard(HashSet<string> keysDown, float deltaTime)
    {
        float velocity = MovementSpeed * deltaTime;
        var flatFront = Vector3.Normalize(new Vector3(Front.X, 0, Front.Z));
        var flatRight = Vector3.Normalize(Vector3.Cross(flatFront, Vector3.UnitY));

        if (keysDown.Contains("KeyW")) Position += flatFront * velocity;
        if (keysDown.Contains("KeyS")) Position -= flatFront * velocity;
        if (keysDown.Contains("KeyA")) Position -= flatRight * velocity;
        if (keysDown.Contains("KeyD")) Position += flatRight * velocity;
        if (keysDown.Contains("Space")) Position += Vector3.UnitY * velocity;
        if (keysDown.Contains("ShiftLeft")) Position -= Vector3.UnitY * velocity;
    }

    public Matrix4x4 GetViewMatrix()
        => Matrix4x4.CreateLookAt(Position, Position + Front, Vector3.UnitY);

    public Matrix4x4 GetProjectionMatrix(float aspectRatio)
    {
        float fovRad = FovDegrees * MathF.PI / 180f;
        return Matrix4x4.CreatePerspectiveFieldOfView(fovRad, aspectRatio, NearPlane, FarPlane);
    }

    public Matrix4x4 GetVpMatrix(float aspectRatio)
        => GetViewMatrix() * GetProjectionMatrix(aspectRatio);

    public void WriteMvp(float[] buffer, float aspectRatio)
    {
        var vp = GetVpMatrix(aspectRatio);
        buffer[0]  = vp.M11; buffer[1]  = vp.M12; buffer[2]  = vp.M13; buffer[3]  = vp.M14;
        buffer[4]  = vp.M21; buffer[5]  = vp.M22; buffer[6]  = vp.M23; buffer[7]  = vp.M24;
        buffer[8]  = vp.M31; buffer[9]  = vp.M32; buffer[10] = vp.M33; buffer[11] = vp.M34;
        buffer[12] = vp.M41; buffer[13] = vp.M42; buffer[14] = vp.M43; buffer[15] = vp.M44;
    }
}
