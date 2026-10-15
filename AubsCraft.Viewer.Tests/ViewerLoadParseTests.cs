namespace AubsCraft.Viewer.Tests;

/// <summary>The load gate reads the render worker's "[Voxel]" line: both the format of the first release and the quad-buffer one.</summary>
public class ViewerLoadParseTests
{
    [Test]
    public void TheFirstReleasesLine_ParsesWithoutTheMeshCounters()
    {
        var s = ViewerLoadTests.Sample.TryParse(5,
            "[Voxel] 37 fps | frame CPU 19.1 ms (record 17.6) | 837 draws, 837 binds | 2478/8125 sections | 2226 columns+far | 460K/1635K quads");
        Assert.That(s, Is.Not.Null);
        Assert.That((s!.Fps, s.FrameCpuMs, s.RecordMs, s.Draws, s.Binds), Is.EqualTo((37, 19.1, 17.6, 837, 837)));
        Assert.That((s.VisibleSections, s.TotalSections, s.Columns, s.VisibleQuads, s.TotalQuads), Is.EqualTo((2478, 8125, 2226, 460_000L, 1_635_000L)));
        Assert.That(s.QuadBuffers, Is.EqualTo(-1), "an unpooled build reports no quad buffers");
    }

    [Test]
    public void TheQuadBufferLine_ParsesTheMeshCounters()
    {
        var s = ViewerLoadTests.Sample.TryParse(12.5,
            "[Voxel] 140 fps | frame CPU 2.4 ms (record 1.1) | 590 draws, 3 binds | 2478/8125 sections | 2226 columns+far | 460K/1635K quads | 2900 meshes (410 re-meshes, 7 waiting) in 2 quad buffers");
        Assert.That(s, Is.Not.Null);
        Assert.That((s!.Draws, s.Binds, s.Meshes, s.Remeshes, s.Waiting, s.QuadBuffers), Is.EqualTo((590, 3, 2900, 410, 7, 2)));
        Assert.That(s.Seconds, Is.EqualTo(12.5));
    }

    [Test]
    public void TheFarWindowLine_ParsesTheFarMeshCounts()
    {
        var s = ViewerLoadTests.Sample.TryParse(30,
            "[Voxel] 60 fps | frame CPU 8.0 ms (record 7.5) | 1700 draws, 40 binds | 900/3000 sections | 5300 columns+far | 300K/900K quads | 6000 meshes (900 re-meshes, 4 waiting) in 2 quad buffers | far 2900/4500 meshed");
        Assert.That(s, Is.Not.Null);
        Assert.That((s!.FarMeshed, s.FarTotal, s.QuadBuffers), Is.EqualTo((2900, 4500, 2)));
        var old = ViewerLoadTests.Sample.TryParse(1,
            "[Voxel] 60 fps | frame CPU 8.0 ms (record 7.5) | 1700 draws, 40 binds | 900/3000 sections | 5300 columns+far | 300K/900K quads | 6000 meshes (900 re-meshes, 4 waiting) in 2 quad buffers");
        Assert.That(old!.FarMeshed, Is.EqualTo(-1), "the previous build has no far counts");
    }

    [Test]
    public void OtherConsoleLines_AreNotSamples()
    {
        Assert.That(ViewerLoadTests.Sample.TryParse(1, "[RenderWorker] Atlas 1024x1024 sliced into the voxel texture array"), Is.Null);
    }
}
