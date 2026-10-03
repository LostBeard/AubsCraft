using System.Diagnostics;
using System.Text.Json;
using AubsCraft.Admin.Server.Models;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Installs Minecraft server software into a folder and says how to launch it. Every download is verified
/// against the checksum its source publishes (AddonDownloader):
///   Paper    - fill.papermc.io (sha256)                    -> server.jar, "-jar server.jar"
///   Fabric   - Fabric's installer from maven.fabricmc.net (sha256) run in server mode -> fabric-server-launch.jar
///   Forge    - installer from maven.minecraftforge.net (sha256), --installServer -> @libraries/.../unix_args.txt
///   NeoForge - installer from maven.neoforged.net (sha256), --install-server     -> @libraries/.../unix_args.txt
/// Vanilla cannot use Velocity's modern forwarding (players would get offline UUIDs behind the proxy), so a
/// vanilla server is created as Fabric with no gameplay mods: the same game, and it works behind the proxy.
/// </summary>
public class ServerSoftwareService
{
    private readonly AddonDownloader _downloader;
    private readonly HttpClient _http;
    private readonly ILogger<ServerSoftwareService> _logger;

    /// <summary>The java that runs installers (the VM's /usr/bin/java; tests set their JDK 25).</summary>
    public string JavaPath { get; set; }

    public ServerSoftwareService(AddonDownloader downloader, IConfiguration configuration, ILogger<ServerSoftwareService> logger)
    {
        _downloader = downloader;
        _logger = logger;
        JavaPath = configuration.GetValue<string>("Java:Path") ?? "java";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AubsCraft-Admin/1.0 (https://github.com/LostBeard)");
    }

    /// <summary>How the installed software starts: the java arguments after the heap flags (systemd LAUNCH).</summary>
    public record Installed(string LaunchArgs, string Description);

    /// <summary>The loader that is actually installed for a requested one (Vanilla -> Fabric, see above).</summary>
    public static ServerLoader Effective(ServerLoader requested) => requested == ServerLoader.Vanilla ? ServerLoader.Fabric : requested;

    /// <summary>Release Minecraft versions this loader can install, newest first.</summary>
    public async Task<List<string>> GameVersionsAsync(ServerLoader loader, CancellationToken ct = default)
    {
        switch (Effective(loader))
        {
            case ServerLoader.Paper:
            {
                using var doc = JsonDocument.Parse(await _http.GetStringAsync("https://fill.papermc.io/v3/projects/paper", ct));
                return doc.RootElement.GetProperty("versions").EnumerateObject()
                    .SelectMany(g => g.Value.EnumerateArray().Select(v => v.GetString()!))
                    .Where(IsRelease).ToList();
            }
            case ServerLoader.Fabric:
            {
                using var doc = JsonDocument.Parse(await _http.GetStringAsync("https://meta.fabricmc.net/v2/versions/game", ct));
                return doc.RootElement.EnumerateArray().Where(v => v.GetProperty("stable").GetBoolean())
                    .Select(v => v.GetProperty("version").GetString()!).ToList();
            }
            case ServerLoader.Forge:
            {
                var promos = await ForgePromotionsAsync(ct);
                return promos.Keys.Select(k => k[..k.LastIndexOf('-')]).Distinct().OrderByDescending(ParseVersion).ToList();
            }
            case ServerLoader.NeoForge:
            {
                var versions = await NeoForgeVersionsAsync(ct);
                return versions.Select(NeoForgeGameVersion).OfType<string>().Distinct().OrderByDescending(ParseVersion).ToList();
            }
            default:
                return [];
        }
    }

    /// <summary>Installs the software into <paramref name="dir"/> (created if needed).</summary>
    public async Task<Installed> InstallAsync(ServerLoader loader, string gameVersion, string dir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dir);
        switch (Effective(loader))
        {
            case ServerLoader.Paper:
            {
                var jar = await _downloader.PaperMcAsync("paper", gameVersion, ct);
                await _downloader.DownloadAsync(jar, Path.Combine(dir, "server.jar"), ct);
                return new Installed("-jar server.jar", $"Paper {jar.Version}");
            }
            case ServerLoader.Fabric:
            {
                var installer = await MavenAsync("https://maven.fabricmc.net/net/fabricmc/fabric-installer", await FabricInstallerVersionAsync(ct), "fabric-installer", "", ct);
                var loaderVersion = await FabricLoaderVersionAsync(gameVersion, ct);
                var path = Path.Combine(dir, ".installer.jar");
                await _downloader.DownloadAsync(installer, path, ct);
                await RunInstallerAsync(dir, ["-jar", path, "server", "-dir", dir, "-mcversion", gameVersion, "-loader", loaderVersion, "-downloadMinecraft"], ct);
                File.Delete(path);
                Require(dir, "fabric-server-launch.jar", "server.jar");
                return new Installed("-jar fabric-server-launch.jar", $"Fabric loader {loaderVersion}");
            }
            case ServerLoader.Forge:
            {
                var promos = await ForgePromotionsAsync(ct);
                var forge = promos.GetValueOrDefault($"{gameVersion}-recommended") ?? promos.GetValueOrDefault($"{gameVersion}-latest")
                    ?? throw new InvalidOperationException($"Forge has no build for Minecraft {gameVersion}.");
                var full = $"{gameVersion}-{forge}";
                var installer = await MavenAsync("https://maven.minecraftforge.net/net/minecraftforge/forge", full, "forge", "-installer", ct);
                var path = Path.Combine(dir, ".installer.jar");
                await _downloader.DownloadAsync(installer, path, ct);
                await RunInstallerAsync(dir, ["-jar", path, "--installServer", dir], ct);
                File.Delete(path);
                var args = $"libraries/net/minecraftforge/forge/{full}/{ArgsFile}";
                Require(dir, args, "user_jvm_args.txt");
                return new Installed($"@user_jvm_args.txt @{args}", $"Forge {full}");
            }
            case ServerLoader.NeoForge:
            {
                var versions = await NeoForgeVersionsAsync(ct);
                var candidates = versions.Where(v => NeoForgeGameVersion(v) == gameVersion).ToList();
                var neo = candidates.LastOrDefault(v => !v.Contains('-')) ?? candidates.LastOrDefault()
                    ?? throw new InvalidOperationException($"NeoForge has no build for Minecraft {gameVersion}.");
                var installer = await MavenAsync("https://maven.neoforged.net/releases/net/neoforged/neoforge", neo, "neoforge", "-installer", ct);
                var path = Path.Combine(dir, ".installer.jar");
                await _downloader.DownloadAsync(installer, path, ct);
                await RunInstallerAsync(dir, ["-jar", path, "--install-server", dir], ct);
                File.Delete(path);
                var args = $"libraries/net/neoforged/neoforge/{neo}/{ArgsFile}";
                Require(dir, args, "user_jvm_args.txt");
                return new Installed($"@user_jvm_args.txt @{args}", $"NeoForge {neo}");
            }
            default:
                throw new NotSupportedException($"Cannot install {loader}.");
        }
    }

    /// <summary>Forge/NeoForge write both; the classpath separator differs (':' vs ';').</summary>
    private static string ArgsFile => OperatingSystem.IsWindows() ? "win_args.txt" : "unix_args.txt";

    private static void Require(string dir, params string[] files)
    {
        foreach (var f in files)
            if (!File.Exists(Path.Combine(dir, f)))
                throw new InvalidOperationException($"The installer did not produce {f}.");
    }

    private async Task RunInstallerAsync(string dir, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(JavaPath, args)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("java did not start");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { p.Kill(entireProcessTree: true); throw; }
        var output = await stdout + await stderr;
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"Installer failed (exit {p.ExitCode}): {output[^Math.Min(800, output.Length)..]}");
        _logger.LogInformation("Installer finished in {Dir}", dir);
        foreach (var log in Directory.GetFiles(dir, "*installer*.log")) File.Delete(log);
    }

    /// <summary>A maven artifact with the .sha256 the repository publishes beside it.</summary>
    private async Task<AddonArtifact> MavenAsync(string baseUrl, string version, string artifact, string classifier, CancellationToken ct)
    {
        var file = $"{artifact}-{version}{classifier}.jar";
        var url = $"{baseUrl}/{version}/{file}";
        var sha = (await _http.GetStringAsync(url + ".sha256", ct)).Trim().Split(' ')[0];
        return new AddonArtifact(artifact, file, url, "sha256", sha, version);
    }

    private async Task<string> FabricInstallerVersionAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync("https://meta.fabricmc.net/v2/versions/installer", ct));
        return doc.RootElement.EnumerateArray().First(v => v.GetProperty("stable").GetBoolean()).GetProperty("version").GetString()!;
    }

    private async Task<string> FabricLoaderVersionAsync(string gameVersion, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync($"https://meta.fabricmc.net/v2/versions/loader/{gameVersion}", ct));
        var all = doc.RootElement.EnumerateArray().Select(v => v.GetProperty("loader")).ToList();
        var pick = all.FirstOrDefault(l => l.GetProperty("stable").GetBoolean());
        if (pick.ValueKind == JsonValueKind.Undefined) pick = all.FirstOrDefault();
        if (pick.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException($"Fabric has no loader for Minecraft {gameVersion}.");
        return pick.GetProperty("version").GetString()!;
    }

    private async Task<Dictionary<string, string>> ForgePromotionsAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync("https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", ct));
        return doc.RootElement.GetProperty("promos").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }

    private async Task<List<string>> NeoForgeVersionsAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync("https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge", ct));
        return doc.RootElement.GetProperty("versions").EnumerateArray().Select(v => v.GetString()!).ToList();
    }

    /// <summary>
    /// NeoForge numbers its builds after the Minecraft version: 21.5.98 is for 1.21.5, 21.0.x for 1.21, and from
    /// Minecraft 26 on the full version leads (26.3.0.46-beta for 26.3, 26.1.2.x for 26.1.2).
    /// </summary>
    public static string? NeoForgeGameVersion(string neo)
    {
        var core = neo.Split('-')[0].Split('.');
        if (core.Length < 3 || !int.TryParse(core[0], out var major)) return null;
        if (major < 26) return core[1] == "0" ? $"1.{core[0]}" : $"1.{core[0]}.{core[1]}";
        if (core.Length < 4) return null;
        return core[2] == "0" ? $"{core[0]}.{core[1]}" : $"{core[0]}.{core[1]}.{core[2]}";
    }

    private static bool IsRelease(string v) => !v.Contains("-pre") && !v.Contains("-rc") && !v.Contains("snapshot", StringComparison.OrdinalIgnoreCase);

    private static Version ParseVersion(string v) => Version.TryParse(v.Split('-')[0], out var p) ? p : new Version(0, 0);
}
