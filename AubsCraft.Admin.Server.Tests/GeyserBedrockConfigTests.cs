using AubsCraft.Admin.Server.Models;
using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// The proxy's Geyser listeners, written into the REAL config.yml Geyser 2.12.1 generates (TestData/geyser-2.12.1-config.yml,
/// from a test proxy's first start). Geyser 2.12 generates "transport: nethernet" (no RakNet listener at all), so a proxy set
/// up with Geyser's defaults turns away every Bedrock client still on RakNet; the panel writes "both" (TJ 2026-11-02).
/// Only the targeted lines change.
/// </summary>
public class GeyserBedrockConfigTests
{
    private static (ProxyDefinition Proxy, string Config) NewProxy()
    {
        var dir = TestUtil.NewTempDir();
        var plugin = Path.Combine(dir, "plugins", "Geyser-Velocity");
        Directory.CreateDirectory(plugin);
        var config = Path.Combine(plugin, "config.yml");
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "geyser-2.12.1-config.yml"), config);
        return (new ProxyDefinition { Path = dir }, config);
    }

    private static List<string> Changed(string[] before, string[] after) =>
        Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]).Select(i => after[i]).ToList();

    [Test]
    public void Defaults_AreBoth_RakNetOnTheBedrockPort_NetherNetOnItsOwn()
    {
        var (proxy, config) = NewProxy();
        var before = File.ReadAllLines(config);
        Assert.That(before, Does.Contain("  transport: nethernet"), "the fixture is Geyser 2.12's own default");

        ProxyService.WriteGeyserBedrock(proxy);

        var after = File.ReadAllLines(config);
        Assert.That(after, Has.Length.EqualTo(before.Length));
        Assert.That(Changed(before, after), Is.EqualTo(new[] { "  webrtc-port: 19133", "  transport: both" }),
            "port 19132 is already right; the advertised addresses are left to Geyser when none are set");
    }

    [Test]
    public void OtherPorts_AndAdvertisedAddresses_AreWritten()
    {
        var (proxy, config) = NewProxy();
        proxy.BedrockPort = 19232;
        proxy.BedrockWebRtcPort = 19233;
        proxy.BedrockAdvertiseAddresses = ["mc.spawndev.com", "192.168.1.142"];
        var before = File.ReadAllLines(config);

        ProxyService.WriteGeyserBedrock(proxy);

        Assert.That(Changed(before, File.ReadAllLines(config)), Is.EqualTo(new[]
        {
            "  port: 19232",
            "  webrtc-port: 19233",
            "  transport: both",
            "    advertise-addresses: ['mc.spawndev.com', '192.168.1.142']",
        }));
    }

    [Test]
    public void RakNetOnly_ClearsTheNetherNetPort()
    {
        var (proxy, config) = NewProxy();
        proxy.BedrockTransport = "raknet";
        ProxyService.WriteGeyserBedrock(proxy);
        var after = File.ReadAllLines(config);
        Assert.That(after, Does.Contain("  transport: raknet"));
        Assert.That(after, Does.Contain("  webrtc-port: 0"));
    }

    [Test]
    public void NonsenseIsRefused_BeforeTheFileIsTouched()
    {
        var (proxy, config) = NewProxy();
        var before = File.ReadAllText(config);
        proxy.BedrockTransport = "carrier-pigeon";
        Assert.Throws<InvalidOperationException>(() => ProxyService.WriteGeyserBedrock(proxy));
        proxy.BedrockTransport = "both";
        proxy.BedrockWebRtcPort = proxy.BedrockPort; // "both" needs two UDP ports
        Assert.Throws<InvalidOperationException>(() => ProxyService.WriteGeyserBedrock(proxy));
        Assert.That(File.ReadAllText(config), Is.EqualTo(before));
    }
}
