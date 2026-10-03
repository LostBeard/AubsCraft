using System.Security.Cryptography;
using System.IO.Compression;
using System.Text.Json;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// The Java the live tests run servers with: Temurin 25.0.4.1 - the same Java the production VM runs
/// (OpenJDK 25.0.4.1). Velocity 4.x needs 25 (class file 69). Unzipped once under
/// %LOCALAPPDATA%\AubsCraft.Tests (nothing installed, no PATH change), SHA-256 checked.
/// </summary>
public static class Jdk
{
    private const string Folder = "jdk-25.0.4.1+1";
    private static readonly Lazy<Task<string>> JavaExe = new(EnsureAsync);

    public static Task<string> JavaAsync() => JavaExe.Value;

    private static async Task<string> EnsureAsync()
    {
        var root = PaperServer.CacheRoot;
        var java = Path.Combine(root, Folder, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        if (File.Exists(java)) return java;

        using var http = new HttpClient();
        using var meta = JsonDocument.Parse(await http.GetStringAsync(
            "https://api.adoptium.net/v3/assets/release_name/eclipse/jdk-25.0.4.1+1?architecture=x64&image_type=jdk&os=windows"));
        var package = meta.RootElement.GetProperty("binaries")[0].GetProperty("package");
        var zip = Path.Combine(root, "jdk25.zip");
        await using (var src = await http.GetStreamAsync(package.GetProperty("link").GetString()))
        await using (var dst = File.Create(zip))
            await src.CopyToAsync(dst);
        using (var s = File.OpenRead(zip))
        {
            var sha = Convert.ToHexStringLower(SHA256.HashData(s));
            if (sha != package.GetProperty("checksum").GetString())
                throw new InvalidDataException("JDK zip SHA-256 mismatch: " + sha);
        }
        ZipFile.ExtractToDirectory(zip, root, overwriteFiles: true);
        File.Delete(zip);
        return java;
    }
}
