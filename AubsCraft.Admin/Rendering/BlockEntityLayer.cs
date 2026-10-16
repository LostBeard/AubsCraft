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
    private readonly Dictionary<(int cx, int cz), Dictionary<(int x, int y, int z), SignText>> _texts = new();
    private readonly Dictionary<(int cx, int cz), Dictionary<(int x, int y, int z), (byte Pattern, byte Color)[]>> _banners = new();

    /// <summary>The patterns of a column's banners (replacing what it had).</summary>
    public void SetBanners(int cx, int cz, IReadOnlyList<BannerLayers> banners)
    {
        _banners.Remove((cx, cz));
        if (banners.Count == 0) return;
        var map = new Dictionary<(int, int, int), (byte, byte)[]>(banners.Count);
        foreach (var b in banners) map[(b.X, b.Y, b.Z)] = b.Layers;
        _banners[(cx, cz)] = map;
    }

    /// <summary>Past this many blocks a sign's writing is not drawn (it could not be read).</summary>
    public const float TextRange = 28f;

    /// <summary>The pictures of signs' writing (made once the entity atlas is there); without it the signs are blank boards.</summary>
    public SignTextAtlas? Texts { get; set; }
    /// <summary>Signs with writing in the columns the layer knows.</summary>
    public int KnownTexts { get; private set; }
    /// <summary>Signs whose writing was drawn in the last frame.</summary>
    public int TextsDrawn { get; private set; }

    /// <summary>What a column's signs say (replacing what it had).</summary>
    public void SetSignTexts(int cx, int cz, IReadOnlyList<SignText> texts)
    {
        if (_texts.Remove((cx, cz), out var old)) KnownTexts -= old.Count;
        if (texts.Count == 0) return;
        var map = new Dictionary<(int, int, int), SignText>(texts.Count);
        foreach (var t in texts) map[(t.X, t.Y, t.Z)] = t;
        _texts[(cx, cz)] = map;
        KnownTexts += map.Count;
    }

    /// <summary>All the blocks the layer knows, and how many were posed in the last frame.</summary>
    public int Known { get; private set; }
    public int Posed { get; private set; }

    /// <summary>
    /// The way the bed under a sleeper faces (the yaw the game numbers: south 0, west 90, north 180, east 270), found among the beds within a block and a half of (x, y, z); null when there is none
    /// near (the bed is not loaded, or the player sleeps on something that is not a bed block entity).
    /// </summary>
    public float? BedFacingNear(float x, float y, float z)
    {
        int cx0 = (int)MathF.Floor((x - 1.5f) / 16f), cx1 = (int)MathF.Floor((x + 1.5f) / 16f), cz0 = (int)MathF.Floor((z - 1.5f) / 16f), cz1 = (int)MathF.Floor((z + 1.5f) / 16f);
        float best = float.MaxValue;
        float? yaw = null;
        for (int cx = cx0; cx <= cx1; cx++)
            for (int cz = cz0; cz <= cz1; cz++)
            {
                if (!_columns.TryGetValue((cx, cz), out var spots)) continue;
                foreach (var spot in spots)
                {
                    if (spot.Kind != BlockEntityKind.Bed) continue;
                    float dx = cx * 16 + spot.X + 0.5f - x, dy = spot.Y + 0.6f - y, dz = cz * 16 + spot.Z + 0.5f - z;
                    float d2 = dx * dx + dy * dy + dz * dz;
                    if (Math.Abs(dx) > 1.5f || Math.Abs(dz) > 1.5f || Math.Abs(dy) > 1.5f || d2 >= best) continue;
                    best = d2;
                    yaw = spot.Orient * 22.5f;
                }
            }
        return yaw;
    }

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
        if (_texts.Remove((cx, cz), out var texts)) KnownTexts -= texts.Count;
        _banners.Remove((cx, cz));
    }

    /// <summary>Append the boxes of the blocks near the camera and in its view to <paramref name="output"/>.</summary>
    public void Collect(Vector3 camera, in FrustumCuller.Frustum frustum, List<PartDraw> output)
    {
        _near.Clear();
        Texts?.BeginFrame();
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
        int written = 0;
        float textSq = TextRange * TextRange;
        foreach (var (d2, cx, cz, spot) in _near)
        {
            BlockEntityModels.Pose(spot, cx * 16 + spot.X, spot.Y, cz * 16 + spot.Z, output);
            // The patterns on a banner
            if (spot.Kind == BlockEntityKind.Banner && _banners.TryGetValue((cx, cz), out var banners) && banners.TryGetValue((spot.X, spot.Y, spot.Z), out var layers))
                BlockEntityModels.PoseBannerLayers(spot, cx * 16 + spot.X, spot.Y, cz * 16 + spot.Z, layers, output);
            // The writing on a sign near enough to read
            if (Texts != null && d2 <= textSq && spot.Kind is BlockEntityKind.Sign or BlockEntityKind.HangingSign
                && _texts.TryGetValue((cx, cz), out var column) && column.TryGetValue((spot.X, spot.Y, spot.Z), out var sign))
            {
                bool hanging = spot.Kind == BlockEntityKind.HangingSign;
                if (Texts.Ensure(sign, hanging))
                {
                    BlockEntityModels.PoseSignText(spot, cx * 16 + spot.X, spot.Y, cz * 16 + spot.Z, "text:" + SignTextAtlas.KeyOf(sign, hanging), output);
                    written++;
                }
            }
        }
        Posed = _near.Count;
        TextsDrawn = written;
    }
}
