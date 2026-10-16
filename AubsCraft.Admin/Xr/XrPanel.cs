using System.Numerics;

namespace AubsCraft.Admin.Xr;

/// <summary>Where one surface of the headset's UI floats and which part of the HUD atlas it shows. Pure data.</summary>
/// <param name="Name">What it is called (the log and the tests).</param>
/// <param name="WidthMeters">Its size in the room.</param>
/// <param name="HeightMeters">Its size in the room.</param>
/// <param name="Distance">How far in front of the head its middle is (horizontally), metres.</param>
/// <param name="DropBelowEyes">How far below the eyes its middle is (negative: above).</param>
/// <param name="Lateral">How far to the right of where the head looks (negative: the left), metres.</param>
/// <param name="Follow">True: it keeps its distance from the head and turns lazily after it (a bar, a feed). False: it is put where the head looked when it appeared and stays there in the room (a menu, a window).</param>
/// <param name="RegionX">The part of the HUD atlas it shows, in the HUD's layout pixels.</param>
/// <param name="RegionY">The part of the HUD atlas it shows, in the HUD's layout pixels.</param>
/// <param name="RegionWidth">The part of the HUD atlas it shows, in the HUD's layout pixels.</param>
/// <param name="RegionHeight">The part of the HUD atlas it shows, in the HUD's layout pixels.</param>
public sealed record XrPanelSpec(string Name, float WidthMeters, float HeightMeters, float Distance, float DropBelowEyes, float Lateral, bool Follow,
    int RegionX, int RegionY, int RegionWidth, int RegionHeight)
{
    /// <summary>How far the head may turn from a following surface (degrees) before it follows.</summary>
    public float DeadZoneDegrees { get; init; } = 28f;
    /// <summary>How quickly a following surface catches up, per second (an ease: 4 closes most of the gap in half a second).</summary>
    public float FollowRate { get; init; } = 4f;
    /// <summary>True: the surface rides over the left hand (an armband of the blocks in hand), put by <see cref="XrPanel.PlaceOverHand"/> each frame; it is not shown while that hand is not tracked.</summary>
    public bool OverLeftHand { get; init; }
}

/// <summary>
/// Where one surface of the HUD floats in a headset: a flat rectangle in the ROOM's space (metres) that faces the head, either kept at a distance from it (a lazy follow: a glance leaves it be,
/// looking away drags it round) or left where it appeared. Pure geometry: no GPU in here.
/// </summary>
public sealed class XrPanel
{
    public XrPanel(XrPanelSpec spec) => Spec = spec;

    public XrPanelSpec Spec { get; }

    /// <summary>The size in the room, metres.</summary>
    public float Width => Spec.WidthMeters;
    public float Height => Spec.HeightMeters;

    /// <summary>The part of the HUD atlas it shows, in layout pixels.</summary>
    public int PixelWidth => Spec.RegionWidth;
    public int PixelHeight => Spec.RegionHeight;

    private bool _placed;

    /// <summary>The surface is shown.</summary>
    public bool Visible { get; private set; }

    /// <summary>The way the surface faces away from, as a heading (radians, 0 = looking down -Z, turning left positive): the way the head was last looking, eased.</summary>
    public float Heading { get; private set; }

    /// <summary>Its middle, in the room.</summary>
    public Vector3 Center { get; private set; }

    private Vector3? _right, _up;

    /// <summary>Its own right (its pixels' x).</summary>
    public Vector3 Right => _right ?? new(MathF.Cos(Heading), 0f, -MathF.Sin(Heading));

    /// <summary>Its own up (its pixels' -y).</summary>
    public Vector3 Up => _up ?? Vector3.UnitY;

    /// <summary>From the head toward the surface: the way it faces away from the viewer.</summary>
    public Vector3 Forward => _right is { } r ? -Vector3.Normalize(Vector3.Cross(r, Up)) : new(-MathF.Sin(Heading), 0f, -MathF.Cos(Heading));

    /// <summary>The surface rides over a hand: where, from where the hand is, the way it points and where the head is. Pure.</summary>
    public static (Vector3 Center, Vector3 Right, Vector3 Up) OverHand(Vector3 hand, Vector3 handDirection, Vector3 head)
    {
        // A hand of palm-down or pointing: the strip floats a hand's breadth above it and a little toward the viewer, turned to face the viewer with its text level
        var center = hand + Vector3.UnitY * 0.11f;
        var toHead = Vector3.Normalize(head - center);
        var right = Vector3.Cross(Vector3.UnitY, toHead);
        right = right.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(right);
        var up = Vector3.Normalize(Vector3.Cross(toHead, right));
        return (center + toHead * 0.04f, right, up);
    }

    /// <summary>Put the surface over a hand (see <see cref="OverHand"/>); the surface is then facing the viewer wherever the hand is.</summary>
    public void PlaceOverHand(Vector3 hand, Vector3 handDirection, Vector3 head)
    {
        var (center, right, up) = OverHand(hand, handDirection, head);
        Center = center; _right = right; _up = up;
        _placed = true;
    }

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

    /// <summary>Follow (or be placed by) the head for <paramref name="dt"/> seconds.</summary>
    public void Update(float dt, Vector3 headPosition, Quaternion headOrientation)
    {
        _right = null; _up = null;   // a heading puts it level again
        if (HeadingOf(Vector3.Transform(-Vector3.UnitZ, headOrientation)) is { } look)
        {
            if (!_placed)
            {
                Heading = look;
                _placed = true;
                Center = At(headPosition);
            }
            else if (Spec.Follow)
            {
                float diff = Wrap(look - Heading);
                float dead = Spec.DeadZoneDegrees * MathF.PI / 180f;
                if (MathF.Abs(diff) > dead)
                {
                    float excess = diff - MathF.Sign(diff) * dead;
                    Heading = Wrap(Heading + excess * (1f - MathF.Exp(-Spec.FollowRate * dt)));
                }
            }
        }
        if (Spec.Follow) Center = At(headPosition);
    }

    private Vector3 At(Vector3 head) => head + Forward * Spec.Distance + Right * Spec.Lateral + new Vector3(0f, -Spec.DropBelowEyes, 0f);

    /// <summary>The four corners in the room: top left, top right, bottom left, bottom right (as the HUD's pixels run).</summary>
    public (Vector3 TopLeft, Vector3 TopRight, Vector3 BottomLeft, Vector3 BottomRight) Corners()
    {
        var right = Right * (Width / 2f);
        var up = Up * (Height / 2f);
        return (Center - right + up, Center + right + up, Center - right - up, Center + right - up);
    }

    /// <summary>
    /// Where a ray from a controller meets the surface, as a pixel of its region (<paramref name="x"/> across, <paramref name="y"/> down) and how far along the ray; false when it misses or
    /// points away from it.
    /// </summary>
    public bool Hit(Vector3 origin, Vector3 direction, out float x, out float y, out float distance)
    {
        x = y = distance = 0f;
        if (!Visible) return false;
        var normal = -Forward;   // facing the viewer
        float denominator = Vector3.Dot(direction, normal);
        if (MathF.Abs(denominator) < 1e-5f) return false;
        distance = Vector3.Dot(Center - origin, normal) / denominator;
        if (distance <= 0f) return false;
        var local = origin + direction * distance - Center;
        float u = Vector3.Dot(local, Right) / Width + 0.5f;
        float v = 0.5f - Vector3.Dot(local, Up) / Height;
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

/// <summary>The surfaces of the headset's UI and the HUD atlas they show (the layout the ViewerHud builds its XR screen to).</summary>
public static class XrSurfaces
{
    /// <summary>The HUD's layout size in pixels: every surface is a rectangle of it.</summary>
    public const int AtlasWidth = 1100, AtlasHeight = 830;
    /// <summary>The atlas texture has this many pixels to each of the layout's (text is sharp up close; mips keep it steady far away).</summary>
    public const float TextureScale = 1.5f;

    /// <summary>The menu: tiles for what can be done now. Put where the head looks when it opens, a little to the left, and left there.</summary>
    public static XrPanelSpec Menu => new("menu", 0.34f, 0.577f, 0.75f, 0.04f, -0.34f, false, 0, 0, 330, 560);
    /// <summary>The window: one page of a bigger thing (blocks, blueprints, players, what to say, the map, the world), in front.</summary>
    public static XrPanelSpec Window => new("window", 0.84f, 0.597f, 0.95f, 0.08f, 0.22f, false, 340, 0, 760, 540);
    /// <summary>The bar: the blocks in hand and what is being done, an armband over the left hand (only while building); the grips switch the block.</summary>
    public static XrPanelSpec Bar => new("bar", 0.38f, 0.048f, 0.95f, 0.46f, 0f, true, 0, 570, 760, 96) { OverLeftHand = true };
    /// <summary>The feed: chat and notices, small, low and to the left.</summary>
    public static XrPanelSpec Feed => new("feed", 0.40f, 0.29f, 1.05f, 0.55f, -0.72f, true, 770, 570, 330, 240);

    public static XrPanelSpec[] All => [Window, Menu, Bar, Feed];
}
