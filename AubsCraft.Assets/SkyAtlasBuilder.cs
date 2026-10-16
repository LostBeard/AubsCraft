using AubsCraft.Assets.Model;
using StbImageSharp;

namespace AubsCraft.Assets;

/// <summary>The packed sky images and what describes them.</summary>
public sealed record SkyAtlasResult(SkyAssets Assets, byte[] Rgba, string Report);

/// <summary>Packs the game's sun, moon phases and cloud map (textures/environment) into one atlas (see <see cref="SkyAssets"/>): the clouds at the corner, the sun and the moon beside them.</summary>
public sealed class SkyAtlasBuilder
{
    private const string Folder = "assets/minecraft/textures/environment/";
    private readonly ClientJar _jar;
    private readonly string _version;

    public SkyAtlasBuilder(ClientJar jar, string version)
    {
        _jar = jar;
        _version = version;
    }

    private ImageResult Read(string name)
    {
        var path = Folder + name + ".png";
        if (!_jar.Exists(path)) throw new InvalidOperationException($"The client jar has no {path}");
        return ImageResult.FromMemory(_jar.ReadBytes(path), ColorComponents.RedGreenBlueAlpha);
    }

    public SkyAtlasResult Build()
    {
        var clouds = Read("clouds");
        var sun = Read("sun");
        var moon = Read("moon_phases");
        if (moon.Width % 4 != 0 || moon.Height % 2 != 0) throw new InvalidOperationException($"moon_phases is {moon.Width}x{moon.Height}: expected four frames across and two down");
        int moonW = moon.Width / 4, moonH = moon.Height / 2;
        int width = clouds.Width + Math.Max(sun.Width, moon.Width);
        int height = Math.Max(clouds.Height, sun.Height + moon.Height);
        var rgba = new byte[width * height * 4];
        void Copy(ImageResult img, int x0, int y0)
        {
            for (int row = 0; row < img.Height; row++)
                Buffer.BlockCopy(img.Data, row * img.Width * 4, rgba, ((y0 + row) * width + x0) * 4, img.Width * 4);
        }
        Copy(clouds, 0, 0);
        Copy(sun, clouds.Width, 0);
        Copy(moon, clouds.Width, sun.Height);
        var assets = new SkyAssets
        {
            McVersion = _version, AtlasWidth = width, AtlasHeight = height,
            Clouds = new[] { 0, 0, clouds.Width, clouds.Height },
            Sun = new[] { clouds.Width, 0, sun.Width, sun.Height },
            Moon = new[] { clouds.Width, sun.Height, moonW, moonH },
        };
        return new SkyAtlasResult(assets, rgba, $"Sky assets for Minecraft {_version}\n  sun {sun.Width}x{sun.Height}, moon {moonW}x{moonH} x 8, clouds {clouds.Width}x{clouds.Height} in {width}x{height} px\n");
    }
}
