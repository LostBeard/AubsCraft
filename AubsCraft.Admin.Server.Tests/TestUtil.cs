using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>Small helpers: a temp folder per test, in-memory configuration, and a null logger factory.</summary>
public static class TestUtil
{
    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aubscraft-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static IConfiguration Config(params (string key, string? value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.key, v.value)))
            .Build();

    public static ILoggerFactory Loggers => NullLoggerFactory.Instance;

    public static ILogger<T> Log<T>() => NullLogger<T>.Instance;

    /// <summary>Polls until the condition holds (or fails the test with the message after the timeout).</summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(250);
        }
        Assert.Fail($"Timed out after {timeout.TotalSeconds:F0}s waiting for: {because}");
    }
}
