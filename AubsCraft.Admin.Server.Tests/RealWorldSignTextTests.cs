using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// The writing on the signs of a real world (the scene the gates use: <c>SceneBuilderTests</c> gives its signs words), read through the same parser the viewer's server uses. Skipped when the world is not there.
/// </summary>
public class RealWorldSignTextTests
{
    [Test]
    public void TheSceneSignsSayWhatTheyWereGiven()
    {
        var world = Environment.GetEnvironmentVariable("AUBS_SCENE_WORLD") ?? "D:/users/tj/Projects/_scratch/scene-server";
        var regions = Path.Combine(world, "world", "region");
        if (!Directory.Exists(regions)) Assert.Ignore("No world at " + regions);
        var all = new List<(int cx, int cz, AubsCraft.Live.Model.BlockEntities.SignText sign)>();
        foreach (var file in Directory.GetFiles(regions, "r.*.mca"))
        {
            var parts = Path.GetFileNameWithoutExtension(file).Split('.');
            int rx = int.Parse(parts[1]), rz = int.Parse(parts[2]);
            foreach (var (lx, lz) in RegionReader.ListChunks(file))
            {
                ChunkResult? chunk;
                try { chunk = RegionReader.ReadChunk(file, lx, lz); } catch { continue; }
                if (chunk?.SignTexts == null) continue;
                foreach (var s in chunk.SignTexts) all.Add((rx * 32 + lx, rz * 32 + lz, s));
            }
        }
        foreach (var (cx, cz, s) in all) TestContext.Out.WriteLine($"chunk ({cx},{cz}) sign at {s.X},{s.Y},{s.Z}: {string.Join(" / ", s.Lines)} color {s.Color} glow {s.Glowing}");
        if (all.Count == 0) Assert.Ignore("The scene was built before its signs had words: run SceneBuilderTests.BuildTheScene again");
        Assert.That(all.Count, Is.GreaterThanOrEqualTo(9), "four standing, four wall, one glowing");
        Assert.That(all.Any(a => a.sign.Lines[0] == "South" && a.sign.Lines[1] == "AubsCraft" && a.sign.Color == 14), Is.True, "a red standing sign");
        Assert.That(all.Any(a => a.sign.Lines[1] == "on a wall" && a.sign.Color == 15), Is.True, "a black wall sign");
        Assert.That(all.Any(a => a.sign.Glowing && a.sign.Lines[0] == "Glowing" && a.sign.Color == 11), Is.True, "the glowing one, blue");
    }
}
