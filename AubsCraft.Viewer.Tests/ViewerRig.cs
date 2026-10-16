using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AubsCraft.Admin.Server.Tests.Live;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// The 3D viewer exactly as a visitor gets it: the PUBLISHED server (AOT client, the build that ships) on a free
/// loopback port reading the real world, and the installed Chrome (real GPU WebGPU - Playwright's bundled
/// Chromium only has the SwiftShader software adapter) with its own throw-away profile. Nothing here touches
/// TJ's browser. Both processes are in the test host's job object, so they die with it however it exits.
/// </summary>
public sealed class ViewerRig : IAsyncDisposable
{
    public const string OwnerName = "owner";
    public const string OwnerPassword = "viewer-gate-password";
    public const string LiveSecret = "viewer-gate-live-secret";

    private readonly StringBuilder _serverLog = new();
    private readonly string _stateDir;
    private Process? _server;
    private IPlaywright? _playwright;

    public string BaseUrl { get; private set; } = "";
    public IBrowserContext Context { get; private set; } = null!;
    /// <summary>The server's stdout/stderr so far (a failing gate prints it).</summary>
    public string ServerLog { get { lock (_serverLog) return _serverLog.ToString(); } }

    private ViewerRig(string stateDir) => _stateDir = stateDir;

    /// <param name="publishDir">Output of `dotnet publish AubsCraft.Admin.Server -c Release` (framework-dependent).</param>
    /// <param name="worldServerPath">A Minecraft server folder (contains world/); only read.</param>
    public static async Task<ViewerRig> StartAsync(string publishDir, string worldServerPath, string? secondServerId = null)
    {
        var dll = Path.Combine(publishDir, "AubsCraft.Admin.Server.dll");
        if (!File.Exists(dll)) throw new FileNotFoundException($"No published server at {dll}");
        if (!Directory.Exists(Path.Combine(worldServerPath, "world")))
            throw new DirectoryNotFoundException($"No world folder under {worldServerPath}");

        var stateDir = Path.Combine(Path.GetTempPath(), "aubscraft-viewer-gate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDir);
        var rig = new ViewerRig(stateDir);
        try
        {
            // Every process started from here on (the server, Playwright's driver, Chrome) is killed with this host.
            try { KillOnExitJob.Add(Process.GetCurrentProcess()); }
            catch (InvalidOperationException ex)
            {
                // Not fatal: DisposeAsync still stops what this rig started. Only a crashed host would leak them.
                TestContext.Progress.WriteLine($"[ViewerRig] No kill-on-exit safety net: {ex.Message}");
            }
            await rig.StartServerAsync(dll, publishDir, worldServerPath, secondServerId);
            await rig.StartBrowserAsync();
            await rig.SignInAsync();
            return rig;
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    private async Task StartServerAsync(string dll, string publishDir, string worldServerPath, string? secondServerId)
    {
        // A second registered server (the same world folder under another id) so the gate can switch the viewed server: the panel shows a
        // picker only when there is more than one. Pre-writing the registry replaces the one built from the single-server config.
        if (secondServerId != null)
        {
            var path = worldServerPath.Replace('\\', '/');
            File.WriteAllText(Path.Combine(_stateDir, "servers.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                Servers = new[]
                {
                    new { Id = "aubscraft", Name = "AubsCraft", Path = path },
                    new { Id = secondServerId, Name = "Second", Path = path },
                },
            }));
        }
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        BaseUrl = $"http://127.0.0.1:{port}";

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = publishDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(dll);
        psi.Environment["ASPNETCORE_URLS"] = BaseUrl;
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        // Every state file in the throw-away folder: the gate never reads or writes the real panel's accounts/registry.
        psi.Environment["Servers__RegistryPath"] = Path.Combine(_stateDir, "servers.json");
        psi.Environment["ActivityLog__FilePath"] = Path.Combine(_stateDir, "activity-log.json");
        psi.Environment["Auth__UsersPath"] = Path.Combine(_stateDir, "users.json");
        psi.Environment["Auth__CredentialsPath"] = Path.Combine(_stateDir, "admin.json");
        psi.Environment["Auth__InviteCodesPath"] = Path.Combine(_stateDir, "invite-codes.json");
        psi.Environment["Auth__WhitelistAuditPath"] = Path.Combine(_stateDir, "whitelist-audit.json");
        psi.Environment["Moderation__BansPath"] = Path.Combine(_stateDir, "bans.json");
        // The live feed's shared secret, so the gate can post frames the way the Minecraft plugin does (the real secret is never used here)
        psi.Environment["Live__Secret"] = LiveSecret;
        psi.Environment["Live__SecretPath"] = Path.Combine(_stateDir, "live-secret.txt");
        // The single-server config becomes the one registered server (ServerRegistry creates servers.json from it).
        // RCON is unreachable here; the world is read straight from the region files.
        psi.Environment["Minecraft__ServerPath"] = worldServerPath.Replace('\\', '/');

        _server = new Process { StartInfo = psi };
        _server.OutputDataReceived += (_, e) => AppendLog(e.Data);
        _server.ErrorDataReceived += (_, e) => AppendLog(e.Data);
        _server.Start();
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();

        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (_server.HasExited)
                throw new InvalidOperationException($"The published server exited with {_server.ExitCode}:\n{ServerLog}");
            try
            {
                using var r = await http.GetAsync("/api/public/status");
                if (r.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(250);
        }
        throw new TimeoutException($"The published server did not answer within 60 s:\n{ServerLog}");
    }

    private void AppendLog(string? line)
    {
        if (line == null) return;
        lock (_serverLog) _serverLog.AppendLine(line);
    }

    private async Task StartBrowserAsync()
    {
        _playwright = await Playwright.CreateAsync();
        Context = await _playwright.Chromium.LaunchPersistentContextAsync(
            Path.Combine(_stateDir, "chrome-profile"),
            new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = false,
                // Installed Chrome: the bundled Chromium only exposes the SwiftShader SOFTWARE adapter here.
                Channel = "chrome",
                ViewportSize = new ViewportSize { Width = 1280, Height = 720 },
                Args =
                [
                    "--enable-unsafe-webgpu",
                    // No "Vulkan" in this list: it pushes Dawn off D3D12 and Chrome silently falls back to SwiftShader.
                    "--enable-features=WebGPUService,SkiaGraphite",
                    "--ignore-gpu-blocklist",
                    "--disable-software-rasterizer",
                    "--no-sandbox",
                    // On TJ's desktop (he likes to watch the gate run; 10-15), top left. Nothing that throttles a window: a vsync'd or
                    // backgrounded Chrome pins every build near 30 FPS, which measures nothing.
                    "--window-position=0,0",
                    "--disable-gpu-vsync",
                    "--disable-frame-rate-limit",
                    "--disable-renderer-backgrounding",
                    "--disable-background-timer-throttling",
                    "--disable-backgrounding-occluded-windows",
                    // A fake microphone (a tone) that needs no permission: the voice gate speaks into it
                    "--use-fake-ui-for-media-stream",
                    "--use-fake-device-for-media-stream",
                ],
            });
    }

    private async Task SignInAsync()
    {
        // The API request context shares its cookies with the browser context: log in once, the pages are signed in.
        var setup = await Context.APIRequest.PostAsync(BaseUrl + "/api/auth/setup",
            new APIRequestContextOptions { DataObject = new { Username = OwnerName, Password = OwnerPassword } });
        if (!setup.Ok) throw new InvalidOperationException($"Owner setup failed: {setup.Status} {await setup.TextAsync()}");
        var login = await Context.APIRequest.PostAsync(BaseUrl + "/api/auth/login",
            new APIRequestContextOptions { DataObject = new { Username = OwnerName, Password = OwnerPassword } });
        if (!login.Ok) throw new InvalidOperationException($"Login failed: {login.Status} {await login.TextAsync()}");
    }

    /// <summary>Send one live frame to the panel as the Minecraft plugin does (loopback, shared secret). Returns the status code.</summary>
    public async Task<int> PostLiveFrameAsync(byte[] frame, string serverId = "aubscraft")
    {
        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/live/ingest/{serverId}") { Content = new ByteArrayContent(frame) };
        req.Headers.Add("X-Live-Secret", LiveSecret);
        using var resp = await http.SendAsync(req);
        return (int)resp.StatusCode;
    }

    /// <summary>Send one changed chunk to the panel as the Minecraft plugin does (loopback, shared secret): the panel serves it instead of the file's and tells the viewers.</summary>
    public async Task<int> PostLiveChunkAsync(int cx, int cz, byte[] body, string serverId = "aubscraft")
    {
        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/live/chunk/{serverId}?cx={cx}&cz={cz}") { Content = new ByteArrayContent(body) };
        req.Headers.Add("X-Live-Secret", LiveSecret);
        using var resp = await http.SendAsync(req);
        return (int)resp.StatusCode;
    }

    /// <summary>A world chunk column exactly as the viewer's full-3D path receives it.</summary>
    public async Task<ChunkColumn> GetChunkAsync(int cx, int cz)
    {
        var r = await Context.APIRequest.GetAsync($"{BaseUrl}/api/world/chunk/{cx}/{cz}");
        if (!r.Ok) throw new InvalidOperationException($"Chunk ({cx},{cz}): HTTP {r.Status}");
        return ChunkColumn.Parse(await r.BodyAsync());
    }

    public async ValueTask DisposeAsync()
    {
        try { if (Context != null) await Context.CloseAsync(); } catch { }
        try { _playwright?.Dispose(); } catch { }
        try
        {
            if (_server is { HasExited: false }) { _server.Kill(entireProcessTree: true); _server.WaitForExit(10_000); }
        }
        catch { }
        _server?.Dispose();
        try { Directory.Delete(_stateDir, recursive: true); } catch { }
    }
}

/// <summary>One 16 x 384 x 16 column in the viewer's wire format: palette strings, then ushort indices (x + z*16 + y*256).</summary>
public sealed class ChunkColumn
{
    public const int Height = 384, BaseY = -64;
    public required List<string> Palette { get; init; }
    public required ushort[] Blocks { get; init; }

    public static ChunkColumn Parse(byte[] data)
    {
        int o = 0;
        int count = BitConverter.ToInt32(data, o); o += 4;
        var palette = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            int len = BitConverter.ToInt32(data, o); o += 4;
            palette.Add(Encoding.UTF8.GetString(data, o, len)); o += len;
        }
        var blocks = new ushort[(data.Length - o) / 2];
        Buffer.BlockCopy(data, o, blocks, 0, blocks.Length * 2);
        if (blocks.Length != 16 * Height * 16) throw new InvalidDataException($"Column has {blocks.Length} blocks, expected {16 * Height * 16}");
        return new ChunkColumn { Palette = palette, Blocks = blocks };
    }

    /// <summary>World Y of the top of the highest block at (x, z) that is neither air nor water, or null for an empty column.</summary>
    public int? SurfaceTop(int x, int z) => HighestWhere(x, z, name => !IsAir(name) && !IsWater(name));

    /// <summary>World Y of the top of the highest block at (x, z) that is not air (water counts), or null.</summary>
    public int? TopOfAnything(int x, int z) => HighestWhere(x, z, name => !IsAir(name));

    private int? HighestWhere(int x, int z, Func<string, bool> match)
    {
        for (int y = Height - 1; y >= 0; y--)
            if (match(Palette[Blocks[x + z * 16 + y * 256]])) return y + BaseY + 1;
        return null;
    }

    private static bool IsAir(string n) => n is "minecraft:air" or "minecraft:cave_air" or "minecraft:void_air";

    /// <summary>True when the block at column-local (x, y from the column bottom, z) is air; false outside the column.</summary>
    public bool IsAirAt(int x, int y, int z)
        => x is >= 0 and < 16 && z is >= 0 and < 16 && y is >= 0 and < Height && IsAir(Palette[Blocks[x + z * 16 + y * 256]]);
    private static bool IsWater(string n) => n is "minecraft:water" or "minecraft:flowing_water";
}
