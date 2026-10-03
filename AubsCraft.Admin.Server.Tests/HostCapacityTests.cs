using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// Capacity warnings (never limits) before starting a server, on the production VM's real size
/// (7,894 MB / 4 cores after the 2026-10-03 resize) and its real primary server (3 GB heap).
/// </summary>
public class HostCapacityTests
{
    private const long VmMb = 7894;
    private const int VmCores = 4;

    private static ServerDefinition Server(string id, int heapMb) => new() { Id = id, Name = id, Path = "/x", MemoryMb = heapMb };

    [Test]
    public void ThePrimaryAlone_Fits()
    {
        var c = HostCapacityService.Evaluate(VmMb, VmCores, [], Server("aubscraft", 3072));
        Assert.That(c.Warnings, Is.Empty);
        Assert.That(c.RunningServers, Is.EqualTo(1));
        Assert.That(c.NeededMemoryMb, Is.EqualTo(3072 + 768 + HostCapacityService.OsReserveMb));
    }

    [Test]
    public void ASecondSmallServer_Fits()
    {
        var c = HostCapacityService.Evaluate(VmMb, VmCores, [Server("aubscraft", 3072)], Server("creative", 1536));
        Assert.That(c.Warnings, Is.Empty, string.Join(" | ", c.Warnings));
    }

    [Test]
    public void ASecondLargeServer_WarnsAboutMemory()
    {
        var c = HostCapacityService.Evaluate(VmMb, VmCores, [Server("aubscraft", 3072)], Server("creative", 2048));
        Assert.That(c.Warnings, Has.Count.EqualTo(1));
        Assert.That(c.Warnings[0], Does.Contain("7,894 MB"));
    }

    [Test]
    public void MoreServersThanCores_WarnsAboutCpu()
    {
        var running = Enumerable.Range(1, 4).Select(i => Server("s" + i, 512)).ToList();
        var c = HostCapacityService.Evaluate(64_000, VmCores, running, Server("s5", 512));
        Assert.That(c.Warnings, Has.Count.EqualTo(1));
        Assert.That(c.Warnings[0], Does.Contain("5 running servers on 4 CPU core(s)"));
    }

    [Test]
    public void StartingAnAlreadyRunningServer_IsNotCountedTwice()
    {
        var c = HostCapacityService.Evaluate(VmMb, VmCores, [Server("aubscraft", 3072)], Server("aubscraft", 3072));
        Assert.That(c.RunningServers, Is.EqualTo(1));
        Assert.That(c.Warnings, Is.Empty);
    }

    [Test]
    public void ABiggerMachine_RemovesTheWarning()
    {
        // The same two servers that warn on today's VM are fine with more RAM: nothing about THIS hardware is baked in.
        var c = HostCapacityService.Evaluate(16_000, 6, [Server("aubscraft", 3072)], Server("creative", 2048));
        Assert.That(c.Warnings, Is.Empty);
    }
}
