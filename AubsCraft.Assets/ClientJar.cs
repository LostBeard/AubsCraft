using System.IO.Compression;
using System.Text;

namespace AubsCraft.Assets;

/// <summary>A Minecraft client jar (a zip) opened for reading: assets by path ("assets/minecraft/models/block/stone.json").</summary>
public sealed class ClientJar : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _entries;

    private ClientJar(ZipArchive zip)
    {
        _zip = zip;
        _entries = zip.Entries.Where(e => e.Length > 0 || !e.FullName.EndsWith('/')).ToDictionary(e => e.FullName, e => e, StringComparer.Ordinal);
    }

    public static ClientJar Open(string path) => new(new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read));

    /// <summary>A jar made of these files, in memory (tests).</summary>
    public static ClientJar FromFiles(IReadOnlyDictionary<string, byte[]> files)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in files)
            {
                var entry = zip.CreateEntry(name);
                using var s = entry.Open();
                s.Write(bytes);
            }
        stream.Position = 0;
        return new ClientJar(new ZipArchive(stream, ZipArchiveMode.Read));
    }

    public bool Exists(string path) => _entries.ContainsKey(path);

    public byte[] ReadBytes(string path)
    {
        if (!_entries.TryGetValue(path, out var entry)) throw new FileNotFoundException($"{path} is not in the jar");
        using var s = entry.Open();
        using var ms = new MemoryStream((int)entry.Length);
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public string ReadText(string path) => Encoding.UTF8.GetString(ReadBytes(path));

    /// <summary>The paths under a prefix ("assets/minecraft/blockstates/"), sorted.</summary>
    public IEnumerable<string> List(string prefix) => _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal);

    public void Dispose() => _zip.Dispose();
}
