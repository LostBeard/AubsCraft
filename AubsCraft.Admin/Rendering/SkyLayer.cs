using System.Numerics;
using AubsCraft.Assets.Model;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.VoxelEngine.Rendering;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// The sky behind the world: the gradient from the horizon's colour (the fog's) to the zenith's, the sun and the moon in its phase, stars, clouds - drawn first in the world's pass by
/// <see cref="SkyPipeline"/>, from the game's own sun, moon and cloud images. Without those images (the assets not loaded) it is the gradient alone.
/// </summary>
public sealed class SkyLayer : IDisposable
{
    /// <summary>Where the clouds float (the game's overworld height).</summary>
    public const float CloudHeight = 192.33f;
    /// <summary>How fast they drift: 0.36 blocks a tick, as the game's 0.03 map pixels of 12 blocks.</summary>
    public const float CloudBlocksPerTick = 0.36f;

    private readonly SpawnJSRuntime _js;
    private readonly SkyPipeline _pipe = new();
    private readonly WeatherPipeline _weather = new();
    private EntityTextureAtlas? _atlas;
    private SkyAssets? _assets;
    private string _origin = "";
    private bool _disposed;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public SkyLayer(SpawnJSRuntime js) => _js = js;

    public bool Ready => _pipe.IsReady;

    /// <summary>Where the rain and snow stop and what falls where (the columns' heights and biomes): fed by the worker as columns arrive.</summary>
    public WeatherMap Map { get; } = new();

    public void Init(GPUDevice device, GPUQueue queue, string colorFormat, string origin)
    {
        _origin = origin;
        _pipe.Init(device, queue, colorFormat);
        _weather.Init(device, queue, colorFormat);
    }

    /// <summary>Fetch the sun, moon and cloud images (mc-sky.json and the pixels, which go from the fetched ArrayBuffer to the GPU without entering .NET).</summary>
    public async Task LoadAssetsAsync(GPUDevice device, GPUQueue queue)
    {
        using var jsonResponse = await _js.Fetch($"{_origin}/assets/mc-sky.json", new FetchOptions { Credentials = "include" });
        if (!jsonResponse.Ok) { Console.WriteLine($"[Sky] Sky assets HTTP {jsonResponse.Status}: the sky has no sun, moon or clouds"); return; }
        var assets = System.Text.Json.JsonSerializer.Deserialize(await jsonResponse.Text(), SkyAssetsJson.Default.SkyAssets);
        if (assets == null || assets.AtlasWidth <= 0) return;
        using var pixelResponse = await _js.Fetch($"{_origin}/assets/mc-sky.rgba", new FetchOptions { Credentials = "include" });
        if (!pixelResponse.Ok) { Console.WriteLine($"[Sky] Sky atlas HTTP {pixelResponse.Status}"); return; }
        using var buffer = await pixelResponse.ArrayBuffer();
        using var pixels = new Uint8Array(buffer);
        if (pixels.Length != assets.AtlasWidth * assets.AtlasHeight * 4) { Console.WriteLine($"[Sky] Sky atlas is {pixels.Length} bytes, the assets describe {assets.AtlasWidth}x{assets.AtlasHeight}"); return; }
        var atlas = EntityTextureAtlas.Create(device, queue, assets.AtlasWidth, assets.AtlasHeight);
        atlas.WriteRegion(0, 0, assets.AtlasWidth, assets.AtlasHeight, pixels);
        _pipe.SetTextures(atlas);
        _weather.SetTextures(atlas);
        _atlas = atlas;
        _assets = assets;
        Console.WriteLine($"[Sky] Sky assets: sun {assets.Sun[2]}x{assets.Sun[3]}, moon {assets.Moon[2]}x{assets.Moon[3]}, clouds {assets.Clouds[2]}x{assets.Clouds[3]}");
    }

    /// <summary>
    /// What the sky looks like this frame, from the camera's view and the time and weather. <paramref name="serverSeconds"/> is the server's clock (the moon's phase is its day number, the clouds
    /// drift with it); a negative value means none is known (the clouds then drift with the viewer's own clock).
    /// </summary>
    public static SkyUniforms Uniforms(in ViewSetup view, float timeOfDay, float rain, float thunder, DayLighting light, double serverSeconds, double localSeconds, SkyAssets? assets)
    {
        var look = DayLighting.SkyAt(timeOfDay, rain, thunder);
        float angle = timeOfDay * MathF.Tau;
        Matrix4x4.Invert(view.ViewProjectionReversed, out var inverse);
        // The clouds drift by the viewer's own clock: the server's time stops when its day cycle does (a server kept at noon), and a drifting cloud must not stop with it
        double ticks = localSeconds * 20.0;
        int moonPhase = serverSeconds >= 0 ? (int)((long)(serverSeconds * 20.0 / 24000.0) % 8) : 0;
        static Vector4 Rect(int[]? r) => r == null || r.Length < 4 ? Vector4.Zero : new Vector4(r[0], r[1], r[2], r[3]);
        return new SkyUniforms
        {
            InverseViewProjection = inverse,
            Camera = new Vector4(view.Position, 1f),
            SunDirection = new Vector4(light.SunDirection, look.Daylight),
            Zenith = new Vector4(look.Zenith, moonPhase),
            Horizon = new Vector4(light.FogColor, look.Night),
            Cloud = new Vector4(assets == null ? 1e9f : CloudHeight, (float)(ticks * CloudBlocksPerTick % (12.0 * 256.0)), Math.Clamp(rain, 0f, 1f), -angle),
            CloudColor = new Vector4(look.CloudColour, 0.82f),
            SunRect = Rect(assets?.Sun), MoonRect = Rect(assets?.Moon), CloudRect = Rect(assets?.Clouds),
        };
    }

    public void Draw(GPURenderPassEncoder pass, in ViewSetup view, float timeOfDay, float rain, float thunder, DayLighting light, double serverSeconds)
    {
        if (_disposed) return;
        var sky = Uniforms(in view, timeOfDay, rain, thunder, light, serverSeconds, _clock.Elapsed.TotalSeconds, _assets);
        _pipe.Draw(pass, in sky);
    }

    /// <summary>
    /// Rain and snow over the world, drawn last in the pass: a streak for each column round the camera, where the map says something falls and nothing roofs it over. <paramref name="rain"/>
    /// is how hard it rains (0..1; the server's weather eased), <paramref name="light"/> how much daylight there is (the rain is dark at night).
    /// </summary>
    public void DrawWeather(GPURenderPassEncoder pass, in ViewSetup view, float rain, float daylight, double serverSeconds)
    {
        if (_disposed || _assets == null || rain < 0.02f) return;
        if (Map.Build(view.Position.X, view.Position.Z, out int x0, out int z0, out var cells)) _weather.SetCells(cells, WeatherMap.Side);
        // The viewer's own clock, not the server's: the server's time stands still when its day cycle is off, and the rain would stand with it
        double ticks = _clock.Elapsed.TotalSeconds * 20.0 % 100000.0;
        var uniforms = new WeatherUniforms
        {
            ViewProjection = view.ViewProjectionReversed,
            Camera = new Vector4(view.Position, (float)ticks),
            Params = new Vector4(Math.Clamp(rain, 0f, 1f), 0.35f + 0.65f * Math.Clamp(daylight, 0f, 1f), WeatherMap.Side, 12f),
            Window = new Vector4(x0, z0, 0f, 0f),
            RainRect = Rect(_assets.Rain),
            SnowRect = Rect(_assets.Snow),
        };
        _weather.Draw(pass, in uniforms);
    }

    private static Vector4 Rect(int[]? r) => r == null || r.Length < 4 ? Vector4.Zero : new Vector4(r[0], r[1], r[2], r[3]);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _weather.Dispose();
        _pipe.Dispose();
        _atlas?.Dispose();
    }
}
