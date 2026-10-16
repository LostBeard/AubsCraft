using System.Numerics;
using AubsCraft.Live.Model.BlockEntities;
using AubsCraft.Live.Model.Entities;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// The blocks the game draws with a model of their own - chests, beds, signs, banners, shulker boxes: the server says where they are in each column (the <c>X-BlockEntities</c>
/// header), this keeps them per column and each frame turns the ones near the camera and in view into boxes (<see cref="BlockEntityModels"/>), which <see cref="EntityLayer"/> draws
/// together with the creatures.
/// </summary>
public sealed class BlockEntityLayer
{
    /// <summary>Most blocks posed in a frame (the nearest ones).</summary>
    public const int MaxPosed = 2500;
    /// <summary>Past this many blocks they are not drawn: a chest is a few pixels there.</summary>
    public const float Range = 80f;

    private readonly Dictionary<(int cx, int cz), BlockEntitySpot[]> _columns = new();
    private readonly List<(float distSq, int cx, int cz, BlockEntitySpot spot)> _near = new();

    /// <summary>All the blocks the layer knows, and how many were posed in the last frame.</summary>
    public int Known { get; private set; }
    public int Posed { get; private set; }

    /// <summary>A column's blocks (replacing what it had). None removes it.</summary>
    public void SetColumn(int cx, int cz, IReadOnlyList<BlockEntitySpot> spots)
    {
        if (_columns.Remove((cx, cz), out var old)) Known -= old.Length;
        if (spots.Count == 0) return;
        var copy = new BlockEntitySpot[spots.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = spots[i];
        _columns[(cx, cz)] = copy;
        Known += copy.Length;
    }

    public void RemoveColumn(int cx, int cz)
    {
        if (_columns.Remove((cx, cz), out var old)) Known -= old.Length;
    }

    /// <summary>Append the boxes of the blocks near the camera and in its view to <paramref name="output"/>.</summary>
    public void Collect(Vector3 camera, in FrustumCuller.Frustum frustum, List<PartDraw> output)
    {
        _near.Clear();
        float rangeSq = Range * Range;
        foreach (var ((cx, cz), spots) in _columns)
        {
            float dxc = cx * 16f + 8f - camera.X, dzc = cz * 16f + 8f - camera.Z;
            if (dxc * dxc + dzc * dzc > (Range + 12f) * (Range + 12f)) continue;
            foreach (var spot in spots)
            {
                float x = cx * 16 + spot.X, y = spot.Y, z = cz * 16 + spot.Z;
                float dx = x + 0.5f - camera.X, dy = y + 0.5f - camera.Y, dz = z + 0.5f - camera.Z;
                float d2 = dx * dx + dy * dy + dz * dz;
                if (d2 > rangeSq) continue;
                if (!FrustumCuller.IsBoxVisible(in frustum, new Vector3(x - 0.5f, y - 0.5f, z - 0.5f), new Vector3(x + 1.5f, y + 2.5f, z + 1.5f))) continue;
                _near.Add((d2, cx, cz, spot));
            }
        }
        if (_near.Count > MaxPosed) { _near.Sort((a, b) => a.distSq.CompareTo(b.distSq)); _near.RemoveRange(MaxPosed, _near.Count - MaxPosed); }
        foreach (var (_, cx, cz, spot) in _near)
            BlockEntityModels.Pose(spot, cx * 16 + spot.X, spot.Y, cz * 16 + spot.Z, output);
        Posed = _near.Count;
    }
}
