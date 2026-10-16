using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AubsCraft.Live.Model.Editing;

namespace AubsCraft.Admin.Server.Services;

/// <summary>Makes the plugin's binary command of an edit (the layout is in <c>Edits.java</c>), after checking everything about it: an admin's request is never trusted to be well formed or reasonable.</summary>
public static class EditCodec
{
    /// <summary>Most blocks one edit may change; more is refused (a bigger job is several edits).</summary>
    public const long MaxBlocks = 2_000_000;
    public const int MaxOps = 64, MaxPalette = 4096, MinY = -64, MaxY = 319, MaxCoordinate = 30_000_000;

    // "minecraft:oak_stairs[facing=north,half=bottom]": a name and optional properties of plain characters - nothing that could be anything else
    private static readonly Regex BlockPattern = new(@"^[a-z0-9_.\-]+:[a-z0-9_./\-]+(\[[a-z0-9_]+=[a-z0-9_]+(,[a-z0-9_]+=[a-z0-9_]+)*\])?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsBlock(string? s) => s != null && s.Length <= 300 && BlockPattern.IsMatch(s);

    /// <summary>The command, or throws <see cref="ArgumentException"/> saying what is wrong with the request.</summary>
    public static byte[] Build(string commandId, string editor, EditRequest request, out long blocks)
    {
        if (request.Ops.Count == 0) throw new ArgumentException("An edit with nothing in it");
        if (request.Ops.Count > MaxOps) throw new ArgumentException($"At most {MaxOps} steps in an edit");
        var palette = new List<string>();
        var index = new Dictionary<string, int>();
        int Palette(string? block)
        {
            if (!IsBlock(block)) throw new ArgumentException("Not a block: " + (block is { Length: > 60 } ? block[..60] + "..." : block));
            if (index.TryGetValue(block!, out var i)) return i;
            if (palette.Count >= MaxPalette) throw new ArgumentException("Too many different blocks");
            index[block!] = palette.Count;
            palette.Add(block!);
            return palette.Count - 1;
        }
        static void Check(int[]? p, string what)
        {
            if (p is not { Length: 3 }) throw new ArgumentException(what + " needs three numbers");
            if (p[1] < MinY || p[1] > MaxY) throw new ArgumentException($"{what}: y must be between {MinY} and {MaxY}");
            if (Math.Abs(p[0]) > MaxCoordinate || Math.Abs(p[2]) > MaxCoordinate) throw new ArgumentException(what + " is out of the world");
        }

        var ops = new MemoryStream();
        long total = 0;
        foreach (var op in request.Ops)
        {
            switch (op.Op)
            {
                case "set":
                    Check(op.At, "set");
                    ops.WriteByte(1);
                    I32(ops, op.At![0]); I32(ops, op.At[1]); I32(ops, op.At[2]);
                    U16(ops, Palette(op.Block));
                    total++;
                    break;
                case "fill":
                    Check(op.From, "fill from"); Check(op.To, "fill to");
                    long volume = (long)(Math.Abs(op.To![0] - op.From![0]) + 1) * (Math.Abs(op.To[1] - op.From[1]) + 1) * (Math.Abs(op.To[2] - op.From[2]) + 1);
                    total += volume;
                    if (total > MaxBlocks) throw new ArgumentException($"More than {MaxBlocks:N0} blocks in one edit");
                    ops.WriteByte(2);
                    foreach (var v in op.From) I32(ops, v);
                    foreach (var v in op.To) I32(ops, v);
                    U16(ops, Palette(op.Block));
                    I32(ops, op.Only == null ? -1 : Palette(op.Only));
                    ops.WriteByte(op.Hollow ? (byte)1 : (byte)0);
                    break;
                case "blocks":
                    Check(op.At, "blocks");
                    if (op.Palette is not { Length: > 0 } || op.Data == null) throw new ArgumentException("blocks needs a palette and data");
                    byte[] raw;
                    try { raw = Convert.FromBase64String(op.Data); }
                    catch (FormatException) { throw new ArgumentException("blocks data is not base64"); }
                    if (raw.Length % 8 != 0) throw new ArgumentException("blocks data is not whole records");
                    total += raw.Length / 8;
                    if (total > MaxBlocks) throw new ArgumentException($"More than {MaxBlocks:N0} blocks in one edit");
                    var map = op.Palette.Select(Palette).ToArray();
                    // Every record's palette index becomes the edit's; every position must be inside the world
                    for (int i = 0; i < raw.Length; i += 8)
                    {
                        int dy = (short)(raw[i + 2] | (raw[i + 3] << 8));
                        int y = op.At![1] + dy;
                        if (y < MinY || y > MaxY) throw new ArgumentException("blocks: a block is outside the world's height");
                        int p = raw[i + 6] | (raw[i + 7] << 8);
                        if (p >= map.Length) throw new ArgumentException("blocks: a palette index is out of range");
                        raw[i + 6] = (byte)map[p]; raw[i + 7] = (byte)(map[p] >> 8);
                    }
                    ops.WriteByte(3);
                    I32(ops, op.At![0]); I32(ops, op.At[1]); I32(ops, op.At[2]);
                    I32(ops, raw.Length);
                    ops.Write(raw);
                    break;
                default:
                    throw new ArgumentException("Unknown step: " + op.Op);
            }
        }
        if (total > MaxBlocks) throw new ArgumentException($"More than {MaxBlocks:N0} blocks in one edit");
        blocks = total;

        var body = new MemoryStream();
        body.WriteByte(1);
        Str(body, commandId);
        Str(body, editor);
        Str(body, Truncate(request.Label ?? "", 120));
        U16(body, palette.Count);
        foreach (var p in palette) Str(body, p);
        U16(body, request.Ops.Count);
        ops.Position = 0;
        ops.CopyTo(body);
        return body.ToArray();
    }

    public static byte[] BuildUndo(string commandId, string editId)
    {
        var body = new MemoryStream();
        body.WriteByte(2);
        Str(body, commandId);
        Str(body, editId);
        return body.ToArray();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
    private static void U16(Stream s, int v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); }
    private static void I32(Stream s, int v) { Span<byte> b = stackalloc byte[4]; BitConverter.TryWriteBytes(b, v); s.Write(b); }
    private static void Str(Stream s, string v) { var b = Encoding.UTF8.GetBytes(v); U16(s, b.Length); s.Write(b); }
}

/// <summary>
/// The panel's side of the edit channel: the commands admins send wait here until the plugin of that server collects them (it asks, and the answer comes when there is something, or after a wait with
/// nothing), and each command's caller waits for the plugin's result. One queue a server. The plugin makes every connection; the panel never connects in.
/// </summary>
public sealed class EditChannelService
{
    /// <summary>How long a long poll waits with nothing to say.</summary>
    public static readonly TimeSpan PollWait = TimeSpan.FromSeconds(25);
    /// <summary>The plugin counts as connected when it asked this recently.</summary>
    public static readonly TimeSpan ConnectedWithin = TimeSpan.FromSeconds(60);

    private sealed class ServerQueue
    {
        public readonly object Lock = new();
        public readonly Queue<byte[]> Commands = new();
        public readonly SemaphoreSlim Signal = new(0);
        public readonly ConcurrentDictionary<string, TaskCompletionSource<EditResult>> Pending = new();
        public DateTime LastPoll = DateTime.MinValue;
    }

    private readonly ConcurrentDictionary<string, ServerQueue> _queues = new(StringComparer.OrdinalIgnoreCase);
    private ServerQueue QueueOf(string serverId) => _queues.GetOrAdd(serverId, _ => new ServerQueue());

    /// <summary>Whether the server's plugin is there to take commands.</summary>
    public bool PluginConnected(string serverId) => DateTime.UtcNow - QueueOf(serverId).LastPoll < ConnectedWithin;

    /// <summary>Send a command and wait for the plugin's result (a timeout is an error result, and the command is dropped if it was never collected).</summary>
    public async Task<EditResult> SubmitAsync(string serverId, string commandId, byte[] payload, TimeSpan timeout, CancellationToken ct = default)
    {
        var q = QueueOf(serverId);
        if (!PluginConnected(serverId)) return new EditResult(false, null, 0, 0, false, "The server's AubsCraft Live plugin is not connected (it needs the latest plugin, installed and the server restarted)");
        var done = new TaskCompletionSource<EditResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        q.Pending[commandId] = done;
        lock (q.Lock) q.Commands.Enqueue(payload);
        q.Signal.Release();
        try { return await done.Task.WaitAsync(timeout, ct); }
        catch (TimeoutException) { return new EditResult(false, null, 0, 0, false, "The server did not answer in time (a big edit may still be going on)"); }
        finally { q.Pending.TryRemove(commandId, out _); }
    }

    /// <summary>The plugin's long poll: the waiting commands framed (u16 count, then i32 length and bytes each), or null after <paramref name="wait"/> with none.</summary>
    public async Task<byte[]?> PollAsync(string serverId, TimeSpan wait, CancellationToken ct)
    {
        var q = QueueOf(serverId);
        q.LastPoll = DateTime.UtcNow;
        try
        {
            if (!await q.Signal.WaitAsync(wait, ct)) { q.LastPoll = DateTime.UtcNow; return null; }
        }
        catch (OperationCanceledException) { return null; }
        List<byte[]> taken = new();
        lock (q.Lock)
            while (q.Commands.Count > 0 && taken.Count < 64) taken.Add(q.Commands.Dequeue());
        // One signal was spent for the first; the others' signals are spent now, so none is left over for a command that is gone
        for (int i = 1; i < taken.Count; i++) q.Signal.Wait(0);
        q.LastPoll = DateTime.UtcNow;
        var framed = new MemoryStream();
        framed.WriteByte((byte)taken.Count); framed.WriteByte((byte)(taken.Count >> 8));
        foreach (var c in taken)
        {
            framed.Write(BitConverter.GetBytes(c.Length));
            framed.Write(c);
        }
        return framed.ToArray();
    }

    /// <summary>A result from the plugin (its JSON). False when it is not a result of ours.</summary>
    public bool Complete(string serverId, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetString() ?? "";
            var ok = root.GetProperty("ok").GetBoolean();
            var result = new EditResult(ok,
                root.TryGetProperty("edit", out var e) ? e.GetString() : null,
                root.TryGetProperty("placed", out var p) ? p.GetInt64() : 0,
                root.TryGetProperty("skipped", out var s) ? s.GetInt64() : 0,
                root.TryGetProperty("applied", out var a) && a.GetBoolean(),
                root.TryGetProperty("error", out var err) ? err.GetString() : null);
            return QueueOf(serverId).Pending.TryGetValue(id, out var tcs) && tcs.TrySetResult(result);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }
}
