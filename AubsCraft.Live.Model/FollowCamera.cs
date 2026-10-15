namespace AubsCraft.Live.Model;

/// <summary>How the viewer's camera tracks a player it follows.</summary>
public enum FollowMode
{
    /// <summary>Behind and above the player, looking at them; the viewer's mouse look orbits around them.</summary>
    Chase,
    /// <summary>At the player's eyes, looking where they look: the game as they see it.</summary>
    Eyes,
}

/// <summary>Where the camera goes to follow a player (pure maths, so it can be tested without a browser).</summary>
public static class FollowCamera
{
    public const float StandingEyeHeight = 1.62f, SneakingEyeHeight = 1.27f;
    public const float DefaultDistance = 5f, MinDistance = 1.5f, MaxDistance = 24f;
    public const float DefaultPitch = -15f;

    /// <summary>The viewer camera's yaw (degrees; 0 = +X, 90 = +Z, -90 = -Z which is north) for a Minecraft yaw (0 = south +Z, 90 = west, 180 = north).</summary>
    public static float CameraYawOf(float minecraftYaw) => minecraftYaw + 90f;

    /// <summary>The viewer camera's pitch (up positive) for a Minecraft pitch (down positive).</summary>
    public static float CameraPitchOf(float minecraftPitch) => -minecraftPitch;

    public static (float X, float Y, float Z) Eye(in LiveEntityView v) => (v.X, v.Y + (v.Sneaking ? SneakingEyeHeight : StandingEyeHeight), v.Z);

    /// <summary>
    /// The camera for <paramref name="player"/>. <paramref name="yaw"/> and <paramref name="pitch"/> are the viewer camera's own (the mouse look);
    /// <see cref="FollowMode.Eyes"/> ignores them and returns the player's. The returned angles are what the camera should have.
    /// </summary>
    public static (float X, float Y, float Z, float Yaw, float Pitch) Place(in LiveEntityView player, FollowMode mode, float yaw, float pitch, float distance)
    {
        var (ex, ey, ez) = Eye(player);
        if (mode == FollowMode.Eyes)
            return (ex, ey, ez, CameraYawOf(player.Yaw), Math.Clamp(CameraPitchOf(player.Pitch), -89f, 89f));

        pitch = Math.Clamp(pitch, -89f, 89f);
        distance = Math.Clamp(distance, MinDistance, MaxDistance);
        float yawRad = yaw * MathF.PI / 180f, pitchRad = pitch * MathF.PI / 180f;
        // The camera's Front, as FpsCamera defines it; the camera stands "distance" behind the eye along it
        float fx = MathF.Cos(yawRad) * MathF.Cos(pitchRad), fy = MathF.Sin(pitchRad), fz = MathF.Sin(yawRad) * MathF.Cos(pitchRad);
        return (ex - fx * distance, ey - fy * distance, ez - fz * distance, yaw, pitch);
    }

    /// <summary>A place to stand to look at a player from behind and above (for a teleport): the chase camera at the player's own heading.</summary>
    public static (float X, float Y, float Z, float Yaw, float Pitch) Behind(in LiveEntityView player)
        => Place(player, FollowMode.Chase, CameraYawOf(player.Yaw), DefaultPitch, DefaultDistance);
}
