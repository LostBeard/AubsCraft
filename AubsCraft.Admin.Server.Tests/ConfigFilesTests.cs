using AubsCraft.Admin.Server.Services;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>
/// In-place config edits on the REAL files Paper 1.21.5 generates (TestData, copied from a test server):
/// only the targeted value changes, every other line stays byte-identical.
/// </summary>
public class ConfigFilesTests
{
    private static string Copy(string name)
    {
        var dst = Path.Combine(TestUtil.NewTempDir(), name);
        File.Copy(Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", name), dst);
        return dst;
    }

    [Test]
    public void PaperGlobal_VelocityBlock_IsSetAndNothingElseChanges()
    {
        var path = Copy("paper-global.1.21.5.yml");
        var before = File.ReadAllLines(path);

        ConfigFiles.SetYamlScalar(path, ["proxies", "velocity", "enabled"], "true");
        ConfigFiles.SetYamlScalar(path, ["proxies", "velocity", "secret"], ConfigFiles.YamlString("s3cr'et"));

        var after = File.ReadAllLines(path);
        Assert.That(after, Has.Length.EqualTo(before.Length));
        var changed = Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]).ToList();
        Assert.That(changed.Select(i => after[i]), Is.EqualTo(new[] { "    enabled: true", "    secret: 's3cr''et'" }));
        // The OTHER "online-mode" (proxies.bungee-cord) and velocity.online-mode are untouched.
        Assert.That(after, Does.Contain("    online-mode: true"));
    }

    [Test]
    public void YamlPath_MustExist()
    {
        var path = Copy("paper-global.1.21.5.yml");
        var ex = Assert.Throws<InvalidDataException>(() => ConfigFiles.SetYamlScalar(path, ["proxies", "waterfall", "enabled"], "true"));
        Assert.That(ex!.Message, Does.Contain("proxies.waterfall"));
    }

    [Test]
    public void YamlPath_DoesNotMatchAKeyOutsideItsParent()
    {
        // "enabled" exists in many blocks; under proxies.velocity it must be the velocity one, and a key that
        // only exists in a LATER top-level block must not be found under proxies.
        var path = Copy("paper-global.1.21.5.yml");
        Assert.Throws<InvalidDataException>(() => ConfigFiles.SetYamlScalar(path, ["proxies", "save-empty-scoreboard-teams"], "false"));
    }

    [Test]
    public void ServerProperties_SetsAndAppends_KeepingOtherLines()
    {
        var path = Copy("server.1.21.5.properties");
        var before = File.ReadAllLines(path);
        ConfigFiles.SetProperty(path, "server-port", "25566");
        ConfigFiles.SetProperty(path, "online-mode", "false");
        ConfigFiles.SetProperty(path, "brand-new-key", "x");

        var after = File.ReadAllLines(path);
        Assert.That(ConfigFiles.GetProperty(path, "server-port"), Is.EqualTo("25566"));
        Assert.That(ConfigFiles.GetProperty(path, "online-mode"), Is.EqualTo("false"));
        Assert.That(after[^1], Is.EqualTo("brand-new-key=x"));
        Assert.That(after, Has.Length.EqualTo(before.Length + 1));
        Assert.That(after.Count(l => l.StartsWith("server-port=")), Is.EqualTo(1));
    }
}
