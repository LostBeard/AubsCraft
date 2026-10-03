using AubsCraft.Admin.Server.Services;
using Microsoft.Data.Sqlite;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// The panel must read CoreProtect's WAL-mode SQLite database without creating database.db-shm / -wal: on the
/// VM those files came out owned by the panel's user (0700) and CoreProtect then failed SQLITE_CANTOPEN on
/// every start (found 2026-10-03, nothing had been logged for rollback since April).
/// </summary>
public class CoreProtectDbTests
{
    /// <summary>A WAL database with CoreProtect's co_block shape, written and closed the way CoreProtect leaves it on shutdown.</summary>
    private static string CreateWalDatabase(int rows)
    {
        var path = Path.Combine(TestUtil.NewTempDir(), "database.db");
        using (var w = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            w.Open();
            using var cmd = w.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE co_block (time INTEGER, action INTEGER);";
            cmd.ExecuteNonQuery();
            for (int i = 0; i < rows; i++)
            {
                cmd.CommandText = $"INSERT INTO co_block VALUES ({i}, {i % 2})";
                cmd.ExecuteNonQuery();
            }
        }
        // A clean close checkpoints and removes -wal/-shm: the state while the Minecraft server is stopped.
        Assert.That(File.Exists(path + "-shm") || File.Exists(path + "-wal"), Is.False, "precondition: no side files");
        return path;
    }

    [Test]
    public void Reading_CreatesNoSideFiles_AndSeesTheData()
    {
        var path = CreateWalDatabase(25);
        using (var conn = PlayerStatsService.OpenCoreProtectReadOnly(path))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT action, COUNT(*) FROM co_block GROUP BY action ORDER BY action";
            using var r = cmd.ExecuteReader();
            var counts = new List<(long, long)>();
            while (r.Read()) counts.Add((r.GetInt64(0), r.GetInt64(1)));
            Assert.That(counts, Is.EqualTo(new[] { (0L, 13L), (1L, 12L) }));

            // While the panel's connection is open - the moment that used to create them.
            Assert.That(File.Exists(path + "-shm"), Is.False, "panel created database.db-shm");
            Assert.That(File.Exists(path + "-wal"), Is.False, "panel created database.db-wal");
        }
        SqliteConnection.ClearAllPools();
    }

    [Test]
    public void APathWithSpaces_Opens()
    {
        var dir = Path.Combine(TestUtil.NewTempDir(), "Core Protect");
        Directory.CreateDirectory(dir);
        var src = CreateWalDatabase(3);
        var path = Path.Combine(dir, "database.db");
        File.Copy(src, path);
        using var conn = PlayerStatsService.OpenCoreProtectReadOnly(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM co_block";
        Assert.That(cmd.ExecuteScalar(), Is.EqualTo(3L));
    }
}
