using System.Numerics;

namespace AubsCraft.Admin.Xr;

/// <summary>
/// Where the HUD floats in a headset: a flat panel in front of the person, in the ROOM's space (metres), so it stays where a menu in front of the body would be however the world flies by.
/// It keeps a fixed distance from the head and a little below the eyes, always facing the head, and turns only when the head has turned past a dead zone - a glance leaves it be, looking
/// away drags it round (lazy follow). Pure geometry: no GPU in here.
/// </summary>
public sealed class XrPanel
{
    /// <summary>The panel's size in the room, metres.</summary>
    public const float Width = 1.5f, Height = 0.9375f;
    /// <summary>The HUD's own size in pixels (the panel's texture): the same shape as the panel.</summary>
    public const int PixelWidth = 800, PixelHeight = 500;
    /// <summary>How far in front of the head the panel's middle is (horizontally), metres.</summary>
    public const float Distance = 1.3f;
    /// <summary>How far below the eyes the panel's middle is.</summary>
    public const float DropBelowEyes = 0.12f;
    /// <summary>How far the head may turn from the panel (degrees) before the panel follows.</summary>
    public const float DeadZoneDegrees = 28f;
    /// <summary>How quickly the panel catches up, per second (an ease: 4 closes most of the gap in half a second).</summary>
    public const float FollowRate = 4f;

    private bool _placed;

    /// <summary>The panel is shown.</summary>
    public bool Visible { get; private set; } = true;

    /// <summary>The way the panel faces away from, as a heading (radians, 0 = looking down -Z, turning left positive): the way the head was last looking, eased.</summary>
    public float Heading { get; private set; }

    /// <summary>The panel's middle, in the room.</summary>
    public Vector3 Center { get; private set; }

    /// <summary>The panel's own right (its pixels' x).</summary>
    public Vector3 Right => new(MathF.Cos(Heading), 0f, -MathF.Sin(Heading));

    /// <summary>From the head toward the panel (level).</summary>
    public Vector3 Forward => new(-MathF.Sin(Heading), 0f, -MathF.Cos(Heading));

    /// <summary>Show or hide it. Shown again, it appears where the head is looking.</summary>
    public void SetVisible(bool visible)
    {
        if (visible && !Visible) _placed = false;
        Visible = visible;
    }

    /// <summary>The heading of a direction (what <see cref="Heading"/> measures), or null when it points (nearly) straight up or down and has no heading.</summary>
    public static float? HeadingOf(Vector3 direction)
    {
        if (direction.X * direction.X + direction.Z * direction.Z < 0.09f * direction.LengthSquared()) return null;
        return MathF.Atan2(-direction.X, -direction.Z);
    }

    /// <summary>Follow the head for <paramref name="dt"/> seconds.</summary>
    public void Update(float dt, Vector3 headPosition, Quaternion headOrientation)
    {
        if (HeadingOf(Vector3.Transform(-Vector3.UnitZ, headOrientation)) is { } look)
        {
            if (!_placed) { Heading = look; _placed = true; }
            else
            {
                float diff = Wrap(look - Heading);
                float dead = DeadZoneDegrees * MathF.PI / 180f;
                if (MathF.Abs(diff) > dead)
                {
                    float excess = diff - MathF.Sign(diff) * dead;
                    Heading = Wrap(Heading + excess * (1f - MathF.Exp(-FollowRate * dt)));
                }
            }
        }
        Center = headPosition + Forward * Distance + new Vector3(0f, -DropBelowEyes, 0f);
    }

    /// <summary>The panel's four corners in the room: top left, top right, bottom left, bottom right (as the HUD's pixels run).</summary>
    public (Vector3 TopLeft, Vector3 TopRight, Vector3 BottomLeft, Vector3 BottomRight) Corners()
    {
        var right = Right * (Width / 2f);
        var up = Vector3.UnitY * (Height / 2f);
        return (Center - right + up, Center + right + up, Center - right - up, Center + right - up);
    }

    /// <summary>
    /// Where a ray from a controller meets the panel, as a pixel of the HUD (<paramref name="x"/> across, <paramref name="y"/> down) and how far along the ray; false when it misses or
    /// points away from it.
    /// </summary>
    public bool Hit(Vector3 origin, Vector3 direction, out float x, out float y, out float distance)
    {
        x = y = distance = 0f;
        if (!Visible) return false;
        var normal = -Forward;   // facing the head
        float denominator = Vector3.Dot(direction, normal);
        if (MathF.Abs(denominator) < 1e-5f) return false;
        distance = Vector3.Dot(Center - origin, normal) / denominator;
        if (distance <= 0f) return false;
        var local = origin + direction * distance - Center;
        float u = Vector3.Dot(local, Right) / Width + 0.5f;
        float v = 0.5f - local.Y / Height;
        x = u * PixelWidth;
        y = v * PixelHeight;
        return u >= 0f && u <= 1f && v >= 0f && v <= 1f;
    }

    private static float Wrap(float radians)
    {
        while (radians > MathF.PI) radians -= 2f * MathF.PI;
        while (radians < -MathF.PI) radians += 2f * MathF.PI;
        return radians;
    }
}
