using System.Text;
using System.Text.RegularExpressions;
using AubsCraft.Assets.Model;
using StbImageSharp;

namespace AubsCraft.Assets;

/// <summary>The packed particle sprites and what describes them.</summary>
public sealed record ParticleAtlasResult(ParticleAssets Assets, byte[] Rgba, string Report);

/// <summary>
/// Packs every particle texture of the client jar into one atlas (see <see cref="ParticleAssets"/>). Textures that are the frames of one animation
/// (<c>name_0</c>, <c>name_1</c>, ... the same size, numbered without a gap) are placed as one strip, frame after frame to the right, so a particle's frame
/// is an offset from its first. Shelf packed, tallest first, deterministic; nothing between neighbours (the shader fetches exact texels).
/// </summary>
public sealed partial class ParticleAtlasBuilder
{
    public const int AtlasWidth = 1024;
    private const string Folder = "assets/minecraft/textures/particle/";

    private readonly ClientJar _jar;
    private readonly string _version;

    public ParticleAtlasBuilder(ClientJar jar, string version)
    {
        _jar = jar;
        _version = version;
    }

    [GeneratedRegex(@"^(.+)_(\d+)$")]
    private static partial Regex Numbered();

    private sealed record Unit(string Name, int W, int H, int Frames, byte[] Rgba);

    public ParticleAtlasResult Build(IEnumerable<string>? onlyNames = null)
    {
        var report = new StringBuilder();
        var images = new Dictionary<string, (int w, int h, byte[] rgba)>(StringComparer.Ordinal);
        foreach (var path in _jar.List(Folder).Where(p => p.EndsWith(".png", StringComparison.Ordinal)).OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = path[Folder.Length..^4];
            if (name.Contains('/')) continue;
            var image = ImageResult.FromMemory(_jar.ReadBytes(path), ColorComponents.RedGreenBlueAlpha);
            images[name] = (image.Width, image.Height, image.Data);
        }

        // Animations: name_0..name_{n-1} of one size become a strip named without the number
        var groups = new Dictionary<string, SortedDictionary<int, string>>(StringComparer.Ordinal);
        foreach (var name in images.Keys)
        {
            var m = Numbered().Match(name);
            if (!m.Success) continue;
            var baseName = m.Groups[1].Value;
            if (!groups.TryGetValue(baseName, out var frames)) groups[baseName] = frames = new SortedDictionary<int, string>();
            frames[int.Parse(m.Groups[2].Value)] = name;
        }
        var units = new List<Unit>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (baseName, frames) in groups.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var list = frames.ToList();
            bool contiguous = list.Count >= 2 && list.Select((f, i) => f.Key == i).All(b => b);
            bool sameSize = list.All(f => images[f.Value].w == images[list[0].Value].w && images[f.Value].h == images[list[0].Value].h);
            if (!contiguous || !sameSize) continue;
            var (w, h, _) = images[list[0].Value];
            var strip = new byte[w * list.Count * h * 4];
            for (int f = 0; f < list.Count; f++)
            {
                var src = images[list[f].Value].rgba;
                for (int row = 0; row < h; row++)
                    Buffer.BlockCopy(src, row * w * 4, strip, (row * w * list.Count + f * w) * 4, w * 4);
                used.Add(list[f].Value);
            }
            units.Add(new Unit(baseName, w, h, list.Count, strip));
        }
        foreach (var (name, img) in images.OrderBy(i => i.Key, StringComparer.Ordinal))
            if (!used.Contains(name)) units.Add(new Unit(name, img.w, img.h, 1, img.rgba));
        if (onlyNames != null)
        {
            var wanted = new HashSet<string>(onlyNames, StringComparer.Ordinal);
            units = units.Where(u => wanted.Contains(u.Name)).ToList();
        }

        var placed = new Dictionary<string, int[]>(StringComparer.Ordinal);
        int x = 0, y = 0, rowHeight = 0;
        foreach (var u in units.OrderByDescending(u => u.H).ThenByDescending(u => u.W * u.Frames).ThenBy(u => u.Name, StringComparer.Ordinal))
        {
            int width = u.W * u.Frames;
            if (width > AtlasWidth) throw new InvalidOperationException($"{u.Name} is {width} px wide: wider than the {AtlasWidth} px particle atlas");
            if (x + width > AtlasWidth) { y += rowHeight; x = 0; rowHeight = 0; }
            if (rowHeight == 0) rowHeight = u.H;
            placed[u.Name] = new[] { x, y, u.W, u.H, u.Frames };
            x += width;
        }
        int height = y + rowHeight;

        var rgba = new byte[AtlasWidth * Math.Max(height, 1) * 4];
        foreach (var u in units)
        {
            var at = placed[u.Name];
            int width = u.W * u.Frames;
            for (int row = 0; row < u.H; row++)
                Buffer.BlockCopy(u.Rgba, row * width * 4, rgba, ((at[1] + row) * AtlasWidth + at[0]) * 4, width * 4);
        }

        var assets = new ParticleAssets { McVersion = _version, AtlasWidth = AtlasWidth, AtlasHeight = height, Sprites = placed };
        report.AppendLine($"Particle assets for Minecraft {_version}");
        report.AppendLine($"  {placed.Count} sprites ({placed.Values.Count(p => p[4] > 1)} animations) in {AtlasWidth}x{height} px");
        return new ParticleAtlasResult(assets, rgba, report.ToString());
    }
}
