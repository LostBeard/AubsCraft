using System.Security.Cryptography;
using System.Text.Json;
using AubsCraft.Admin.Server.Models;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// A server's plugins/mods, known exactly: every jar is identified on Modrinth by its sha1 (/v2/version_files), so the
/// panel knows its project, version, what it requires and whether players need it too. On top of that:
///   Install - the project plus every library it requires (once each), each download checked against Modrinth's sha512;
///             a mod players need on their own game (client_side "required") joins the server's ClientMods, so the
///             Quest page, the PC pack and the proxy gate follow it.
///   Update  - the newest build for this exact loader + Minecraft version (/v2/version_files/update), swapped in.
///   Remove  - refused while another installed add-on requires it, and for the ones the proxy needs; a client mod also
///             leaves ClientMods.
/// Jars Modrinth does not know (added by hand) are listed by name and can still be toggled and removed.
/// </summary>
public class AddonManagerService
{
    /// <summary>The proxy's player forwarding: without it nobody can join through the proxy.</summary>
    public static readonly string[] Protected = ["fabricproxy-lite", "proxy-compatible-forge"];
    private const string Api = "https://api.modrinth.com/v2";

    private readonly ServerRegistry _registry;
    private readonly AddonDownloader _downloader;
    private readonly ProxyService _proxy;
    private readonly ILogger<AddonManagerService> _logger;

    public AddonManagerService(ServerRegistry registry, AddonDownloader downloader, ProxyService proxy, ILogger<AddonManagerService> logger)
    {
        _registry = registry;
        _downloader = downloader;
        _proxy = proxy;
        _logger = logger;
    }

    private ServerDefinition Get(string id) => _registry.Get(id) ?? throw new InvalidOperationException($"No server '{id}'.");

    private static string Dir(ServerDefinition def) => def.AddonsPath ?? throw new InvalidOperationException($"{def.Name} runs no plugins or mods.");

    private static string? GameVersionFilter(ServerDefinition def) => def.Loader == ServerLoader.Paper ? null : def.GameVersion;

    public async Task<List<InstalledAddon>> ListAsync(string serverId, bool checkUpdates = false, CancellationToken ct = default)
    {
        var def = Get(serverId);
        var dir = Dir(def);
        if (!Directory.Exists(dir)) return [];
        var files = Directory.GetFiles(dir, "*.jar").Concat(Directory.GetFiles(dir, "*.jar.disabled")).OrderBy(Path.GetFileName).ToList();
        var hashes = files.ToDictionary(f => f, Sha1);

        var versions = new Dictionary<string, JsonElement>();
        if (hashes.Count > 0)
        {
            using var doc = JsonDocument.Parse(await _downloader.ModrinthPostAsync($"{Api}/version_files", new { hashes = hashes.Values.Distinct(), algorithm = "sha1" }, ct));
            foreach (var p in doc.RootElement.EnumerateObject()) versions[p.Name] = p.Value.Clone();
        }
        var projects = new Dictionary<string, JsonElement>();
        var ids = versions.Values.Select(v => v.GetProperty("project_id").GetString()!).Distinct().ToList();
        if (ids.Count > 0)
        {
            using var doc = JsonDocument.Parse(await _downloader.ModrinthGetAsync($"{Api}/projects?ids={Uri.EscapeDataString(JsonSerializer.Serialize(ids))}", ct));
            foreach (var p in doc.RootElement.EnumerateArray()) projects[p.GetProperty("id").GetString()!] = p.Clone();
        }
        var updates = new Dictionary<string, JsonElement>();
        if (checkUpdates && versions.Count > 0)
        {
            var loaders = ModrinthService.LoadersFor(def.Loader);
            object body = GameVersionFilter(def) is { } gv
                ? new { hashes = versions.Keys, algorithm = "sha1", loaders, game_versions = new[] { gv } }
                : new { hashes = versions.Keys, algorithm = "sha1", loaders };
            using var doc = JsonDocument.Parse(await _downloader.ModrinthPostAsync($"{Api}/version_files/update", body, ct));
            foreach (var p in doc.RootElement.EnumerateObject()) updates[p.Name] = p.Value.Clone();
        }

        var result = new List<InstalledAddon>();
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var enabled = !fileName.EndsWith(".disabled", StringComparison.Ordinal);
            if (!versions.TryGetValue(hashes[file], out var v))
            {
                result.Add(new InstalledAddon(fileName, enabled, fileName.Replace(".jar.disabled", "").Replace(".jar", ""), "", null, null, false, false, null, []));
                continue;
            }
            var projectId = v.GetProperty("project_id").GetString()!;
            var project = projects.GetValueOrDefault(projectId);
            var slug = project.ValueKind == JsonValueKind.Object ? project.GetProperty("slug").GetString()! : projectId;
            var title = project.ValueKind == JsonValueKind.Object ? project.GetProperty("title").GetString()! : slug;
            var client = project.ValueKind == JsonValueKind.Object && project.GetProperty("client_side").GetString() == "required";
            string? update = null;
            if (updates.TryGetValue(hashes[file], out var u) && u.GetProperty("id").GetString() != v.GetProperty("id").GetString())
                update = u.GetProperty("version_number").GetString();
            result.Add(new InstalledAddon(fileName, enabled, title, v.GetProperty("version_number").GetString()!, projectId, slug,
                client, Protected.Contains(slug), update, []));
        }
        // Who needs whom: an add-on is "required by" every installed add-on whose version lists it as required.
        var byProject = result.Where(a => a.ProjectId != null).ToDictionary(a => a.ProjectId!, a => a);
        foreach (var (file, hash) in hashes)
        {
            if (!versions.TryGetValue(hash, out var v)) continue;
            var needer = result.First(a => a.FileName == Path.GetFileName(file));
            if (!needer.Enabled) continue;
            foreach (var dep in AddonDownloader.RequiredProjectIds(v))
                if (byProject.TryGetValue(dep, out var needed)) needed.RequiredBy.Add(needer.Name);
        }
        return result;
    }

    /// <summary>Installs a Modrinth project (newest build for this server) with every library it requires that is not installed yet.</summary>
    public async Task<string> InstallAsync(string serverId, string project, CancellationToken ct = default)
    {
        var def = Get(serverId);
        var dir = Dir(def);
        Directory.CreateDirectory(dir);
        var installed = await ListAsync(serverId, false, ct);
        var have = installed.Where(a => a.ProjectId != null).SelectMany(a => new[] { a.ProjectId!, a.Slug! }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (have.Contains(project)) return $"{installed.First(a => a.ProjectId == project || a.Slug == project).Name} is already installed.";

        List<AddonArtifact>? artifacts = null;
        Exception? last = null;
        foreach (var loader in ModrinthService.LoadersFor(def.Loader))
        {
            try { artifacts = await _downloader.ModrinthWithDependenciesAsync([project], loader, GameVersionFilter(def), have, ct); break; }
            catch (InvalidOperationException ex) { last = ex; }
        }
        if (artifacts == null) throw new InvalidOperationException(last?.Message ?? "No build for this server.");

        foreach (var a in artifacts)
        {
            await _downloader.DownloadAsync(a, Path.Combine(dir, a.FileName), ct);
            _logger.LogWarning("Installed {File} ({Version}) on {Server}", a.FileName, a.Version, def.Id);
        }
        var main = artifacts[0];
        var note = "";
        using (var doc = JsonDocument.Parse(await _downloader.ModrinthGetAsync($"{Api}/project/{Uri.EscapeDataString(main.ProjectId ?? project)}", ct)))
        {
            var slug = doc.RootElement.GetProperty("slug").GetString()!;
            if (def.Loader != ServerLoader.Paper && doc.RootElement.GetProperty("client_side").GetString() == "required"
                && !def.ClientMods.Contains(slug, StringComparer.OrdinalIgnoreCase))
            {
                def.ClientMods.Add(slug);
                _registry.Update(def);
                ClientModsChanged();
                note = " Players need it on their game too: it's now on the Quest setup and in the PC mod pack (re-download it).";
            }
        }
        var libs = artifacts.Count > 1 ? $" with {string.Join(", ", artifacts.Skip(1).Select(a => a.Name))}" : "";
        return $"Installed {main.Name} {main.Version}{libs}. Restart {def.Name} to load it.{note}";
    }

    /// <summary>Swaps an installed add-on for its newest build for this server.</summary>
    public async Task<string> UpdateAsync(string serverId, string fileName, CancellationToken ct = default)
    {
        var def = Get(serverId);
        var dir = Dir(def);
        var addon = (await ListAsync(serverId, true, ct)).FirstOrDefault(a => a.FileName == fileName)
            ?? throw new InvalidOperationException("No such add-on.");
        if (addon.UpdateVersion == null) return $"{addon.Name} is up to date.";
        var path = Path.Combine(dir, fileName);
        var loaders = ModrinthService.LoadersFor(def.Loader);
        object body = GameVersionFilter(def) is { } gv
            ? new { loaders, game_versions = new[] { gv } }
            : new { loaders };
        using var doc = JsonDocument.Parse(await _downloader.ModrinthPostAsync($"{Api}/version_file/{Sha1(path)}/update?algorithm=sha1", body, ct));
        var artifact = AddonDownloader.FromModrinthVersion(doc.RootElement, addon.Slug ?? addon.Name);
        var target = Path.Combine(dir, artifact.FileName + (addon.Enabled ? "" : ".disabled"));
        await _downloader.DownloadAsync(artifact, target, ct);
        if (!string.Equals(target, path, StringComparison.Ordinal)) File.Delete(path);
        _logger.LogWarning("Updated {Name} on {Server}: {Old} -> {New}", addon.Name, def.Id, addon.Version, artifact.Version);
        var note = addon.ClientNeeded ? " Players need the new version too: update the headsets (Quest setup) and re-download the PC pack." : "";
        return $"Updated {addon.Name} to {artifact.Version}. Restart {def.Name} to load it.{note}";
    }

    public async Task<string> RemoveAsync(string serverId, string fileName, CancellationToken ct = default)
    {
        var def = Get(serverId);
        var addon = (await ListAsync(serverId, false, ct)).FirstOrDefault(a => a.FileName == fileName)
            ?? throw new InvalidOperationException("No such add-on.");
        if (addon.Protected) throw new InvalidOperationException($"{addon.Name} lets players in through the proxy; it can't be removed.");
        if (addon.RequiredBy.Count > 0) throw new InvalidOperationException($"{addon.Name} is needed by {string.Join(", ", addon.RequiredBy)}. Remove those first.");
        File.Delete(Path.Combine(Dir(def), fileName));
        _logger.LogWarning("Removed {File} from {Server}", fileName, def.Id);
        var note = "";
        if (addon.Slug != null && def.ClientMods.RemoveAll(m => string.Equals(m, addon.Slug, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            _registry.Update(def);
            ClientModsChanged();
            note = " It's no longer needed on players' games (the Quest setup will offer to remove it).";
        }
        return $"Removed {addon.Name}. Restart {def.Name} to unload it.{note}";
    }

    /// <summary>The proxy gate's list of servers that need client mods follows ClientMods.</summary>
    private void ClientModsChanged()
    {
        if (_registry.Proxy is { } proxy && Directory.Exists(proxy.Path)) ProxyService.WriteJavaOnly(proxy, _registry.All, _proxy.PublicUrl);
    }

    private static string Sha1(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA1.HashData(s));
    }
}

/// <summary>An installed plugin/mod. ProjectId/Slug are null for a jar Modrinth does not know.</summary>
public record InstalledAddon(string FileName, bool Enabled, string Name, string Version, string? ProjectId, string? Slug,
    bool ClientNeeded, bool Protected, string? UpdateVersion, List<string> RequiredBy);
