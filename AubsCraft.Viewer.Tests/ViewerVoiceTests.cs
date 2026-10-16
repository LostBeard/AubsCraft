using System.Net.WebSockets;
using AubsCraft.Live.Model.Voice;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// Voice in a real browser: the voice and the microphone are turned on (Chrome is given a fake microphone, a beeping tone), the panel gets Opus packets of it and hands them to "the plugin" (the test: a socket as the
/// plugin makes it); the test sends them back as a player speaking, and the browser decodes them and says who is heard. Real Opus both ways; only Simple Voice Chat itself is not here.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_SCENE_WORLD     the scene folder (default D:/users/tj/Projects/_scratch/scene-server)
/// </summary>
[NonParallelizable]
public class ViewerVoiceTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_SCENE_WORLD") ?? "D:/users/tj/Projects/_scratch/scene-server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "voice";
        _outDir = Path.Combine(ViewerShotTests.RepoRoot(), "screenshots", "viewer", tag);
        Directory.CreateDirectory(_outDir);
        _rig = await ViewerRig.StartAsync(publish!, world);
    }

    [OneTimeTearDown]
    public async Task StopRigAsync()
    {
        if (_rig != null) await _rig.DisposeAsync();
    }

    [Test]
    public async Task TheMicrophoneReachesThePlugin_AndAPlayersVoiceIsHeardInTheBrowser()
    {
        // The plugin's socket, as the real plugin opens it
        using var plugin = new ClientWebSocket();
        plugin.Options.SetRequestHeader("X-Live-Secret", ViewerRig.LiveSecret);
        await plugin.ConnectAsync(new Uri(_rig.BaseUrl.Replace("http://", "ws://") + "/api/live/voice/plugin/aubscraft"), CancellationToken.None);

        var received = new List<byte[]>();
        var listeners = new List<int>();
        using var stop = new CancellationTokenSource();
        var replay = Task.Run(async () =>
        {
            var buffer = new byte[8192];
            try
            {
                while (!stop.IsCancellationRequested && plugin.State == WebSocketState.Open)
                {
                    var r = await plugin.ReceiveAsync(buffer, stop.Token);
                    if (r.MessageType != WebSocketMessageType.Binary) continue;
                    var frame = buffer.AsSpan(0, r.Count).ToArray();
                    if (frame[0] == VoiceFrames.Listeners) { lock (listeners) listeners.Add(frame[1]); continue; }
                    if (frame[0] != VoiceFrames.Mic) continue;
                    lock (received) received.Add(frame);
                    // The microphone's packet, back as a player speaking: [1][id u16][x y z][flags][opus] -> [5][name][x y z][flags][opus]
                    var opus = frame.AsSpan(3 + 12 + 1).ToArray();
                    await plugin.SendAsync(VoiceFrames.WritePlayerVoice("Steve", 12f, -59f, 14f, 0, opus), WebSocketMessageType.Binary, true, stop.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
        });

        var page = await _rig.Context.NewPageAsync();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var errors = new List<string>();
        page.Console += (_, m) => { log.Enqueue(m.Text); if (m.Type == "error" && !m.Text.Contains("whitelisted")) errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        try
        {
            await page.GotoAsync($"{_rig.BaseUrl}/map?hud=0&time=6000&dd=6&light=0&cam=-8.5,-58.0,8.5,-90,0", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 8, quietSeconds: 3, maxSeconds: 60);

            await page.GetByRole(AriaRole.Button, new() { Name = "Voice: Off" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Voice: On" }).WaitForAsync(new() { Timeout = 15_000 });
            for (int i = 0; i < 25; i++) { lock (listeners) if (listeners.Contains(1)) break; await Task.Delay(200); }
            lock (listeners) Assert.That(listeners, Does.Contain(1), "the plugin was told one viewer listens");

            await page.GetByRole(AriaRole.Button, new() { Name = "Mic: Off" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Mic: On" }).WaitForAsync(new() { Timeout = 15_000 });
            for (int i = 0; i < 40 && received.Count < 25; i++) await Task.Delay(100);
            int count; lock (received) count = received.Count;
            TestContext.Progress.WriteLine($"[voice] the plugin received {count} packets of the microphone");
            Assert.That(count, Is.GreaterThanOrEqualTo(25), "a second of microphone reached the plugin:\n" + string.Join("\n", log.ToArray().Where(l => l.Contains("Voice")).TakeLast(8)));
            byte[] first; lock (received) first = received[0];
            Assert.That(first.Length, Is.InRange(3 + 12 + 1 + 3, 3 + 12 + 1 + 600), "an Opus packet of a few dozen bytes");
            Assert.That(BitConverter.ToSingle(first, 3), Is.EqualTo(-8.5f).Within(2f), "from where the camera is");

            // What came back as Steve is decoded and heard: the page says so
            await page.GetByText("Speaking: Steve").WaitForAsync(new() { Timeout = 15_000 });
            Assert.That(log.ToArray().Any(l => l.Contains("[Voice] hearing Steve")), Is.True, "a voice was made for Steve");

            // The microphone off: the packets stop
            await page.GetByRole(AriaRole.Button, new() { Name = "Mic: On" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Mic: Off" }).WaitForAsync(new() { Timeout = 10_000 });
            await Task.Delay(800);
            int atOff; lock (received) atOff = received.Count;
            await Task.Delay(1000);
            lock (received) Assert.That(received.Count - atOff, Is.LessThanOrEqualTo(3), "nothing more is sent");
            Assert.That(errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", errors));
        }
        finally
        {
            stop.Cancel();
            try { await plugin.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
            try { await replay; } catch { }
            try { File.WriteAllLines(Path.Combine(_outDir, "console-voice.log"), log.ToArray()); } catch { }
            await page.CloseAsync();
        }
    }
}
