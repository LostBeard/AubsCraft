using SpawnDev.SpawnJS.JSObjects;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// The viewer's end of the live feed: a WebSocket to <c>/api/live/ws?server=</c> that hands every frame (the plugin's binary frame, relayed as it came) to
/// <paramref name="onFrame"/>, and keeps trying when the connection drops (a server restart, a panel deploy, a proxy timeout) with a growing pause.
///
/// A frame is a few KB to ~100 KB, about five a second. It is structured entity data, not the world's bulk blocks, so it comes into .NET to be parsed
/// and interpolated (ReadBytes: the rule against pulling data from JS is about chunk data on the GPU path, and this is the one exception, called out in CLAUDE.md).
/// </summary>
public sealed class LiveFeedClient : IDisposable
{
    private readonly string _url;
    private readonly Action<byte[]> _onFrame;
    private readonly Action<string> _log;
    private WebSocket? _socket;
    private bool _disposed;
    private int _failures;
    private bool _everConnected;

    /// <summary>Frames received (all connections).</summary>
    public long Frames { get; private set; }
    public bool Connected => _socket?.ReadyState == 1;

    public LiveFeedClient(string url, Action<byte[]> onFrame, Action<string>? log = null)
    {
        _url = url;
        _onFrame = onFrame;
        _log = log ?? (_ => { });
    }

    public void Start() => Connect();

    private void Connect()
    {
        if (_disposed) return;
        try
        {
            var socket = new WebSocket(_url);
            socket.BinaryType = "arraybuffer";
            _socket = socket;
            socket.OnOpen += _ =>
            {
                if (_socket != socket) return;
                _failures = 0;
                if (!_everConnected) _log("Live feed connected");
                _everConnected = true;
            };
            socket.OnMessage += e =>
            {
                if (_socket != socket) return;
                try
                {
                    using var buffer = e.GetData<ArrayBuffer>();
                    if (buffer == null) return;
                    Frames++;
                    _onFrame(buffer.ReadBytes());
                }
                catch (Exception ex)
                {
                    _log("Live frame error: " + ex.Message);
                }
                finally { e.Dispose(); }
            };
            socket.OnClose += _ => OnLost(socket);
            socket.OnError += _ => { /* a close always follows */ };
        }
        catch (Exception ex)
        {
            _log("Live feed connect error: " + ex.Message);
            _ = RetryAsync();
        }
    }

    private void OnLost(WebSocket socket)
    {
        if (_disposed || _socket != socket) return;
        _socket = null;
        socket.Dispose();
        _ = RetryAsync();
    }

    private async Task RetryAsync()
    {
        // 1 s, 2 s, 4 s ... at most 20 s: a restart does not need hammering, and a viewer left open all night should not either
        int delayMs = (int)Math.Min(1000 * Math.Pow(2, Math.Min(_failures, 5)), 20000);
        _failures++;
        if (_failures == 3) _log("Live feed is not available (the server has no live plugin, or is restarting); trying again");
        await Task.Delay(delayMs);
        Connect();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var socket = _socket;
        _socket = null;
        if (socket != null)
        {
            try { socket.Close(1000, "viewer closed"); } catch { /* already closed */ }
            socket.Dispose();
        }
    }
}
