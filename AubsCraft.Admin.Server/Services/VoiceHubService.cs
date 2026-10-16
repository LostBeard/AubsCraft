using System.Collections.Concurrent;
using AubsCraft.Live.Model.Voice;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Voice between the viewers' browsers and the server's players. A browser sends its microphone (Opus packets with the place it speaks from); the panel gives each speaker an id and hands the packets to
/// the server's plugin, which plays them to the players near that place in Simple Voice Chat, and to the OTHER browsers of the server, so people in the viewer hear one another too. What players say in
/// the game comes from the plugin (only while somebody listens) and goes to every browser of the server. One socket each way per party; the panel keeps no audio.
/// </summary>
public sealed class VoiceHubService
{
    /// <summary>A browser may send this many frames a second (50 is a normal rate: 20 ms each); more are dropped.</summary>
    public const int MaxFramesPerSecond = 100;

    public sealed class Speaker
    {
        public ushort Id { get; init; }
        public string Name { get; init; } = "";
        public string ServerId { get; init; } = "";
        internal Func<byte[], ValueTask> Send { get; init; } = _ => ValueTask.CompletedTask;
        internal long WindowStart;
        internal int InWindow;
    }

    private sealed class Server
    {
        public readonly List<Speaker> Browsers = new();
        public Func<byte[], ValueTask>? Plugin;
        public readonly object Lock = new();
    }

    private readonly ConcurrentDictionary<string, Server> _servers = new(StringComparer.OrdinalIgnoreCase);
    private int _nextId;
    private Server Of(string serverId) => _servers.GetOrAdd(serverId, _ => new Server());

    /// <summary>A browser is in. Its frames go through <see cref="FromBrowserAsync"/>; what it hears is given to <paramref name="send"/>.</summary>
    public async Task<Speaker> AddBrowserAsync(string serverId, string name, Func<byte[], ValueTask> send)
    {
        var s = Of(serverId);
        var speaker = new Speaker { Id = (ushort)(Interlocked.Increment(ref _nextId) & 0xFFFF), Name = name, ServerId = serverId, Send = send };
        int count;
        lock (s.Lock) { s.Browsers.Add(speaker); count = s.Browsers.Count; }
        await TellPluginAsync(s, VoiceFrames.WriteListeners(count));
        return speaker;
    }

    public async Task RemoveBrowserAsync(Speaker speaker)
    {
        var s = Of(speaker.ServerId);
        int count;
        lock (s.Lock) { s.Browsers.Remove(speaker); count = s.Browsers.Count; }
        await TellPluginAsync(s, VoiceFrames.WriteLeft(speaker.Id));
        await TellPluginAsync(s, VoiceFrames.WriteListeners(count));
    }

    /// <summary>The server's plugin is connected (its socket's sender): it is told how many listen, at once.</summary>
    public async Task SetPluginAsync(string serverId, Func<byte[], ValueTask>? send)
    {
        var s = Of(serverId);
        int count;
        lock (s.Lock) { s.Plugin = send; count = s.Browsers.Count; }
        if (send != null) await TellPluginAsync(s, VoiceFrames.WriteListeners(count));
    }

    public bool PluginConnected(string serverId) => _servers.TryGetValue(serverId, out var s) && s.Plugin != null;

    public int ListenerCount(string serverId)
    {
        if (!_servers.TryGetValue(serverId, out var s)) return 0;
        lock (s.Lock) return s.Browsers.Count;
    }

    /// <summary>A frame from a browser: a microphone packet. Anything else, or too many, is dropped.</summary>
    public async Task FromBrowserAsync(Speaker speaker, byte[] frame)
    {
        if (!Prepare(speaker, frame, out var toPlugin, out var voice)) return;
        long now = Environment.TickCount64;
        if (now - speaker.WindowStart >= 1000) { speaker.WindowStart = now; speaker.InWindow = 0; }
        if (++speaker.InWindow > MaxFramesPerSecond) return;

        var s = Of(speaker.ServerId);
        Func<byte[], ValueTask>? plugin;
        Speaker[] others;
        lock (s.Lock) { plugin = s.Plugin; others = s.Browsers.Where(b => b != speaker).ToArray(); }
        if (plugin != null) await Safe(plugin, toPlugin);
        foreach (var other in others) await Safe(other.Send, voice);
    }

    // The two messages one mic frame makes (spans cannot cross an await, so it is done here)
    private static bool Prepare(Speaker speaker, byte[] frame, out byte[] toPlugin, out byte[] voice)
    {
        toPlugin = voice = [];
        if (!VoiceFrames.TryReadMic(frame, out var x, out var y, out var z, out var flags, out var opus) || opus.Length == 0) return false;
        toPlugin = VoiceFrames.ToPlugin(speaker.Id, frame);
        voice = VoiceFrames.WriteVoice(speaker.Name, x, y, z, flags, opus);
        return true;
    }
    /// <summary>A frame from the server's plugin: a player speaking in the game. It goes to every browser.</summary>
    public async Task FromPluginAsync(string serverId, byte[] frame)
    {
        if (ReadPlayerVoice(frame) is not { } speech) return;
        var s = Of(serverId);
        Speaker[] browsers;
        lock (s.Lock) browsers = s.Browsers.ToArray();
        if (browsers.Length == 0) return;
        var voice = VoiceFrames.WriteVoice(speech.Name, speech.X, speech.Y, speech.Z, speech.Flags, speech.Opus);
        foreach (var b in browsers) await Safe(b.Send, voice);
    }

    private static VoiceFrames.Speech? ReadPlayerVoice(byte[] frame) => frame.Length > 0 && frame[0] == VoiceFrames.PlayerVoice ? VoiceFrames.ReadSpeech(frame) : null;

    private static async Task TellPluginAsync(Server s, byte[] frame)
    {
        Func<byte[], ValueTask>? plugin;
        lock (s.Lock) plugin = s.Plugin;
        if (plugin != null) await Safe(plugin, frame);
    }

    // A socket that has gone away must not stop the others hearing
    private static async Task Safe(Func<byte[], ValueTask> send, byte[] frame)
    {
        try { await send(frame); }
        catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
    }
}
