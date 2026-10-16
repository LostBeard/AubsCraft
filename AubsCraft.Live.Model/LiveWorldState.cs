namespace AubsCraft.Live.Model;

/// <summary>One entity as drawn: interpolated between two snapshots, with what an animation needs.</summary>
public readonly record struct LiveEntityView(
    int Id, string Type, float X, float Y, float Z, float Yaw, float Pitch, float BodyYaw, int Flags,
    float SpeedBlocksPerTick, float WalkedBlocks, string? Name, string? Variant)
{
    public bool OnGround => (Flags & LiveFlags.OnGround) != 0;
    public bool Sneaking => (Flags & LiveFlags.Sneaking) != 0;
    public bool Baby => (Flags & LiveFlags.Baby) != 0;
    public bool Sleeping => (Flags & LiveFlags.Sleeping) != 0;
    public bool Swimming => (Flags & LiveFlags.Swimming) != 0;
    public bool Gliding => (Flags & LiveFlags.Gliding) != 0;
    public bool IsPlayer => (Flags & LiveFlags.Player) != 0;
    public bool Riding => (Flags & LiveFlags.Riding) != 0;
    public bool Dead => (Flags & LiveFlags.Dead) != 0;
    public bool Swinging => (Flags & LiveFlags.Swinging) != 0;
    public bool Using => (Flags & LiveFlags.Using) != 0;
}

/// <summary>
/// What the live frames add up to: every entity's recent history, sampled at the DISPLAY rate. Frames come a few times a second; the picture is drawn
/// sixty. Each entity is shown a little in the past (<see cref="RenderDelaySeconds"/>), between the two snapshots that bracket that moment, so it
/// moves smoothly instead of in steps. The timeline is the server's own (a frame carries the world's tick), with the offset to the local clock
/// taken as the MINIMUM over a window: a frame that arrives late does not make the world lurch, only the best-case latency counts.
/// </summary>
public sealed class LiveWorldState
{
    private const int MaxSnapshots = 8;
    private const int OffsetWindow = 60;
    /// <summary>Further apart than this between two snapshots (blocks) and the entity teleported: no sliding across the map.</summary>
    public const float TeleportDistance = 16f;
    /// <summary>An entity absent from the frames for this long (server seconds) is gone.</summary>
    public const double GoneAfterSeconds = 1.5;
    /// <summary>Past the newest snapshot an entity is held, not extrapolated, after this long.</summary>
    public const double HoldSeconds = 0.5;

    private readonly record struct Snapshot(double Time, LiveEntity Entity, double Walked);

    private sealed class Track
    {
        public readonly List<Snapshot> Snapshots = new();
        public double MissingSince = double.NaN;
        public string World = "";
    }

    private readonly Dictionary<(string world, int id), Track> _tracks = new();
    private readonly Dictionary<(string world, int id), LivePlayer> _players = new();
    private readonly Queue<double> _offsets = new();
    private double _offset;
    private bool _haveOffset;
    private double _latestServerTime = double.NegativeInfinity;
    private double _lastRawTime = double.NegativeInfinity, _lastLocalNow;

    /// <summary>How far in the past entities are shown, in seconds. A little more than the frame interval (0.2 s), so two snapshots always bracket it.</summary>
    public double RenderDelaySeconds { get; set; } = 0.3;

    /// <summary>Entities the state knows about (all worlds).</summary>
    public int TrackCount => _tracks.Count;

    /// <summary>Players the state knows about.</summary>
    public int PlayerCount => _players.Count;

    /// <summary>The server's clock (seconds) as of the newest frame.</summary>
    public double LatestServerTime => _latestServerTime;

    /// <summary>The first world of the latest frame: the server's main world, the one the viewer shows (a server names it as it likes).</summary>
    public string? PrimaryWorld { get; private set; }

    /// <summary>The latest frame's day time in ticks and weather, for the sky.</summary>
    public long DayTime { get; private set; }
    public bool Raining { get; private set; }
    public bool Thundering { get; private set; }

    /// <summary>Take a frame that arrived at <paramref name="localNow"/> (seconds on any monotonic clock).</summary>
    public void Apply(LiveFrame frame, double localNow)
    {
        double raw = frame.FullTime / 20.0;
        if (raw < _lastRawTime) return; // an old frame that overtook a newer one: the world does not run backwards
        // A feed whose clock stands still (an older plugin sent the day time, which stops when the server's day cycle is off): its frames are put a frame's real time apart instead, so the
        // creatures still move between them and the ones that went away are still noticed
        double serverTime = raw > _lastRawTime || double.IsNegativeInfinity(_lastRawTime) ? Math.Max(raw, _latestServerTime) : _latestServerTime + Math.Max(0.0, localNow - _lastLocalNow);
        _lastRawTime = raw;
        _lastLocalNow = localNow;
        _latestServerTime = serverTime;
        DayTime = frame.DayTime;
        Raining = frame.Raining;
        Thundering = frame.Thundering;
        if (frame.Worlds.Count > 0) PrimaryWorld = frame.Worlds[0].Name;

        _offsets.Enqueue(localNow - serverTime);
        while (_offsets.Count > OffsetWindow) _offsets.Dequeue();
        _offset = _offsets.Min();
        _haveOffset = true;

        var seen = new HashSet<(string, int)>();
        foreach (var world in frame.Worlds)
        {
            foreach (var e in world.Entities)
            {
                var key = (world.Name, e.Id);
                seen.Add(key);
                if (!_tracks.TryGetValue(key, out var track)) _tracks[key] = track = new Track { World = world.Name };
                track.MissingSince = double.NaN;

                double walked = 0;
                if (track.Snapshots.Count > 0)
                {
                    var last = track.Snapshots[^1];
                    if (last.Time == serverTime) { track.Snapshots.RemoveAt(track.Snapshots.Count - 1); last = track.Snapshots.Count > 0 ? track.Snapshots[^1] : default; }
                    walked = track.Snapshots.Count > 0 ? last.Walked : 0;
                    if (track.Snapshots.Count > 0)
                    {
                        float dx = e.X - last.Entity.X, dz = e.Z - last.Entity.Z;
                        float dist = MathF.Sqrt(dx * dx + dz * dz);
                        float jump = MathF.Sqrt(dist * dist + (e.Y - last.Entity.Y) * (e.Y - last.Entity.Y));
                        if (jump < TeleportDistance) walked += dist; // a teleport is not a walk
                    }
                }
                track.Snapshots.Add(new Snapshot(serverTime, e, walked));
                if (track.Snapshots.Count > MaxSnapshots) track.Snapshots.RemoveAt(0);
            }
            foreach (var p in world.Players) _players[(world.Name, p.EntityId)] = p;
        }

        // Those not in this frame: gone after a while (a chunk unloaded, a mob despawned, a player left)
        var gone = new List<(string, int)>();
        foreach (var (key, track) in _tracks)
        {
            if (seen.Contains(key)) continue;
            if (double.IsNaN(track.MissingSince)) track.MissingSince = serverTime;
            else if (serverTime - track.MissingSince > GoneAfterSeconds) gone.Add(key);
        }
        foreach (var key in gone) { _tracks.Remove(key); _players.Remove(key); }
    }

    /// <summary>The player an entity id belongs to (skin, name, held item), or null.</summary>
    public LivePlayer? PlayerOf(string world, int entityId) => _players.TryGetValue((world, entityId), out var p) ? p : null;

    /// <summary>Every entity of a world as it should be drawn at <paramref name="localNow"/>.</summary>
    /// <param name="mayBeWanted">Asked with an entity's NEWEST known place before it is interpolated; false skips it (a view that cannot see it has no use for its smooth position). Null keeps all.</param>
    public List<LiveEntityView> Sample(string world, double localNow, Func<float, float, float, bool>? mayBeWanted = null)
    {
        var result = new List<LiveEntityView>();
        if (!_haveOffset) return result;
        double t = localNow - _offset - RenderDelaySeconds;

        foreach (var ((w, id), track) in _tracks)
        {
            if (w != world) continue;
            if (TrySample(id, track, t, mayBeWanted, out var view)) result.Add(view);
        }
        return result;
    }

    /// <summary>One entity as it should be drawn at <paramref name="localNow"/> (the same place <see cref="Sample"/> gives it), or null when it is not known or not shown.</summary>
    public LiveEntityView? SampleOne(string world, int entityId, double localNow)
    {
        if (!_haveOffset || !_tracks.TryGetValue((world, entityId), out var track)) return null;
        double t = localNow - _offset - RenderDelaySeconds;
        return TrySample(entityId, track, t, null, out var view) ? view : null;
    }

    /// <summary>The world and entity id of the player called <paramref name="name"/> (any case), or false when no such player is in the frames.</summary>
    public bool TryFindPlayer(string name, out string world, out int entityId)
    {
        foreach (var ((w, id), p) in _players)
        {
            if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            world = w;
            entityId = id;
            return true;
        }
        world = "";
        entityId = 0;
        return false;
    }

    private static bool TrySample(int id, Track track, double t, Func<float, float, float, bool>? mayBeWanted, out LiveEntityView view)
    {
        view = default;
        if (track.Snapshots.Count == 0) return false;
        var snaps = track.Snapshots;
        var newest = snaps[^1].Entity;
        if (mayBeWanted != null && !mayBeWanted(newest.X, newest.Y, newest.Z)) return false;
        if (t < snaps[0].Time) return false;                         // not yet: it appears when the moment reaches its first snapshot
        if (!double.IsNaN(track.MissingSince) && t > track.MissingSince + 0.2) return false; // it has left the frames and the moment has passed

        // The snapshots either side of t; past the newest it is held (never extrapolated beyond a short while)
        int hi = snaps.Count - 1;
        while (hi > 0 && snaps[hi - 1].Time >= t) hi--;
        var b = snaps[hi];
        if (t >= b.Time)
        {
            if (t - b.Time > HoldSeconds && !double.IsNaN(track.MissingSince)) return false;
            view = ToView(id, b.Entity, b.Entity, 0, SpeedOf(snaps, hi), b.Walked, b.Walked);
            return true;
        }
        var a = snaps[hi - 1];
        double span = b.Time - a.Time;
        float f = span <= 0 ? 1f : (float)((t - a.Time) / span);
        float dx = b.Entity.X - a.Entity.X, dy = b.Entity.Y - a.Entity.Y, dz = b.Entity.Z - a.Entity.Z;
        bool teleported = MathF.Sqrt(dx * dx + dy * dy + dz * dz) >= TeleportDistance;
        view = ToView(id, a.Entity, b.Entity, teleported ? 1f : f, SpeedOf(snaps, hi), a.Walked, b.Walked, f);
        return true;
    }

    // Horizontal blocks per tick over the interval ending at snapshot i
    private static float SpeedOf(List<Snapshot> snaps, int i)
    {
        if (i <= 0) return 0;
        var a = snaps[i - 1];
        var b = snaps[i];
        double seconds = b.Time - a.Time;
        if (seconds <= 0) return 0;
        float dx = b.Entity.X - a.Entity.X, dz = b.Entity.Z - a.Entity.Z;
        float dist = MathF.Sqrt(dx * dx + dz * dz);
        if (dist >= TeleportDistance) return 0;
        return (float)(dist / (seconds * 20.0));
    }

    private static LiveEntityView ToView(int id, LiveEntity a, LiveEntity b, float f, float speed, double walkedA, double walkedB, float walkF = 0)
    {
        static float Lerp(float x, float y, float t) => x + (y - x) * t;
        static float Angle(float x, float y, float t)
        {
            // The short way round: 170 to -170 passes through 180, not through 0
            float delta = ((y - x) % 360f + 540f) % 360f - 180f;
            return x + delta * t;
        }
        // Flags and the like are the newer snapshot's once the moment is past the older one's midpoint
        var latest = f >= 0.5f ? b : a;
        return new LiveEntityView(id, latest.Type,
            Lerp(a.X, b.X, f), Lerp(a.Y, b.Y, f), Lerp(a.Z, b.Z, f),
            Angle(a.Yaw, b.Yaw, f), Lerp(a.Pitch, b.Pitch, f), Angle(a.BodyYaw, b.BodyYaw, f),
            latest.Flags, speed, (float)(walkedA + (walkedB - walkedA) * walkF), latest.Name, latest.Variant);
    }
}
