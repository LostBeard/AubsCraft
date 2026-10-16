using System.Text;

namespace AubsCraft.Viewer.Tests;

/// <summary>A world of one chunk (-1, 0): a flat floor of grass at y -61, with what the edits have put on it. It speaks the plugin's language: commands in, chunk updates and results out.</summary>
internal sealed class FakePlugin
{
    private readonly ViewerRig _rig;
    private readonly Dictionary<(int x, int y, int z), string> _blocks = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    public readonly List<(string Kind, int[] At, string Block)> Received = new();
    public readonly List<string> Edits = new();
    private readonly Dictionary<string, List<((int, int, int) At, string Old)>> _journals = new();

    public FakePlugin(ViewerRig rig) => _rig = rig;

    public void Start() => _loop = Task.Run(Loop);

    /// <summary>Something already built when the page loads.</summary>
    public void Place(int x, int y, int z, string block) => _blocks[(x, y, z)] = block;

    public async Task StopAsync()
    {
        _stop.Cancel();
        try { if (_loop != null) await _loop; } catch (OperationCanceledException) { }
    }

    private static byte[] ChunkBody(IReadOnlyDictionary<(int x, int y, int z), string> blocks)
    {
        const int top = 20;
        var palette = new List<string> { "minecraft:air", "minecraft:stone", "minecraft:grass_block[snowy=false]" };
        foreach (var b in blocks.Values) if (!palette.Contains(b)) palette.Add(b);
        using var ms = new MemoryStream();
        void U8(int v) => ms.WriteByte((byte)v);
        void U16(int v) { ms.WriteByte((byte)v); ms.WriteByte((byte)(v >> 8)); }
        U8(1); U8(0); U16(top); U16(palette.Count);
        foreach (var p in palette) { var bytes = Encoding.UTF8.GetBytes(p); U16(bytes.Length); ms.Write(bytes); }
        for (int y = 0; y < top; y++)
            for (int z = 0; z < 16; z++)
                for (int x = 0; x < 16; x++)
                {
                    int wy = y - 64, wx = x - 16;
                    int id = wy < -61 ? 1 : wy == -61 ? 2 : 0;
                    if (blocks.TryGetValue((wx, wy, z), out var over)) id = palette.IndexOf(over);
                    U16(id);
                }
        for (int i = 0; i < 256 * top; i++) U8(0xF0);
        for (int i = 0; i < 256; i++) U16(4);
        U16(0); U16(0);
        return ms.ToArray();
    }

    public Task PostWorldAsync() => _rig.PostLiveChunkAsync(-1, 0, ChunkBody(_blocks));

    private async Task Loop()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_rig.BaseUrl), Timeout = TimeSpan.FromSeconds(60) };
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "/api/live/commands/aubscraft");
                req.Headers.Add("X-Live-Secret", ViewerRig.LiveSecret);
                using var resp = await http.SendAsync(req, _stop.Token);
                if (resp.StatusCode != System.Net.HttpStatusCode.OK) { await Task.Delay(200, _stop.Token); continue; }
                var bytes = await resp.Content.ReadAsByteArrayAsync(_stop.Token);
                int count = BitConverter.ToUInt16(bytes, 0), at = 2;
                for (int i = 0; i < count; i++)
                {
                    int len = BitConverter.ToInt32(bytes, at); at += 4;
                    await HandleAsync(http, bytes.AsSpan(at, len).ToArray());
                    at += len;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { await Task.Delay(200); }
        }
    }

    private async Task HandleAsync(HttpClient http, byte[] cmd)
    {
        int p = 0;
        int U8() => cmd[p++];
        int U16() { int v = cmd[p] | (cmd[p + 1] << 8); p += 2; return v; }
        int I32() { int v = BitConverter.ToInt32(cmd, p); p += 4; return v; }
        string Str() { int n = U16(); var s = Encoding.UTF8.GetString(cmd, p, n); p += n; return s; }
        int kind = U8();
        string id = Str();
        string result;
        if (kind == 1)
        {
            Str(); Str();   // editor, label
            var palette = new string[U16()];
            for (int i = 0; i < palette.Length; i++) palette[i] = Str();
            int ops = U16();
            var journal = new List<((int, int, int), string)>();
            int placed = 0, outside = 0;
            for (int i = 0; i < ops; i++)
            {
                int type = U8();
                void Put(int x, int y, int z, string block)
                {
                    // The fake world is the one chunk it posts: an edit anywhere else would be counted and never drawn
                    if (x < -16 || x > -1 || z < 0 || z > 15) { outside++; return; }
                    journal.Add(((x, y, z), _blocks.TryGetValue((x, y, z), out var old) ? old : (y == -61 ? "minecraft:grass_block[snowy=false]" : y < -61 ? "minecraft:stone" : "minecraft:air")));
                    if (block == "minecraft:air" && y > -61) _blocks.Remove((x, y, z)); else _blocks[(x, y, z)] = block;
                    placed++;
                }
                if (type == 1)
                {
                    int x = I32(), y = I32(), z = I32();
                    var block = palette[U16()];
                    Received.Add(("set", [x, y, z], block));
                    Put(x, y, z, block);
                }
                else if (type == 2)
                {
                    int x1 = I32(), y1 = I32(), z1 = I32(), x2 = I32(), y2 = I32(), z2 = I32();
                    var block = palette[U16()];
                    int only = I32();
                    bool hollow = U8() != 0;
                    Received.Add((hollow ? "fill-hollow" : "fill", [x1, y1, z1, x2, y2, z2], block));
                    for (int x = Math.Min(x1, x2); x <= Math.Max(x1, x2); x++)
                        for (int y = Math.Min(y1, y2); y <= Math.Max(y1, y2); y++)
                            for (int z = Math.Min(z1, z2); z <= Math.Max(z1, z2); z++) Put(x, y, z, block);
                }
                else if (type == 3)
                {
                    int ax = I32(), ay = I32(), az = I32(), length = I32();
                    for (int r = 0; r < length; r += 8)
                    {
                        int dx = (short)(cmd[p + r] | (cmd[p + r + 1] << 8)), dy = (short)(cmd[p + r + 2] | (cmd[p + r + 3] << 8)), dz = (short)(cmd[p + r + 4] | (cmd[p + r + 5] << 8));
                        var block = palette[cmd[p + r + 6] | (cmd[p + r + 7] << 8)];
                        Received.Add(("blocks", [ax + dx, ay + dy, az + dz], block));
                        Put(ax + dx, ay + dy, az + dz, block);
                    }
                    p += length;
                }
                else Assert.Fail("an op the fake plugin does not know: " + type);
            }
            _journals[id] = journal;
            Edits.Add(id);
            result = placed == 0 && outside > 0
                ? $"{{\"id\":\"{id}\",\"ok\":false,\"error\":\"The test world is one floor of 16 x 16 blocks (x -16 to -1, z 0 to 15): build there\"}}"
                : $"{{\"id\":\"{id}\",\"ok\":true,\"edit\":\"{id}\",\"placed\":{placed},\"skipped\":{outside},\"applied\":true}}";
        }
        else
        {
            var edit = Str();
            Received.Add(("undo", [], edit));
            var journal = _journals[edit];
            foreach (var (pos, old) in journal) { if (old == "minecraft:air") _blocks.Remove(pos); else _blocks[pos] = old; }
            result = $"{{\"id\":\"{id}\",\"ok\":true,\"edit\":\"{edit}\",\"placed\":{journal.Count},\"skipped\":0,\"applied\":false}}";
        }
        await PostWorldAsync();
        using var post = new HttpRequestMessage(HttpMethod.Post, "/api/live/commands/aubscraft/result") { Content = new StringContent(result, Encoding.UTF8, "application/json") };
        post.Headers.Add("X-Live-Secret", ViewerRig.LiveSecret);
        (await http.SendAsync(post)).EnsureSuccessStatusCode();
    }
}
