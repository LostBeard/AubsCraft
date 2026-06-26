// -----------------------------------------------------------------------------
//  AubsCraft - update cross-play / cross-version plugins
//
//  Run:   dotnet run sync-plugins.cs
//   or:   double-click update-server-plugins.bat
//
//  NOTE: the source is named sync-plugins.cs (NOT update-*) on purpose. Windows'
//  installer-detection heuristic auto-elevates any .exe whose name contains
//  "update"/"setup"/"install", which blocks `dotnet run` on the built exe. The
//  double-click .bat can keep "update" in its name - that only affects .exe files.
//
//  What it does (so ANY Minecraft client can join):
//    - Geyser-Spigot    : lets Bedrock clients (phone/console/Switch/Quest) join
//    - Floodgate-Spigot : authenticates Bedrock players (kept in lockstep w/ Geyser)
//    - ViaVersion       : lets NEWER Java clients join an older server
//    - ViaBackwards     : lets OLDER Java clients join a newer server
//    - ViaRewind        : extends ViaBackwards down to 1.8.x / 1.7.10 clients
//
//  Pipeline per plugin: download latest -> verify the jar -> back up the old one
//  -> copy into the plugins folder -> (after all copies) chmod readable -> restart.
//
//  IMPORTANT gotcha this script handles for you:
//    New files written through the M: mount land as owner-only (zed, 0700), which
//    the 'minecraft' service user CANNOT read -> the plugin fails with
//    "Permission denied". So after copying we chmod 0644 every jar over SSH.
//    (Overwriting an existing jar keeps its old readable perms; only brand-new
//    files hit this - but we chmod all of them every run to be safe.)
//
//  Requirements on this PC: the aubscraft VM mounted at M:, and `ssh aubscraft`
//  configured (key-based, passwordless sudo for systemctl - same as deploy-aubscraft.bat).
// -----------------------------------------------------------------------------

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

// ---- Configuration ----------------------------------------------------------
const string SshHost      = "aubscraft";                                   // ssh alias -> zed@192.168.1.142
const string WinPlugins   = @"M:\opt\minecraft\server\plugins";            // Windows view of the plugins dir
const string WinBackup    = @"M:\opt\minecraft\server\plugins-backup";     // old jars are archived here (dated)
const string WinVerHist   = @"M:\opt\minecraft\server\version_history.json";
const string WinLatestLog = @"M:\opt\minecraft\server\logs\latest.log";
const string LinuxPlugins = "/opt/minecraft/server/plugins";               // path as the VM sees it (for ssh)
const string Service      = "minecraft.service";
const string UserAgent    = "AubsCraft-Updater/1.0 (lostit1278@gmail.com)";

var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
http.Timeout = TimeSpan.FromMinutes(5);

Console.WriteLine("=== AubsCraft plugin updater ===\n");

// ---- Detect the server's Minecraft version (for Modrinth compatibility) -----
string mc = DetectMcVersion();
Console.WriteLine($"Server Minecraft version: {mc}\n");

// ---- Resolve download URLs --------------------------------------------------
// Geyser + Floodgate: GeyserMC's stable "latest build" endpoints (always current).
string geyserUrl    = "https://download.geysermc.org/v2/projects/geyser/versions/latest/builds/latest/downloads/spigot";
string floodgateUrl = "https://download.geysermc.org/v2/projects/floodgate/versions/latest/builds/latest/downloads/spigot";

// Via* family: latest STABLE release on Modrinth that lists this MC version.
// They release as a matched set, so picking "latest stable for this MC" lines them up.
Console.WriteLine("Resolving latest Via* releases from Modrinth...");
string viaVersionUrl   = ModrinthLatestStable("viaversion",   mc);
string viaBackwardsUrl = ModrinthLatestStable("viabackwards", mc);
string viaRewindUrl    = ModrinthLatestStable("viarewind",    mc);
Console.WriteLine();

var plugins = new (string FileName, string Url)[]
{
    ("Geyser-Spigot.jar",    geyserUrl),
    ("Floodgate-Spigot.jar", floodgateUrl),
    ("ViaVersion.jar",       viaVersionUrl),
    ("ViaBackwards.jar",     viaBackwardsUrl),
    ("ViaRewind.jar",        viaRewindUrl),
};

Directory.CreateDirectory(WinBackup);
string tmp = Path.Combine(Path.GetTempPath(), "aubscraft-plugins-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tmp);
string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

var installed = new List<string>();
var failed    = new List<string>();

// ---- Download + verify + back up + copy ------------------------------------
foreach (var (fileName, url) in plugins)
{
    try
    {
        Console.WriteLine($"[{fileName}]");
        string tmpJar = Path.Combine(tmp, fileName);

        Console.WriteLine($"  download  {url}");
        byte[] bytes = await http.GetByteArrayAsync(url);
        await File.WriteAllBytesAsync(tmpJar, bytes);

        string ver = ReadPluginVersion(tmpJar);   // also validates it's a real plugin jar
        Console.WriteLine($"  verified  version {ver} ({bytes.Length:N0} bytes)");

        string target = Path.Combine(WinPlugins, fileName);
        if (File.Exists(target))
        {
            string oldVer = TryReadPluginVersion(target) ?? "unknown";
            string safeVer = Regex.Replace(oldVer, @"[^A-Za-z0-9.\-]+", "_").Trim('_');
            string bak = Path.Combine(WinBackup, $"{fileName}.bak-{safeVer}-{stamp}");
            File.Copy(target, bak, overwrite: true);
            Console.WriteLine($"  backup    old {oldVer} -> {Path.GetFileName(bak)}");
        }
        else
        {
            Console.WriteLine("  backup    (none - new plugin)");
        }

        File.Copy(tmpJar, target, overwrite: true);
        Console.WriteLine($"  installed -> {target}\n");
        installed.Add($"{fileName} {ver}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAILED    {ex.Message}\n");
        failed.Add(fileName);
    }
}

try { Directory.Delete(tmp, true); } catch { /* best effort */ }

if (installed.Count == 0)
{
    Console.WriteLine("Nothing installed - aborting before restart.");
    return 1;
}

// ---- Fix perms so the 'minecraft' service user can READ the new jars --------
// (New files via the mount are owner-only; chmod 0644 makes them world-readable.
//  zed owns the new files so no sudo is needed; already-readable jars are left as-is.)
string chmodList = string.Join(' ',
    plugins.Select(p => $"{LinuxPlugins}/{p.FileName}"));
Console.WriteLine("Fixing jar permissions over SSH (chmod 0644)...");
Ssh($"chmod 0644 {chmodList} 2>/dev/null; true");

// ---- Restart the server -----------------------------------------------------
Console.WriteLine($"Restarting {Service} (this kicks any online players)...");
int rc = Ssh($"sudo systemctl restart {Service} && echo RESTART_ISSUED");
if (rc != 0)
{
    Console.WriteLine("Restart command returned non-zero - check the VM manually.");
    return 1;
}

// ---- Verify the boot --------------------------------------------------------
Console.WriteLine("\nWaiting for the server to finish booting...");
bool booted = false;
for (int i = 0; i < 80; i++)        // up to ~4 minutes
{
    Thread.Sleep(3000);
    string log = TryReadText(WinLatestLog);
    if (Regex.IsMatch(log, @"Done \([0-9].*help")) { booted = true; break; }
}

string finalLog = TryReadText(WinLatestLog);
Console.WriteLine(booted ? "Server reported Done.\n" : "Did not see 'Done' in time - check logs.\n");

void Report(string label, string needle)
{
    bool ok = finalLog.Contains(needle, StringComparison.OrdinalIgnoreCase);
    Console.WriteLine($"  {(ok ? "OK  " : "??  ")}{label}");
}
Console.WriteLine("Plugin load check:");
Report("Geyser on UDP 19132", "Started Geyser on UDP port 19132");
Report("ViaVersion enabled",  "Enabling ViaVersion");
Report("ViaBackwards enabled","Enabling ViaBackwards");
Report("ViaRewind enabled",   "Enabling ViaRewind");

bool permDenied = finalLog.Contains("Permission denied", StringComparison.OrdinalIgnoreCase);
if (permDenied)
    Console.WriteLine("\n  WARNING: 'Permission denied' present in log - a jar may be unreadable.");

// ---- Summary ----------------------------------------------------------------
Console.WriteLine("\n=== Summary ===");
foreach (var s in installed) Console.WriteLine($"  installed  {s}");
foreach (var f in failed)    Console.WriteLine($"  FAILED     {f}");
Console.WriteLine($"  backups    {WinBackup}");
Console.WriteLine("\nFinal step (only you can do this): connect a client and confirm.");
Console.WriteLine("  Java:    mc.spawndev.com");
Console.WriteLine("  Bedrock: mc.spawndev.com  port 19132");
return failed.Count == 0 ? 0 : 1;


// ============================ helpers ========================================

string DetectMcVersion()
{
    try
    {
        string j = File.ReadAllText(WinVerHist);
        var m = Regex.Match(j, @"MC:\s*([0-9][0-9.]+)");
        if (m.Success) return m.Groups[1].Value;
    }
    catch { /* fall through */ }
    // Fallback: a versions/<x> folder name.
    try
    {
        foreach (var d in Directory.GetDirectories(@"M:\opt\minecraft\server\versions"))
        {
            string name = Path.GetFileName(d);
            if (Regex.IsMatch(name, @"^[0-9][0-9.]+$")) return name;
        }
    }
    catch { /* ignore */ }
    throw new Exception("Could not detect the server Minecraft version. Edit DetectMcVersion().");
}

// Query Modrinth for the newest *release* (not snapshot/beta) of a plugin that
// supports the given Minecraft version, and return its primary jar download URL.
string ModrinthLatestStable(string slug, string mcVersion)
{
    string gv = Uri.EscapeDataString($"[\"{mcVersion}\"]");
    string api = $"https://api.modrinth.com/v2/project/{slug}/version?game_versions={gv}";
    string json = http.GetStringAsync(api).GetAwaiter().GetResult();
    using var doc = JsonDocument.Parse(json);

    foreach (var v in doc.RootElement.EnumerateArray())
    {
        if (v.GetProperty("version_type").GetString() != "release") continue;
        string num = v.GetProperty("version_number").GetString() ?? "?";
        // prefer the file marked primary, else the first .jar
        JsonElement? chosen = null;
        foreach (var f in v.GetProperty("files").EnumerateArray())
        {
            string fn = f.GetProperty("filename").GetString() ?? "";
            if (!fn.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
            if (f.GetProperty("primary").GetBoolean()) { chosen = f; break; }
            chosen ??= f;
        }
        if (chosen is null) continue;
        string url = chosen.Value.GetProperty("url").GetString()!;
        Console.WriteLine($"  {slug,-13} {num}");
        return url;
    }
    throw new Exception($"No stable {slug} release found on Modrinth for MC {mcVersion}.");
}

// Read the 'version:' field out of a jar's plugin.yml. Throws if not a valid plugin jar.
string ReadPluginVersion(string jarPath)
{
    using var zip = ZipFile.OpenRead(jarPath);
    var entry = zip.GetEntry("plugin.yml")
        ?? throw new Exception("plugin.yml not found - not a valid Spigot/Paper plugin jar (download failed?).");
    using var sr = new StreamReader(entry.Open());
    string yml = sr.ReadToEnd();
    var m = Regex.Match(yml, @"(?m)^version:\s*[""']?([^""'\r\n]+)");
    return m.Success ? m.Groups[1].Value.Trim() : "unknown";
}

string? TryReadPluginVersion(string jarPath)
{
    try { return ReadPluginVersion(jarPath); } catch { return null; }
}

string TryReadText(string path)
{
    try { return File.ReadAllText(path); } catch { return ""; }
}

// Run a command on the VM over SSH, streaming output. Returns the exit code.
int Ssh(string remoteCommand)
{
    var psi = new ProcessStartInfo("ssh") { RedirectStandardOutput = true, RedirectStandardError = true };
    psi.ArgumentList.Add("-o");
    psi.ArgumentList.Add("BatchMode=yes");
    psi.ArgumentList.Add(SshHost);
    psi.ArgumentList.Add(remoteCommand);
    using var p = Process.Start(psi)!;
    string o = p.StandardOutput.ReadToEnd();
    string e = p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (!string.IsNullOrWhiteSpace(o)) Console.WriteLine("  " + o.Trim().Replace("\n", "\n  "));
    if (!string.IsNullOrWhiteSpace(e)) Console.WriteLine("  " + e.Trim().Replace("\n", "\n  "));
    return p.ExitCode;
}
