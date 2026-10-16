using System.Buffers.Binary;
using AubsCraft.Admin.Server.Services;
using AubsCraft.Live.Model.Voice;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>Voice between browsers, the panel and the server's plugin: the frames, and who hears whom.</summary>
public class VoiceTests
{
    private static readonly byte[] Opus = [0xFC, 0xFF, 0xFE, 1, 2, 3, 4, 5];

    // ---- the frames ----

    [Test]
    public void AMicFrame_CarriesWhereTheSpeakerIsAndTheOpusPacket()
    {
        var frame = VoiceFrames.WriteMic(10.5f, -60f, -3.25f, VoiceFrames.Whispering, Opus);
        Assert.That(frame[0], Is.EqualTo(VoiceFrames.Mic));
        Assert.That(VoiceFrames.TryReadMic(frame, out var x, out var y, out var z, out var flags, out var opus), Is.True);
        Assert.That((x, y, z, flags), Is.EqualTo((10.5f, -60f, -3.25f, VoiceFrames.Whispering)));
        Assert.That(opus.ToArray(), Is.EqualTo(Opus));
    }

    [Test]
    public void ThingsThatAreNotMicFrames_AreRefused()
    {
        Assert.That(VoiceFrames.TryReadMic([], out _, out _, out _, out _, out _), Is.False);
        Assert.That(VoiceFrames.TryReadMic([VoiceFrames.Mic, 1, 2, 3], out _, out _, out _, out _, out _), Is.False, "too short");
        var wrongType = VoiceFrames.WriteMic(0, 0, 0, 0, Opus); wrongType[0] = VoiceFrames.Voice;
        Assert.That(VoiceFrames.TryReadMic(wrongType, out _, out _, out _, out _, out _), Is.False);
        var nan = VoiceFrames.WriteMic(float.NaN, 0, 0, 0, Opus);
        Assert.That(VoiceFrames.TryReadMic(nan, out _, out _, out _, out _, out _), Is.False, "a place that is not a place");
        var huge = new byte[VoiceFrames.MaxFrameBytes + 1]; huge[0] = VoiceFrames.Mic;
        Assert.That(VoiceFrames.TryReadMic(huge, out _, out _, out _, out _, out _), Is.False);
    }

    [Test]
    public void TheMicFrameForThePluginHasTheSpeakersIdPutIn()
    {
        var frame = VoiceFrames.WriteMic(1, 2, 3, 0, Opus);
        var plugin = VoiceFrames.ToPlugin(513, frame);
        Assert.That(plugin[0], Is.EqualTo(VoiceFrames.Mic));
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(plugin.AsSpan(1)), Is.EqualTo(513));
        Assert.That(BinaryPrimitives.ReadSingleLittleEndian(plugin.AsSpan(3)), Is.EqualTo(1f));
        Assert.That(plugin.AsSpan(plugin.Length - Opus.Length).ToArray(), Is.EqualTo(Opus));
        Assert.That(plugin.Length, Is.EqualTo(frame.Length + 2));
    }

    [Test]
    public void SomeoneSpeaking_IsNamedAndPlaced()
    {
        var voice = VoiceFrames.WriteVoice("Aubs", 1.5f, 2.5f, 3.5f, 0, Opus);
        var speech = VoiceFrames.ReadSpeech(voice)!;
        Assert.That((speech.Name, speech.X, speech.Y, speech.Z, speech.Flags), Is.EqualTo(("Aubs", 1.5f, 2.5f, 3.5f, (byte)0)));
        Assert.That(speech.Opus, Is.EqualTo(Opus));
        Assert.That(VoiceFrames.ReadSpeech(VoiceFrames.WritePlayerVoice("Dad", 0, 0, 0, 0, Opus))!.Name, Is.EqualTo("Dad"));
        Assert.That(VoiceFrames.ReadSpeech(VoiceFrames.WriteVoice("ünï cödé ✓", 0, 0, 0, 0, Opus))!.Name, Is.EqualTo("ünï cödé ✓"));
        Assert.That(VoiceFrames.ReadSpeech([VoiceFrames.Voice, 200, 1, 2]), Is.Null, "a name longer than the frame");
        Assert.That(VoiceFrames.ReadSpeech([VoiceFrames.Mic, 0]), Is.Null);
    }

    [Test]
    public void TheShortMessages()
    {
        Assert.That(VoiceFrames.WriteListeners(3), Is.EqualTo(new byte[] { VoiceFrames.Listeners, 3 }));
        Assert.That(VoiceFrames.WriteListeners(900), Is.EqualTo(new byte[] { VoiceFrames.Listeners, 255 }));
        Assert.That(VoiceFrames.WriteLeft(258), Is.EqualTo(new byte[] { VoiceFrames.Left, 2, 1 }));
    }

    // ---- the hub ----

    private sealed class Inbox
    {
        public readonly List<byte[]> Frames = new();
        public ValueTask Take(byte[] f) { lock (Frames) Frames.Add(f); return ValueTask.CompletedTask; }
        public int Count { get { lock (Frames) return Frames.Count; } }
        public byte[][] All() { lock (Frames) return Frames.ToArray(); }
    }

    [Test]
    public async Task ABrowsersVoice_GoesToThePlugin_AndToTheOtherBrowsers_NotBackToItself()
    {
        var hub = new VoiceHubService();
        Inbox plugin = new(), aubs = new(), dad = new();
        await hub.SetPluginAsync("s", plugin.Take);
        var a = await hub.AddBrowserAsync("s", "Aubs", aubs.Take);
        var d = await hub.AddBrowserAsync("s", "Dad", dad.Take);

        await hub.FromBrowserAsync(a, VoiceFrames.WriteMic(5, 6, 7, 0, Opus));

        Assert.That(aubs.All().Any(f => f[0] == VoiceFrames.Voice), Is.False, "you do not hear yourself");
        var heard = VoiceFrames.ReadSpeech(dad.All().Single())!;
        Assert.That((heard.Name, heard.X, heard.Y, heard.Z), Is.EqualTo(("Aubs", 5f, 6f, 7f)));
        Assert.That(heard.Opus, Is.EqualTo(Opus));
        var toPlugin = plugin.All().Last(f => f[0] == VoiceFrames.Mic);
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(toPlugin.AsSpan(1)), Is.EqualTo(a.Id), "the plugin is told whose voice it is");
        Assert.That(a.Id, Is.Not.EqualTo(d.Id));
    }

    [Test]
    public async Task APlayersVoice_GoesToEveryBrowserOfThatServerOnly()
    {
        var hub = new VoiceHubService();
        Inbox one = new(), two = new(), other = new();
        await hub.AddBrowserAsync("s", "Aubs", one.Take);
        await hub.AddBrowserAsync("s", "Dad", two.Take);
        await hub.AddBrowserAsync("elsewhere", "Nikki", other.Take);

        await hub.FromPluginAsync("s", VoiceFrames.WritePlayerVoice("Steve", 1, 2, 3, 0, Opus));

        foreach (var inbox in new[] { one, two })
        {
            var speech = VoiceFrames.ReadSpeech(inbox.All().Single())!;
            Assert.That(speech.Name, Is.EqualTo("Steve"));
        }
        Assert.That(other.Count, Is.Zero, "another server's browsers do not hear it");
    }

    [Test]
    public async Task ThePluginIsToldHowManyListen_AndWhoLeft()
    {
        var hub = new VoiceHubService();
        Inbox plugin = new();
        var a = await hub.AddBrowserAsync("s", "Aubs", new Inbox().Take);   // before the plugin is there
        await hub.SetPluginAsync("s", plugin.Take);
        Assert.That(plugin.All().Single(), Is.EqualTo(new byte[] { VoiceFrames.Listeners, 1 }), "told at once when it connects");
        var b = await hub.AddBrowserAsync("s", "Dad", new Inbox().Take);
        Assert.That(plugin.All().Last(), Is.EqualTo(new byte[] { VoiceFrames.Listeners, 2 }));
        await hub.RemoveBrowserAsync(a);
        var frames = plugin.All();
        Assert.That(frames[^2], Is.EqualTo(VoiceFrames.WriteLeft(a.Id)));
        Assert.That(frames[^1], Is.EqualTo(new byte[] { VoiceFrames.Listeners, 1 }));
        Assert.That(hub.ListenerCount("s"), Is.EqualTo(1));
        await hub.SetPluginAsync("s", null);
        Assert.That(hub.PluginConnected("s"), Is.False);
    }

    [Test]
    public async Task AFloodOrNonsense_IsDropped()
    {
        var hub = new VoiceHubService();
        Inbox plugin = new(), listener = new();
        await hub.SetPluginAsync("s", plugin.Take);
        var a = await hub.AddBrowserAsync("s", "Aubs", new Inbox().Take);
        await hub.AddBrowserAsync("s", "Dad", listener.Take);
        int before = plugin.Count;
        for (int i = 0; i < 250; i++) await hub.FromBrowserAsync(a, VoiceFrames.WriteMic(0, 0, 0, 0, Opus));
        Assert.That(plugin.Count - before, Is.EqualTo(VoiceHubService.MaxFramesPerSecond), "a second's worth and no more");
        Assert.That(listener.Count, Is.EqualTo(VoiceHubService.MaxFramesPerSecond));

        var b = await hub.AddBrowserAsync("s", "Nikki", new Inbox().Take);
        int now = plugin.Count;
        await hub.FromBrowserAsync(b, [9, 9, 9]);
        await hub.FromBrowserAsync(b, VoiceFrames.WriteMic(0, 0, 0, 0, []));   // no audio in it
        await hub.FromBrowserAsync(b, VoiceFrames.WriteVoice("Fake", 0, 0, 0, 0, Opus));   // a browser may not pose as a player
        Assert.That(plugin.Count, Is.EqualTo(now), "nothing reached the plugin");
    }

    [Test]
    public async Task ASocketThatBreaks_DoesNotStopTheOthersHearing()
    {
        var hub = new VoiceHubService();
        Inbox good = new();
        await hub.AddBrowserAsync("s", "Gone", _ => throw new System.Net.WebSockets.WebSocketException("closed"));
        await hub.AddBrowserAsync("s", "Here", good.Take);
        await hub.FromPluginAsync("s", VoiceFrames.WritePlayerVoice("Steve", 0, 0, 0, 0, Opus));
        Assert.That(good.Count, Is.EqualTo(1));
    }
}
