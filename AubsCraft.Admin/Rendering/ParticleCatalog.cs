using System.Numerics;
using AubsCraft.Assets.Model;
using AubsCraft.Live.Model.Particles;
using SpawnDev.VoxelEngine.Rendering;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// What each kind of emitter shows: smoke rising off a campfire, a flame and a wisp over a torch, embers. Each entry is one <see cref="ParticleKind"/> (how many, how long, how it moves,
/// how it grows and fades) with the name of its sprite in the particle atlas; <see cref="Build"/> turns the names into places in the atlas. Lengths are blocks, times seconds. A sprite
/// the atlas does not have drops its entry (and is reported): the rest still shows.
/// </summary>
public sealed class ParticleCatalog
{
    private sealed record Def(
        string Name, EmitterKind[] Owners, string Sprite, int Count, float Life, Vector3 Velocity, float Gravity,
        float Spread, float Drift, float Wobble, float WobbleSpeed, float Size0, float Size1, float Alpha0, float Alpha1,
        Vector3 Origin, float OriginY = 0f, bool Glows = false, Vector3? Tint = null);

    private static EmitterKind[] Owners(params EmitterKind[] k) => k;
    private static readonly Vector3 White = Vector3.One;

    private static readonly Def[] Defs =
    {
        // Smoke is the game's big_smoke (twelve frames, a puff that thins as it ages)
        new("campfire smoke", Owners(EmitterKind.Campfire), "big_smoke", 10, 9f, new(0, 0.9f, 0), 0, 0.12f, 0.06f, 0.5f, 1.1f, 0.7f, 2.4f, 0.9f, 0f, new(0.5f, 0.75f, 0.5f), 0.03f),
        new("soul campfire smoke", Owners(EmitterKind.SoulCampfire), "big_smoke", 10, 9f, new(0, 0.9f, 0), 0, 0.12f, 0.06f, 0.5f, 1.1f, 0.7f, 2.4f, 0.9f, 0f, new(0.5f, 0.75f, 0.5f), 0.03f, Tint: new(0.75f, 0.9f, 1f)),
        new("campfire embers", Owners(EmitterKind.Campfire), "flame", 5, 1.1f, new(0, 0.9f, 0), 0, 0.18f, 0.18f, 0, 0, 0.13f, 0.04f, 1f, 0.3f, new(0.5f, 0.4f, 0.5f), 0.05f, Glows: true),
        new("soul embers", Owners(EmitterKind.SoulCampfire, EmitterKind.SoulFire), "soul_fire_flame", 4, 1.1f, new(0, 0.8f, 0), 0, 0.2f, 0.15f, 0, 0, 0.13f, 0.04f, 1f, 0.3f, new(0.5f, 0.45f, 0.5f), 0.1f, Glows: true),
        new("furnace smoke", Owners(EmitterKind.Furnace), "big_smoke", 4, 1.8f, new(0, 0.55f, 0), 0, 0.1f, 0.05f, 0.12f, 1.5f, 0.22f, 0.52f, 0.7f, 0f, new(0.5f, 1.05f, 0.5f)),
        new("fire smoke", Owners(EmitterKind.Fire), "big_smoke", 4, 2.2f, new(0, 0.7f, 0), 0, 0.25f, 0.1f, 0.15f, 1.2f, 0.45f, 1.0f, 0.6f, 0f, new(0.5f, 0.7f, 0.5f), 0.3f),
        new("fire embers", Owners(EmitterKind.Fire), "flame", 3, 1.1f, new(0, 0.7f, 0), 0, 0.3f, 0.2f, 0, 0, 0.11f, 0.03f, 1f, 0.3f, new(0.5f, 0.5f, 0.5f), 0.3f, Glows: true),
        new("torch flame", Owners(EmitterKind.Torch), "flame", 2, 0.55f, new(0, 0.2f, 0), 0, 0.02f, 0.02f, 0.02f, 6f, 0.16f, 0.06f, 1f, 0.6f, new(0.5f, 0.78f, 0.5f), Glows: true),
        new("torch smoke", Owners(EmitterKind.Torch), "big_smoke", 1, 1.3f, new(0, 0.5f, 0), 0, 0.02f, 0.03f, 0.08f, 2f, 0.1f, 0.28f, 0.5f, 0f, new(0.5f, 0.82f, 0.5f)),
        new("soul torch flame", Owners(EmitterKind.SoulTorch), "soul_fire_flame", 2, 0.55f, new(0, 0.2f, 0), 0, 0.02f, 0.02f, 0.02f, 6f, 0.16f, 0.06f, 1f, 0.6f, new(0.5f, 0.78f, 0.5f), Glows: true),
        new("redstone spark", Owners(EmitterKind.RedstoneTorch), "flame", 1, 0.6f, new(0, 0.25f, 0), 0, 0.04f, 0.04f, 0, 0, 0.1f, 0.04f, 1f, 0.4f, new(0.5f, 0.78f, 0.5f), Glows: true, Tint: new(1f, 0.1f, 0.1f)),
        new("candle flame", Owners(EmitterKind.Candle), "flame", 1, 0.5f, new(0, 0.1f, 0), 0, 0.01f, 0.01f, 0.01f, 5f, 0.09f, 0.03f, 1f, 0.6f, new(0.5f, 0.5f, 0.5f), Glows: true),
        new("candle smoke", Owners(EmitterKind.Candle), "big_smoke", 1, 2f, new(0, 0.35f, 0), 0, 0.01f, 0.02f, 0.05f, 2f, 0.06f, 0.18f, 0.35f, 0f, new(0.5f, 0.55f, 0.5f)),
    };

    /// <summary>How far from the camera an emitter of each kind is worth drawing (blocks): a campfire's plume can be seen a long way off, a candle's wisp only close up.</summary>
    public static float RangeOf(EmitterKind kind) => kind switch
    {
        EmitterKind.Campfire or EmitterKind.SoulCampfire => 140f,
        EmitterKind.Furnace or EmitterKind.Fire or EmitterKind.SoulFire => 64f,
        EmitterKind.Torch or EmitterKind.SoulTorch or EmitterKind.RedstoneTorch => 40f,
        EmitterKind.Candle => 24f,
        _ => 0f,
    };

    private readonly Dictionary<EmitterKind, int[]> _byOwner = new();

    /// <summary>The kinds the pipeline is given, in the order the emitters' indices refer to.</summary>
    public ParticleKind[] Kinds { get; private set; } = System.Array.Empty<ParticleKind>();
    /// <summary>Entries left out because their sprite is not in the atlas.</summary>
    public List<string> Missing { get; } = new();

    /// <summary>The indices of the kinds an emitter of this kind shows (none when none of its sprites exist).</summary>
    public IReadOnlyList<int> KindsOf(EmitterKind owner) => _byOwner.TryGetValue(owner, out var list) ? list : System.Array.Empty<int>();

    public static ParticleCatalog Build(ParticleAssets assets)
    {
        var catalog = new ParticleCatalog();
        var kinds = new List<ParticleKind>();
        var byOwner = new Dictionary<EmitterKind, List<int>>();
        foreach (var d in Defs)
        {
            if (!assets.Sprites.TryGetValue(d.Sprite, out var at)) { catalog.Missing.Add($"{d.Name} ({d.Sprite})"); continue; }
            var tint = d.Tint ?? White;
            kinds.Add(new ParticleKind
            {
                Counts = new Vector4(d.Count, d.Life, d.Glows ? ParticleKind.FlagEmissive : 0f, at[4]),
                Sprite = new Vector4(at[0], at[1], at[2], at[3]),
                Motion = new Vector4(d.Velocity, d.Gravity),
                Scatter = new Vector4(d.Spread, d.Drift, d.Wobble, d.WobbleSpeed),
                Fade = new Vector4(d.Size0, d.Size1, d.Alpha0, d.Alpha1),
                Tint = new Vector4(tint, 1f),
                Origin = new Vector4(d.Origin, d.OriginY),
            });
            foreach (var owner in d.Owners)
            {
                if (!byOwner.TryGetValue(owner, out var list)) byOwner[owner] = list = new List<int>();
                list.Add(kinds.Count - 1);
            }
        }
        catalog.Kinds = kinds.ToArray();
        foreach (var (owner, list) in byOwner) catalog._byOwner[owner] = list.ToArray();
        return catalog;
    }
}
