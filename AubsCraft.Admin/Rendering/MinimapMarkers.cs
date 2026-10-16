using System.Drawing;
using AubsCraft.Live.Model;
using AubsCraft.Live.Model.Entities;
using SpawnDev.GameUI.Elements;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// The dots of the minimap: where each creature of the live feed stands on the map the renderer draws. The map is a top-down view of <c>extent</c> blocks to each edge, turned so that
/// its up is where the camera faces (<c>VoxelWorldRenderer.RenderMinimap</c>); a creature's offset from the camera is turned by the same yaw and divided by the extent.
/// </summary>
public static class MinimapMarkers
{
    public static readonly Color PlayerColour = Color.FromArgb(255, 255, 255, 255);
    public static readonly Color HostileColour = Color.FromArgb(255, 230, 60, 50);
    public static readonly Color NeutralColour = Color.FromArgb(255, 235, 200, 60);
    public static readonly Color PassiveColour = Color.FromArgb(255, 80, 200, 90);
    public static readonly Color VillagerColour = Color.FromArgb(255, 90, 150, 245);

    /// <summary>Map units (right, down) of a world offset from the camera, for a camera facing <paramref name="yawDegrees"/> (0 = +X, 90 = +Z) and a map of <paramref name="extent"/> blocks to each edge.</summary>
    public static (float X, float Y) MapPosition(float dx, float dz, float yawDegrees, float extent)
    {
        float yaw = yawDegrees * MathF.PI / 180f, cos = MathF.Cos(yaw), sin = MathF.Sin(yaw);
        // The map's up is the heading (cos, sin); its right is the heading turned a quarter toward +Z (-sin, cos)
        float right = -dx * sin + dz * cos, up = dx * cos + dz * sin;
        return (right / extent, -up / extent);
    }

    public static Color ColourOf(string type, bool isPlayer) => isPlayer ? PlayerColour : MobKinds.Of(type) switch
    {
        MobKind.Hostile => HostileColour,
        MobKind.Neutral => NeutralColour,
        MobKind.Villager => VillagerColour,
        _ => PassiveColour,
    };

    /// <summary>The markers for <paramref name="creatures"/> around the camera: players big and pinned to the border when out of range, the rest small and left out of range; at most <paramref name="max"/>, nearest first.</summary>
    public static void Build(IEnumerable<LiveEntityView> creatures, float cameraX, float cameraZ, float yawDegrees, float extent, List<MinimapMarker> into, int max = 160)
    {
        into.Clear();
        var near = new List<(float d2, MinimapMarker marker)>();
        foreach (var v in creatures)
        {
            if ((v.Flags & LiveFlags.Invisible) != 0 || EntityModels.IsNotACreature(v.Type)) continue;
            float dx = v.X - cameraX, dz = v.Z - cameraZ;
            var (x, y) = MapPosition(dx, dz, yawDegrees, extent);
            if (!v.IsPlayer && MathF.Max(MathF.Abs(x), MathF.Abs(y)) > 1f) continue;
            near.Add((dx * dx + dz * dz, new MinimapMarker(x, y, ColourOf(v.Type, v.IsPlayer), v.IsPlayer ? 5f : 3f, PinToEdge: v.IsPlayer)));
        }
        if (near.Count > max) { near.Sort((a, b) => a.d2.CompareTo(b.d2)); near.RemoveRange(max, near.Count - max); }
        // Players last so they are drawn over the animals around them
        foreach (var (_, m) in near.OrderBy(n => n.marker.Size)) into.Add(m);
    }
}
