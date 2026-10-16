using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.VoxelEngine.Rendering;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// The light of the world round the camera: for the columns of a window round the camera's column that are loaded, the server's sky and block light (<c>/api/world/chunk-light</c>, a byte a block,
/// 98,304 a column, packed on the wire), written to the <see cref="LightVolume"/> the block shader lights by. The window follows the camera; a column that leaves it loses its light and has it
/// fetched again if it comes back. A column the server has no light for (never lit) is asked for once a minute, and until it comes it is lit as open sky, which is how the world was
/// drawn before there was any light.
/// </summary>
public sealed class LightField : IDisposable
{
    /// <summary>The window's side in columns (a radius of eight chunks: 128 blocks, past which the world is lit as it always was).</summary>
    public const int Side = 17;
    /// <summary>Slots for the window and a few over, so a column coming in does not wait for one going out.</summary>
    public const int Slots = Side * Side + 8;
    private const int ParallelFetches = 3;
    private const double RetryAfterSeconds = 60;

    private readonly SpawnJSRuntime _js;
    private readonly string _baseUrl, _serverQuery;
    private readonly HashSet<(int cx, int cz)> _present = new();
    private readonly Dictionary<(int cx, int cz), double> _failed = new();
    private readonly HashSet<(int cx, int cz)> _inFlight = new();
    private readonly List<(int cx, int cz)> _evicted = new();
    private readonly List<(int d, int cx, int cz)> _candidates = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private int _lastCx = int.MinValue, _lastCz = int.MinValue;
    private bool _disposed;

    public LightField(SpawnJSRuntime js, string baseUrl, string serverQuery)
    {
        _js = js;
        _baseUrl = baseUrl;
        _serverQuery = serverQuery;
    }

    public LightVolume Volume { get; } = new();

    /// <summary>Columns whose light is in the volume.</summary>
    public int Count => _present.Count;

    /// <summary>Whether the world is lit by it (the viewer's setting).</summary>
    public bool Enabled
    {
        get => Volume.Enabled;
        set => Volume.Enabled = value;
    }

    public void Init(GPUDevice device, GPUQueue queue) => Volume.Init(device, queue, Slots, Side);

    /// <summary>Follow the camera (its column) and start fetching what is missing, nearest first; <paramref name="isLoaded"/> says which columns are in the world (light is only fetched for those).</summary>
    public void Update(int cameraCx, int cameraCz, Func<int, int, bool> isLoaded)
    {
        if (_disposed || !Volume.IsReady) return;
        if (cameraCx != _lastCx || cameraCz != _lastCz)
        {
            _lastCx = cameraCx;
            _lastCz = cameraCz;
            _evicted.Clear();
            Volume.SetWindow(cameraCx, cameraCz, _evicted);
            foreach (var key in _evicted) _present.Remove(key);
        }
        if (!Enabled || _inFlight.Count >= ParallelFetches) return;

        _candidates.Clear();
        double now = _clock.Elapsed.TotalSeconds;
        int half = Side / 2;
        for (int dz = -half; dz <= half; dz++)
            for (int dx = -half; dx <= half; dx++)
            {
                var key = (cameraCx + dx, cameraCz + dz);
                if (_present.Contains(key) || _inFlight.Contains(key)) continue;
                if (_failed.TryGetValue(key, out var at) && now - at < RetryAfterSeconds) continue;
                if (!isLoaded(key.Item1, key.Item2)) continue;
                _candidates.Add((dx * dx + dz * dz, key.Item1, key.Item2));
            }
        if (_candidates.Count == 0) return;
        _candidates.Sort((a, b) => a.d.CompareTo(b.d));
        foreach (var (_, cx, cz) in _candidates)
        {
            if (_inFlight.Count >= ParallelFetches) break;
            _inFlight.Add((cx, cz));
            _ = FetchAsync(cx, cz);
        }
    }

    /// <summary>A column changed in the running world: fetch its light again and write it over the old (the old shows meanwhile). Nothing for a column that has no light yet.</summary>
    public void Refresh(int cx, int cz)
    {
        if (_disposed || !Enabled || !_present.Contains((cx, cz)) || !_inFlight.Add((cx, cz))) return;
        _ = FetchAsync(cx, cz);
    }

    /// <summary>A column is gone from the world: its light goes with it.</summary>
    public void RemoveColumn(int cx, int cz)
    {
        _present.Remove((cx, cz));
        _failed.Remove((cx, cz));
        Volume.Release(cx, cz);
    }

    private async Task FetchAsync(int cx, int cz)
    {
        try
        {
            using var response = await _js.Fetch($"{_baseUrl}/api/world/chunk-light/{cx}/{cz}?{_serverQuery}", new FetchOptions { Credentials = "include" });
            if (!response.Ok)
            {
                _failed[(cx, cz)] = _clock.Elapsed.TotalSeconds;
                return;
            }
            using var buffer = await response.ArrayBuffer();
            using var bytes = new Uint8Array(buffer);
            if (_disposed || bytes.Length != LightVolume.ColumnBytes)
            {
                _failed[(cx, cz)] = _clock.Elapsed.TotalSeconds;
                return;
            }
            // The window may have moved on while the bytes came
            int slot = Volume.Assign(cx, cz);
            if (slot < 0) return;
            Volume.Write(slot, bytes);
            _present.Add((cx, cz));
        }
        catch (Exception ex)
        {
            _failed[(cx, cz)] = _clock.Elapsed.TotalSeconds;
            Console.WriteLine($"[Light] Column ({cx},{cz}) light failed: {ex.Message}");
        }
        finally
        {
            _inFlight.Remove((cx, cz));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Volume.Dispose();
    }
}
