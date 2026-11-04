using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using AubsCraft.Live.Model;

namespace AubsCraft.Admin.Server.Services;

/// <summary>What the panel knows about one server's live feed.</summary>
public sealed record LiveFeedStatus(bool Live, long Frames, int Entities, int Players, double AgeMs, long LastFullTime);

/// <summary>
/// The live activity of the servers: frames from the AubsCraft Live plugin arrive at <see cref="Ingest"/>, are checked (the shared secret, then
/// a full parse: a frame that is not exactly ours is refused, never relayed), kept as the server's latest, and fanned out to every viewer
/// subscribed to that server. A viewer that cannot keep up loses the OLDEST frames (a bounded channel): live data is only worth its newest.
/// </summary>
public sealed class LiveFeedService
{
    /// <summary>A frame bigger than this is refused (3000 entities are about 100 KB).</summary>
    public const int MaxFrameBytes = 4 * 1024 * 1024;
    /// <summary>The feed counts as live when its last frame is newer than this.</summary>
    public static readonly TimeSpan LiveWithin = TimeSpan.FromSeconds(5);

    private readonly byte[] _secret;
    private readonly ConcurrentDictionary<string, Feed> _feeds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<LiveFeedService> _logger;

    private sealed class Feed
    {
        public byte[]? Latest;
        public DateTime LastAt = DateTime.MinValue;
        public long Frames;
        public int Entities, Players;
        public long FullTime;
        public readonly List<Channel<byte[]>> Subscribers = new();
        public readonly List<Channel<byte[]>> Notes = new();
        public readonly object Lock = new();
    }

    /// <param name="configuration">Live:Secret, else Live:SecretPath (default live-secret.txt in the working folder), created with a random secret on first use.</param>
    public LiveFeedService(IConfiguration configuration, ILogger<LiveFeedService> logger)
    {
        _logger = logger;
        var secret = configuration.GetValue<string>("Live:Secret");
        if (string.IsNullOrWhiteSpace(secret))
        {
            var path = configuration.GetValue<string>("Live:SecretPath") ?? "live-secret.txt";
            if (File.Exists(path)) secret = File.ReadAllText(path).Trim();
            if (string.IsNullOrWhiteSpace(secret))
            {
                secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                File.WriteAllText(path, secret);
                _logger.LogInformation("Live feed: created a shared secret in {Path}", path);
            }
        }
        Secret = secret!;
        _secret = Encoding.UTF8.GetBytes(Secret);
    }

    /// <summary>The secret the plugin sends; written into each server's plugin config when the plugin is installed.</summary>
    public string Secret { get; }

    /// <summary>Whether the presented secret is the right one, without leaking how much of it matched.</summary>
    public bool VerifySecret(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        var bytes = Encoding.UTF8.GetBytes(presented);
        return bytes.Length == _secret.Length && CryptographicOperations.FixedTimeEquals(bytes, _secret);
    }

    /// <summary>Take a frame from a server. Returns null when it was accepted, else why not (the caller answers 400).</summary>
    public string? Ingest(string serverId, byte[] frame)
    {
        if (frame.Length == 0) return "empty frame";
        if (frame.Length > MaxFrameBytes) return "frame too large";
        LiveFrame parsed;
        try { parsed = LiveFrameCodec.Parse(frame); }
        catch (InvalidDataException ex) { return ex.Message; }

        var feed = _feeds.GetOrAdd(serverId, _ => new Feed());
        lock (feed.Lock)
        {
            feed.Latest = frame;
            feed.LastAt = DateTime.UtcNow;
            feed.Frames++;
            feed.Entities = parsed.Worlds.Sum(w => w.Entities.Count);
            feed.Players = parsed.Worlds.Sum(w => w.Players.Count);
            feed.FullTime = parsed.FullTime;
            foreach (var s in feed.Subscribers) s.Writer.TryWrite(frame);
        }
        return null;
    }

    /// <summary>
    /// A message to every viewer of a server that is not a frame (chunks changed): it is not kept as the latest, so a viewer that opens later does not get it. Unlike a frame it is not worth only its
    /// newest: a viewer that lost one would show that chunk stale until it left, so these wait in a queue of their own and go first.
    /// </summary>
    public void Broadcast(string serverId, byte[] message)
    {
        if (!_feeds.TryGetValue(serverId, out var feed)) return;
        lock (feed.Lock)
            foreach (var s in feed.Notes) s.Writer.TryWrite(message);
    }

    /// <summary>
    /// Subscribe to a server's frames: the latest one first (a viewer that opens mid-session sees the world at once), then every new one, with the notes that chunks changed. Dispose
    /// the result to unsubscribe.
    /// </summary>
    public Subscription Subscribe(string serverId)
    {
        var feed = _feeds.GetOrAdd(serverId, _ => new Feed());
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var notes = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(MaxWaitingNotes) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (feed.Lock)
        {
            if (feed.Latest != null) channel.Writer.TryWrite(feed.Latest);
            feed.Subscribers.Add(channel);
            feed.Notes.Add(notes);
        }
        return new Subscription(channel.Reader, notes.Reader, () =>
        {
            lock (feed.Lock)
            {
                feed.Subscribers.Remove(channel);
                feed.Notes.Remove(notes);
            }
            channel.Writer.TryComplete();
            notes.Writer.TryComplete();
        });
    }

    /// <summary>Notes kept for one viewer that is not reading (each names up to 256 chunks). A viewer this far behind has lost its connection in all but name.</summary>
    public const int MaxWaitingNotes = 4096;

    public sealed class Subscription : IDisposable
    {
        private readonly Action _dispose;
        public ChannelReader<byte[]> Frames { get; }
        public ChannelReader<byte[]> Notes { get; }
        internal Subscription(ChannelReader<byte[]> frames, ChannelReader<byte[]> notes, Action dispose) { Frames = frames; Notes = notes; _dispose = dispose; }
        public void Dispose() => _dispose();

        /// <summary>What to send the viewer next, for as long as there is any: the notes that chunks changed first, then the frames. Ends when unsubscribed.</summary>
        public async IAsyncEnumerable<byte[]> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancel = default)
        {
            while (true)
            {
                if (Notes.TryRead(out var note)) { yield return note; continue; }
                if (Frames.TryRead(out var frame)) { yield return frame; continue; }
                var noteWait = Notes.WaitToReadAsync(cancel).AsTask();
                var frameWait = Frames.WaitToReadAsync(cancel).AsTask();
                await Task.WhenAny(noteWait, frameWait);
                if (noteWait.IsCanceled || frameWait.IsCanceled) throw new OperationCanceledException(cancel);
                if (noteWait is { IsCompletedSuccessfully: true, Result: false } && frameWait is { IsCompletedSuccessfully: true, Result: false }) yield break;
            }
        }
    }

    public LiveFeedStatus StatusOf(string serverId)
    {
        if (!_feeds.TryGetValue(serverId, out var feed)) return new LiveFeedStatus(false, 0, 0, 0, double.PositiveInfinity, 0);
        lock (feed.Lock)
        {
            var age = DateTime.UtcNow - feed.LastAt;
            return new LiveFeedStatus(age < LiveWithin, feed.Frames, feed.Entities, feed.Players, age.TotalMilliseconds, feed.FullTime);
        }
    }

    /// <summary>The number of viewers currently subscribed to a server.</summary>
    public int SubscriberCount(string serverId)
    {
        if (!_feeds.TryGetValue(serverId, out var feed)) return 0;
        lock (feed.Lock) return feed.Subscribers.Count;
    }
}
