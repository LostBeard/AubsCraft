using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace AubsCraft.Admin.Services;

/// <summary>
/// Client-side per-server state in OPFS (Origin Private File System): where the camera was last, so the next visit opens there.
///
/// Storage layout (one folder per Minecraft server - each has its own world; set ServerId first):
///   /aubscraft-cache/servers/{serverId}/meta.json   - camera position
///
/// (The viewer used to cache heightmap frames here too. The voxel renderer streams columns and heightmaps in the server's global
/// block ids and caches nothing yet - offline mode is PLANS D5 - so the old heightmaps/ folder is removed when found.)
/// </summary>
public sealed class WorldCacheService
{
    private readonly SpawnJSRuntime _js;
    private string _serverId = "";

    public WorldCacheService(SpawnJSRuntime js)
    {
        _js = js;
    }

    /// <summary>
    /// The Minecraft server whose state this cache holds. Every server gets its own folder, so switching
    /// servers never shows another world's camera.
    /// </summary>
    public string ServerId
    {
        get => _serverId;
        set => _serverId = value;
    }

    /// <summary>aubscraft-cache/servers/{ServerId}, created on demand. Caller disposes.</summary>
    private async Task<FileSystemDirectoryHandle> GetServerDir(bool create = true)
    {
        if (string.IsNullOrEmpty(_serverId))
            throw new InvalidOperationException("WorldCacheService.ServerId must be set before the cache is used.");
        using var storage = _js.Get<StorageManager>("navigator.storage");
        using var root = await storage.GetDirectory();
        using var cacheDir = await root.GetDirectoryHandle("aubscraft-cache", create);
        await RemoveLegacySingleServerCacheAsync(cacheDir);
        using var servers = await cacheDir.GetDirectoryHandle("servers", create);
        var serverDir = await servers.GetDirectoryHandle(_serverId, create);
        await RemoveOldHeightmapCacheAsync(serverDir);
        return serverDir;
    }

    private bool _legacyChecked;

    /// <summary>
    /// Before multi-server support the cache lived directly in aubscraft-cache/ (heightmaps/ + meta.json). It
    /// belongs to no server folder and would never be read again, so it is deleted once.
    /// </summary>
    private async Task RemoveLegacySingleServerCacheAsync(FileSystemDirectoryHandle cacheDir)
    {
        if (_legacyChecked) return;
        _legacyChecked = true;
        foreach (var name in new[] { "heightmaps", "meta.json" })
        {
            try
            {
                await cacheDir.RemoveEntry(name, true);
                Console.WriteLine($"[WorldCache] Removed the old single-server cache entry '{name}'");
            }
            catch
            {
                // NotFoundError: nothing left from the single-server layout.
            }
        }
    }

    private bool _oldHeightmapsChecked;

    /// <summary>The region files of the old renderer's heightmap cache: nothing reads them any more, so they are deleted once.</summary>
    private async Task RemoveOldHeightmapCacheAsync(FileSystemDirectoryHandle serverDir)
    {
        if (_oldHeightmapsChecked) return;
        _oldHeightmapsChecked = true;
        try
        {
            await serverDir.RemoveEntry("heightmaps", true);
            Console.WriteLine("[WorldCache] Removed the old heightmap cache");
        }
        catch
        {
            // NotFoundError: there is none.
        }
    }

    /// <summary>Clear this server's cached data from OPFS (camera position).</summary>
    public async Task ClearAsync()
    {
        try
        {
            using var storage = _js.Get<StorageManager>("navigator.storage");
            using var root = await storage.GetDirectory();
            using var cacheDir = await root.GetDirectoryHandle("aubscraft-cache", true);
            using var servers = await cacheDir.GetDirectoryHandle("servers", true);
            await servers.RemoveEntry(_serverId, true);
            Console.WriteLine($"[WorldCache] OPFS cache cleared for server '{_serverId}'");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WorldCache] Clear failed: {ex.Message}");
        }
    }

    /// <summary>Save camera position for next session startup.</summary>
    public async Task SaveCameraPositionAsync(float x, float y, float z, float pitch, float yaw)
    {
        try
        {
            using var serverDir = await GetServerDir();
            using var fileHandle = await serverDir.GetFileHandle("meta.json", true);
            using var writable = await fileHandle.CreateWritable();
            var json = $"{{\"x\":{x:F1},\"y\":{y:F1},\"z\":{z:F1},\"pitch\":{pitch:F1},\"yaw\":{yaw:F1}}}";
            await writable.Write(json);
            await writable.Close();
        }
        catch (Exception ex) { Console.WriteLine($"[Cache] Save camera error: {ex.Message}"); }
    }

    /// <summary>Load saved camera position from last session.</summary>
    public async Task<(float x, float y, float z, float pitch, float yaw)?> LoadCameraPositionAsync()
    {
        try
        {
            using var serverDir = await GetServerDir(create: false);
            using var fileHandle = await serverDir.GetFileHandle("meta.json");
            using var file = await fileHandle.GetFile();
            var text = await file.Text();
            var doc = System.Text.Json.JsonDocument.Parse(text);
            var r = doc.RootElement;
            return (
                r.GetProperty("x").GetSingle(),
                r.GetProperty("y").GetSingle(),
                r.GetProperty("z").GetSingle(),
                r.GetProperty("pitch").GetSingle(),
                r.GetProperty("yaw").GetSingle()
            );
        }
        catch (Exception ex) { Console.WriteLine($"[Cache] Load camera error: {ex.Message}"); return null; }
    }
}
