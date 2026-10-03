using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AubsCraft.Admin.Server.Models;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// A REAL Paper 1.21.5 server (the exact build production runs, SHA-256 checked) started as a local Java
/// process for the integration tests. Each server keeps a persistent folder under
/// %LOCALAPPDATA%\AubsCraft.Tests\servers\{name} so Paper's one-time library download is reused, but its
/// world, whitelist and ban lists are wiped on every start so each run begins clean.
/// </summary>
public sealed class PaperServer : IAsyncDisposable
{
    public const string GameVersion = "1.21.5";
    private const int Build = 114;
    private const string JarSha256 = "2ae6ae22adf417699746e0f89fc2ef6cb6ee050a5f6608cee58f0535d60b509e";

    public static string CacheRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AubsCraft.Tests");

    public string Name { get; }
    public string Dir { get; }
    public int Port { get; }
    public int RconPort { get; }
    public string RconPassword { get; }
    public int MaxPlayers { get; }
    public string LevelName { get; }

    private readonly Process _process;
    private readonly List<string> _output = [];
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private PaperServer(string name, string dir, int port, int rconPort, string rconPassword, int maxPlayers, string levelName, Process process)
    {
        Name = name;
        RconPassword = rconPassword;
        Dir = dir;
        Port = port;
        RconPort = rconPort;
        MaxPlayers = maxPlayers;
        LevelName = levelName;
        _process = process;
    }

    /// <summary>A registry definition pointing at this server.</summary>
    public ServerDefinition ToDefinition(string id) => new()
    {
        Id = id,
        Name = Name,
        Loader = ServerLoader.Paper,
        GameVersion = GameVersion,
        Path = Dir,
        ServiceName = "test-" + id,
        RconHost = "127.0.0.1",
        RconPort = RconPort,
        RconPassword = RconPassword,
        GamePort = Port,
        MemoryMb = 1024,
    };

    /// <summary>
    /// Starts a server. fresh = wipe the world, lists, config and plugins first (libraries/, cache/ and
    /// versions/ stay: Paper's one-time downloads); fresh = false restarts it as it is on disk (after a test
    /// edited its config). beforeStart runs on the folder just before java starts (e.g. to add plugins).
    /// </summary>
    public static async Task<PaperServer> StartAsync(string name, int port, int rconPort, int maxPlayers,
        string levelType, string levelName, TimeSpan timeout, bool fresh = true, Func<string, Task>? beforeStart = null)
    {
        var jar = await EnsureJarAsync();
        var dir = Path.Combine(CacheRoot, "servers", name);
        Directory.CreateDirectory(dir);
        string rconPassword;

        if (fresh)
        {
            foreach (var d in Directory.GetDirectories(dir))
            {
                var n = Path.GetFileName(d);
                if (n is "libraries" or "cache" or "versions") continue;
                Directory.Delete(d, recursive: true);
            }
            foreach (var f in Directory.GetFiles(dir)) File.Delete(f);

            File.WriteAllText(Path.Combine(dir, "eula.txt"), "eula=true\n");
            rconPassword = "test-" + Guid.NewGuid().ToString("N")[..12];
            File.WriteAllText(Path.Combine(dir, "server.properties"), string.Join('\n',
            "server-ip=127.0.0.1",
            $"server-port={port}",
            "enable-rcon=true",
            $"rcon.port={rconPort}",
            $"rcon.password={rconPassword}",
            "online-mode=false",
            $"max-players={maxPlayers}",
            $"level-type={levelType}",
            $"level-name={levelName}",
            "spawn-protection=0",
            "view-distance=4",
            "simulation-distance=4",
            "enable-query=false",
            "") );
        }
        else
        {
            rconPassword = File.ReadLines(Path.Combine(dir, "server.properties"))
                .First(l => l.StartsWith("rcon.password=", StringComparison.Ordinal))["rcon.password=".Length..];
        }
        if (beforeStart != null) await beforeStart(dir);

        // The production VM's Java (25), not whatever is on PATH.
        var psi = new ProcessStartInfo(await Jdk.JavaAsync(), ["-Xms512M", "-Xmx1G", "-jar", jar, "--nogui"])
        {
            WorkingDirectory = dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var process = Process.Start(psi) ?? throw new InvalidOperationException("java did not start");
        KillOnExitJob.Add(process); // dies with the test host, even if teardown never runs
        var started = new PaperServer(name, dir, port, rconPort, rconPassword, maxPlayers, levelName, process);
        process.OutputDataReceived += (_, e) => started.OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => started.OnLine(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var finished = await Task.WhenAny(started._done.Task, process.WaitForExitAsync(), Task.Delay(timeout));
        if (finished != started._done.Task)
        {
            var tail = string.Join('\n', started.OutputTail(40));
            await started.DisposeAsync();
            throw new InvalidOperationException($"Paper '{name}' did not finish starting (exited={process.HasExited}). Last output:\n{tail}");
        }
        return started;
    }

    private void OnLine(string? line)
    {
        if (line == null) return;
        lock (_output) _output.Add(line);
        // "[12:00:00 INFO]: Done (12.345s)! For help, type "help""
        if (line.Contains("Done (") && line.Contains("For help")) _done.TrySetResult();
    }

    public List<string> OutputTail(int n)
    {
        lock (_output) return _output.TakeLast(n).ToList();
    }

    // One download shared by every server starting in parallel (two writers to one temp file collide).
    private static readonly Lazy<Task<string>> Jar = new(DownloadJarAsync);

    private static Task<string> EnsureJarAsync() => Jar.Value;

    private static async Task<string> DownloadJarAsync()
    {
        Directory.CreateDirectory(CacheRoot);
        var jar = Path.Combine(CacheRoot, $"paper-{GameVersion}-{Build}.jar");
        if (File.Exists(jar) && Sha256(jar) == JarSha256) return jar;

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AubsCraft-Tests/1.0 (https://github.com/LostBeard)");
        using var meta = JsonDocument.Parse(await http.GetStringAsync(
            $"https://fill.papermc.io/v3/projects/paper/versions/{GameVersion}/builds/{Build}"));
        var url = meta.RootElement.GetProperty("downloads").GetProperty("server:default").GetProperty("url").GetString()!;
        var tmp = jar + ".download";
        await using (var src = await http.GetStreamAsync(url))
        await using (var dst = File.Create(tmp))
            await src.CopyToAsync(dst);
        var sha = Sha256(tmp);
        if (sha != JarSha256)
        {
            File.Delete(tmp);
            throw new InvalidDataException($"Paper jar SHA-256 mismatch: {sha}");
        }
        File.Move(tmp, jar, overwrite: true);
        return jar;
    }

    private static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }

    public async ValueTask DisposeAsync()
    {
        if (_process.HasExited) { _process.Dispose(); return; }
        try
        {
            await _process.StandardInput.WriteLineAsync("stop");
            await _process.StandardInput.FlushAsync();
        }
        catch (IOException) { }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await _process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            // Our own process (by its PID), never by image name.
            _process.Kill(entireProcessTree: true);
        }
        _process.Dispose();
    }
}
