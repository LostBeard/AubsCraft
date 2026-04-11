using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;

namespace SpawnDev.Rcon;

/// <summary>
/// Source RCON protocol client. Works with Minecraft, Valve Source, and any server
/// implementing the Source RCON protocol.
/// https://developer.valvesoftware.com/wiki/Source_RCON_Protocol
/// </summary>
public class RconClient : IAsyncDisposable, IDisposable
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private int _nextRequestId = 1;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<RconPacket>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Server hostname or IP.
    /// </summary>
    public string Host { get; }

    /// <summary>
    /// Server RCON port (default 25575 for Minecraft).
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Whether the client is currently connected and authenticated.
    /// </summary>
    public bool IsConnected => _tcp?.Connected == true && _authenticated;
    private bool _authenticated;

    /// <summary>
    /// Timeout for individual command responses in milliseconds.
    /// </summary>
    public int ResponseTimeoutMs { get; set; } = 10000;

    /// <summary>
    /// Fired when a connection is lost.
    /// </summary>
    public event EventHandler? Disconnected;

    public RconClient(string host, int port = 25575)
    {
        Host = host;
        Port = port;
    }

    /// <summary>
    /// Connects and authenticates with the RCON server.
    /// </summary>
    /// <param name="password">RCON password from server.properties.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>True if connected and authenticated successfully.</returns>
    public async Task<bool> ConnectAsync(string password, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RconClient));

        _tcp = new TcpClient();
        await _tcp.ConnectAsync(Host, Port, cancellationToken);
        _stream = _tcp.GetStream();

        _readCts = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoopAsync(_readCts.Token), _readCts.Token);

        // Authenticate
        var authId = Interlocked.Increment(ref _nextRequestId);
        var authPacket = new RconPacket
        {
            RequestId = authId,
            Type = RconPacketType.Auth,
            Body = password,
        };

        var tcs = new TaskCompletionSource<RconPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[authId] = tcs;

        await SendPacketAsync(authPacket, cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ResponseTimeoutMs);

        try
        {
            var response = await tcs.Task.WaitAsync(timeoutCts.Token);
            _authenticated = response.RequestId != -1;
            return _authenticated;
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(authId, out _);
            await DisconnectAsync();
            return false;
        }
    }

    /// <summary>
    /// Sends a command to the server and returns the response string.
    /// </summary>
    /// <param name="command">The command to execute (e.g., "list", "whitelist add PlayerName").</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>The server's response text.</returns>
    public async Task<string> SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (!IsConnected) throw new InvalidOperationException("Not connected");

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var packet = new RconPacket
        {
            RequestId = requestId,
            Type = RconPacketType.Command,
            Body = command,
        };

        var tcs = new TaskCompletionSource<RconPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        await SendPacketAsync(packet, cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ResponseTimeoutMs);

        try
        {
            var response = await tcs.Task.WaitAsync(timeoutCts.Token);
            return response.Body;
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(requestId, out _);
            throw new TimeoutException($"RCON command timed out: {command}");
        }
    }

    /// <summary>
    /// Disconnects from the server.
    /// </summary>
    public async Task DisconnectAsync()
    {
        _authenticated = false;

        if (_readCts != null)
        {
            await _readCts.CancelAsync();
            _readCts.Dispose();
            _readCts = null;
        }

        if (_readTask != null)
        {
            try { await _readTask; } catch { }
            _readTask = null;
        }

        _stream?.Dispose();
        _stream = null;
        _tcp?.Dispose();
        _tcp = null;

        // Fail all pending requests
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var tcs))
            {
                tcs.TrySetException(new InvalidOperationException("Disconnected"));
            }
        }

        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private async Task SendPacketAsync(RconPacket packet, CancellationToken cancellationToken)
    {
        if (_stream == null) throw new InvalidOperationException("Not connected");

        var bytes = packet.ToBytes();

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[4];

        try
        {
            while (!cancellationToken.IsCancellationRequested && _stream != null)
            {
                // Read length prefix
                if (!await ReadExactAsync(_stream, lengthBuffer, 4, cancellationToken))
                    break;

                var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
                if (length < 10 || length > 4110) // Min: id+type+2 nulls, Max: 4096 body + 10 overhead
                    break;

                // Read payload
                var payload = new byte[length];
                if (!await ReadExactAsync(_stream, payload, length, cancellationToken))
                    break;

                var packet = RconPacket.FromPayload(payload);

                // Auth responses come back as type Response (0) but with the auth request's ID
                // Auth failure sends request ID = -1
                if (_pending.TryRemove(packet.RequestId, out var tcs))
                {
                    tcs.TrySetResult(packet);
                }
                else if (packet.RequestId == -1)
                {
                    // Auth failure - find any pending auth request
                    foreach (var kvp in _pending)
                    {
                        if (_pending.TryRemove(kvp.Key, out var authTcs))
                        {
                            authTcs.TrySetResult(packet);
                            break;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }

        if (!cancellationToken.IsCancellationRequested)
        {
            _ = Task.Run(DisconnectAsync);
        }
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken);
            if (read == 0) return false; // Connection closed
            offset += read;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DisconnectAsync();
        _writeLock.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisconnectAsync().GetAwaiter().GetResult();
        _writeLock.Dispose();
    }
}
