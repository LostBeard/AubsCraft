using System.Numerics;

namespace AubsCraft.Admin.Xr;

/// <summary>
/// The world as a model on a table: a place of the world (<see cref="Focus"/>) shown at a small scale (<see cref="Scale"/>: metres of the room per block) standing at a point of the room
/// (<see cref="Anchor"/>), turned by <see cref="Yaw"/>. The same room-to-world transform every headset frame needs (<see cref="WorldFromRoom"/>), so everything that works at life size - drawing the eyes,
/// the controllers' rays, picking, the HUD panel in the room - works on the table too: the world is just seen from a long way off. Row-vector convention like <see cref="XrMath"/>.
/// </summary>
public sealed class XrTabletop
{
    /// <summary>The smallest and biggest model: a block is 4 mm (a whole region on a table) to 25 cm (a house you can walk round).</summary>
    public const float MinScale = 1f / 256f, MaxScale = 0.25f;

    /// <summary>How far from its middle the model reaches on the table, in metres of the room: what is farther is not drawn.</summary>
    public const float TableRadius = 0.8f;

    /// <summary>A block's size in the room when a session starts: a block is 8 mm, so a model of the world as wide as a table shows about 14 chunks each way.</summary>
    public const float StartScale = 1f / 128f;

    /// <summary>The place of the world at the middle of the model.</summary>
    public Vector3 Focus { get; set; }

    /// <summary>Metres of the room a block of the world takes.</summary>
    public float Scale { get; private set; } = StartScale;

    /// <summary>Where the middle of the model stands in the room.</summary>
    public Vector3 Anchor { get; set; } = new(0f, 0.8f, -0.7f);

    /// <summary>The model's turn about the vertical, in radians (positive turns the world left, as a turn of the room does).</summary>
    public float Yaw { get; set; }

    /// <summary>The room's point for a point of the world.</summary>
    public Vector3 RoomPoint(Vector3 world) => Vector3.Transform(world - Focus, Matrix4x4.CreateRotationY(Yaw) * Matrix4x4.CreateScale(Scale)) + Anchor;

    /// <summary>The world's point for a point of the room.</summary>
    public Vector3 WorldPoint(Vector3 room) => Vector3.Transform(room - Anchor, Matrix4x4.CreateScale(1f / Scale) * Matrix4x4.CreateRotationY(-Yaw)) + Focus;

    /// <summary>Room to world: what <see cref="XrMath.EyeView"/> and the rest take.</summary>
    public Matrix4x4 WorldFromRoom
    {
        get
        {
            // world -> room is: move the focus to the origin, turn, shrink, stand at the anchor. The room to world is that undone.
            var roomFromWorld = Matrix4x4.CreateTranslation(-Focus) * Matrix4x4.CreateRotationY(Yaw) * Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateTranslation(Anchor);
            Matrix4x4.Invert(roomFromWorld, out var worldFromRoom);
            return worldFromRoom;
        }
    }

    /// <summary>Make the model bigger (factor over 1) or smaller, about its middle; kept between <see cref="MinScale"/> and <see cref="MaxScale"/>.</summary>
    public void ZoomBy(float factor) => Scale = Math.Clamp(Scale * factor, MinScale, MaxScale);

    public void SetScale(float scale) => Scale = Math.Clamp(scale, MinScale, MaxScale);

    /// <summary>
    /// Slide the world under the model: <paramref name="roomX"/> and <paramref name="roomZ"/> metres along the table's own right and away directions (as the model is turned), so pushing a stick
    /// forward moves the model's view of the world away from the viewer, whichever way the model is turned.
    /// </summary>
    public void Pan(float roomX, float roomZ)
    {
        // The room's step as a step of the world: undo the model's turn and scale (the table's directions are the room's, the world's are turned and larger)
        var step = Vector3.Transform(new Vector3(roomX, 0f, roomZ), Matrix4x4.CreateScale(1f / Scale) * Matrix4x4.CreateRotationY(-Yaw));
        Focus += step;
    }

    /// <summary>Stand the model on a point of the room in front of the head: <paramref name="distance"/> ahead (level) and <paramref name="below"/> below the eyes.</summary>
    public void PlaceBeforeHead(Vector3 headPosition, Quaternion headOrientation, float distance = 0.7f, float below = 0.75f)
    {
        var look = Vector3.Transform(-Vector3.UnitZ, headOrientation);
        look.Y = 0f;
        look = look.LengthSquared() < 1e-6f ? -Vector3.UnitZ : Vector3.Normalize(look);
        Anchor = headPosition + look * distance - new Vector3(0f, below, 0f);
    }

    /// <summary>How many chunks (of 16 blocks) from the focus the model needs drawn to fill the table, at least 1.</summary>
    public int ChunkRadius => Math.Max(1, (int)MathF.Ceiling(TableRadius / Scale / 16f));
}
