using System.Formats.Tar;
using System.IO.Compression;
using AubsCraft.Admin.Server.Models;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Full backups of a server's folder as .tar.gz (worlds, config, plugins/mods and their data) and restoring
/// them. Skips what the server re-downloads itself (libraries/, cache/, versions/ - Paper's jars) and logs.
/// Backups go to Backups:Path (default /opt/minecraft/backups), one folder per server:
///   {backups}/{serverId}/{serverId}-yyyyMMdd-HHmmss[-label].tar.gz
/// Take a backup with the server STOPPED (or after save-off + save-all flush): region files written mid-copy
/// can be torn.
/// </summary>
public class BackupService
{
    private static readonly string[] SkippedTopLevel = ["libraries", "cache", "versions", "logs", "crash-reports"];

    private readonly string _root;
    private readonly ILogger<BackupService> _logger;

    public BackupService(IConfiguration configuration, ILogger<BackupService> logger)
    {
        _root = configuration.GetValue<string>("Backups:Path") ?? "/opt/minecraft/backups";
        _logger = logger;
    }

    public string Root => _root;

    public record BackupInfo(string ServerId, string FileName, string Path, long SizeBytes, DateTime CreatedUtc);

    /// <summary>Writes a backup of the server's folder and returns it.</summary>
    public async Task<BackupInfo> CreateAsync(ServerDefinition server, string? label = null, CancellationToken ct = default)
    {
        var dir = System.IO.Path.Combine(_root, server.Id);
        Directory.CreateDirectory(dir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var safeLabel = label == null ? "" : "-" + new string(label.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        var file = System.IO.Path.Combine(dir, $"{server.Id}-{stamp}{safeLabel}.tar.gz");
        var tmp = file + ".partial";

        try
        {
            await WriteArchiveAsync(server, tmp, ct);
        }
        catch
        {
            File.Delete(tmp);
            throw;
        }
        File.Move(tmp, file);
        var info = new BackupInfo(server.Id, System.IO.Path.GetFileName(file), file, new FileInfo(file).Length, DateTime.UtcNow);
        _logger.LogInformation("Backup of '{Server}': {File} ({Size:N0} bytes)", server.Id, info.FileName, info.SizeBytes);
        return info;
    }

    private static async Task WriteArchiveAsync(ServerDefinition server, string tmp, CancellationToken ct)
    {
        await using (var fs = File.Create(tmp))
        await using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        await using (var tar = new TarWriter(gz, TarEntryFormat.Pax))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(server.Path, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var rel = System.IO.Path.GetRelativePath(server.Path, path).Replace('\\', '/');
                var top = rel.Split('/')[0];
                if (SkippedTopLevel.Contains(top, StringComparer.OrdinalIgnoreCase)) continue;
                // A running server holds an OS lock on session.lock (Windows refuses to read it); it carries no
                // data - the server rewrites it on start.
                if (Path.GetFileName(path) == "session.lock") continue;
                if (Directory.Exists(path))
                {
                    var dirEntry = new PaxTarEntry(TarEntryType.Directory, rel + "/");
                    if (!OperatingSystem.IsWindows()) dirEntry.Mode = File.GetUnixFileMode(path);
                    await tar.WriteEntryAsync(dirEntry, ct);
                    continue;
                }
                // Shared read: a plugin may hold its file open (SQLite, logs).
                await using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, rel) { DataStream = src, ModificationTime = File.GetLastWriteTimeUtc(path) };
                // Keep each file's permissions: the default 0644 would make a restored world unwritable for the
                // server's group (the panel and the server are different users sharing the minecraft group).
                if (!OperatingSystem.IsWindows()) entry.Mode = File.GetUnixFileMode(path);
                await tar.WriteEntryAsync(entry, ct);
            }
        }
    }

    /// <summary>The server's backups, newest first.</summary>
    public List<BackupInfo> List(string serverId)
    {
        var dir = System.IO.Path.Combine(_root, serverId);
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*.tar.gz")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new BackupInfo(serverId, f.Name, f.FullName, f.Length, f.LastWriteTimeUtc))
            .ToList();
    }

    /// <summary>
    /// Restores a backup INTO the server's folder: everything the backup covers is replaced (what it skipped -
    /// libraries/, cache/, versions/, logs/ - is left alone). The server must be stopped.
    /// </summary>
    public async Task RestoreAsync(ServerDefinition server, string backupFileName, CancellationToken ct = default)
    {
        var file = System.IO.Path.Combine(_root, server.Id, System.IO.Path.GetFileName(backupFileName));
        if (!File.Exists(file)) throw new FileNotFoundException("No such backup", backupFileName);

        // Clear what the backup will bring back, so files created after it (a newer world region) do not survive.
        foreach (var entry in Directory.EnumerateFileSystemEntries(server.Path))
        {
            if (SkippedTopLevel.Contains(System.IO.Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
        }
        await using var fs = File.OpenRead(file);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gz, server.Path, overwriteFiles: true, ct);
        _logger.LogInformation("Restored '{Server}' from {File}", server.Id, backupFileName);
    }
}
