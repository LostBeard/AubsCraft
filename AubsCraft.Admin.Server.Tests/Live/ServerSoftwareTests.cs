using System.Diagnostics;
using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server.Tests.Live;

/// <summary>
/// Installs each loader for real (verified downloads, the loaders' own installers) and STARTS the result with
/// exactly the java arguments the systemd unit will use (JAVA_OPTS + LaunchArgs + nogui), on Java 25.
/// </summary>
[NonParallelizable]
public class ServerSoftwareTests
{
    private ServerSoftwareService _software = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        _software = new ServerSoftwareService(new AddonDownloader(), TestUtil.Config(), TestUtil.Log<ServerSoftwareService>())
        {
            JavaPath = await Jdk.JavaAsync(),
        };
    }

    [TestCase(ServerLoader.Paper)]
    [TestCase(ServerLoader.Fabric)]
    [TestCase(ServerLoader.Forge)]
    [TestCase(ServerLoader.NeoForge)]
    public async Task Installs_AndStarts(ServerLoader loader)
    {
        var dir = Path.Combine(PaperServer.CacheRoot, "install-" + loader.ToString().ToLowerInvariant());
        if (Directory.Exists(dir)) Directory.Delete(dir, true);

        var installed = await _software.InstallAsync(loader, "1.21.5", dir);
        TestContext.Progress.WriteLine($"{loader}: {installed.Description} -> {installed.LaunchArgs}");

        File.WriteAllText(Path.Combine(dir, "eula.txt"), "eula=true\n");
        File.WriteAllText(Path.Combine(dir, "server.properties"), "server-ip=127.0.0.1\nserver-port=25700\nonline-mode=false\nview-distance=4\n");
        var done = await StartUntilDoneAsync(dir, installed.LaunchArgs, TimeSpan.FromMinutes(8));
        Assert.That(done, Is.True, $"{loader} did not reach 'Done'");
    }

    [Test]
    public async Task VersionLists_IncludeTheServersVersion()
    {
        foreach (var loader in new[] { ServerLoader.Paper, ServerLoader.Fabric, ServerLoader.Forge, ServerLoader.NeoForge })
        {
            var versions = await _software.GameVersionsAsync(loader);
            Assert.That(versions, Does.Contain("1.21.5"), $"{loader}: {string.Join(", ", versions.Take(8))}");
            Assert.That(versions, Has.None.Contains("-rc").And.None.Contains("-pre"), loader.ToString());
        }
    }

    [TestCase("21.5.98", "1.21.5")]
    [TestCase("21.0.167", "1.21")]
    [TestCase("26.3.0.46-beta", "26.3")]
    [TestCase("26.1.2.12", "26.1.2")]
    public void NeoForgeVersions_MapToMinecraftVersions(string neo, string mc) =>
        Assert.That(ServerSoftwareService.NeoForgeGameVersion(neo), Is.EqualTo(mc));

    /// <summary>
    /// Runs "java -Xms512M -Xmx2G {launch} nogui" in dir (the systemd unit's ExecStart with its EnvironmentFile
    /// values), waits for "Done (" then sends "stop". Args files (@file) are expanded by java itself.
    /// </summary>
    internal static async Task<bool> StartUntilDoneAsync(string dir, string launchArgs, TimeSpan timeout)
    {
        var args = new List<string> { "-Xms512M", "-Xmx2G" };
        args.AddRange(launchArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        args.Add("nogui");
        var psi = new ProcessStartInfo(await Jdk.JavaAsync(), args)
        {
            WorkingDirectory = dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        KillOnExitJob.Add(p);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tail = new List<string>();
        void On(string? l)
        {
            if (l == null) return;
            lock (tail) { tail.Add(l); if (tail.Count > 40) tail.RemoveAt(0); }
            if (l.Contains("Done (") && l.Contains("For help")) done.TrySetResult();
        }
        p.OutputDataReceived += (_, e) => On(e.Data);
        p.ErrorDataReceived += (_, e) => On(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        var finished = await Task.WhenAny(done.Task, p.WaitForExitAsync(), Task.Delay(timeout));
        var ok = finished == done.Task;
        if (!ok) lock (tail) TestContext.Progress.WriteLine(string.Join('\n', tail));
        if (!p.HasExited)
        {
            await p.StandardInput.WriteLineAsync("stop");
            await p.StandardInput.FlushAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await p.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { p.Kill(true); }
        }
        return ok;
    }
}
