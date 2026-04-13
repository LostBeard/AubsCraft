using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using System.Numerics;

namespace AubsCraft.Admin.Services;

/// <summary>
/// Streams world chunks from the server via binary WebSocket.
/// Data stays in JS land (ArrayBuffer -> IndexedDB) and only crosses to .NET for rendering.
///
/// Protocol:
/// - Client sends text JSON: {"x":228,"z":-243} to update camera position
/// - Server sends binary frames: [cx][cz][palette][heights][blockIds][seabedHeights][seabedBlockIds]
/// - Server sorts chunks by distance to camera, closest first
/// </summary>
public sealed class ChunkStreamService : IDisposable
{
    private readonly BlazorJSRuntime _js;
    private readonly WorldCacheService _cache;
    private WebSocket? _ws;
    private bool _disposed;

    /// <summary>Fires when a heightmap chunk is received and cached. Provides (cx, cz, ArrayBuffer) for rendering.
    /// ArrayBuffer stays in JS - caller must dispose when done.</summary>
    public event Action<int, int, ArrayBuffer>? OnChunkReceived;

    /// <summary>Number of chunks received so far.</summary>
    public int ReceivedCount { get; private set; }

    /// <summary>True when the WebSocket is connected and streaming.</summary>
    public bool IsConnected => _ws?.ReadyState == 1;

    public ChunkStreamService(BlazorJSRuntime js, WorldCacheService cache)
    {
        _js = js;
        _cache = cache;
    }

    /// <summary>
    /// Connect to the binary WebSocket endpoint and start receiving chunks.
    /// Camera position determines chunk priority order.
    /// </summary>
    public void Connect(Vector3 cameraPosition)
    {
        if (_ws != null) return;

        // Build WebSocket URL relative to current page
        var location = _js.Get<Location>("location");
        var protocol = location.Protocol == "https:" ? "wss:" : "ws:";
        var host = location.Host;
        location.Dispose();

        var url = $"{protocol}//{host}/api/world/ws";
        _ws = new WebSocket(url);
        _ws.BinaryType = "arraybuffer";

        _ws.OnOpen += OnOpen;
        _ws.OnMessage += OnMessage;
        _ws.OnClose += OnClose;
        _ws.OnError += OnError;

        // Send initial camera position after connection opens
        _pendingCameraX = cameraPosition.X;
        _pendingCameraZ = cameraPosition.Z;
    }

    private float _pendingCameraX, _pendingCameraZ;
    private float _lastSentX = float.NaN, _lastSentZ = float.NaN;

    /// <summary>
    /// Update the camera position. Server re-sorts remaining chunks by distance.
    /// Only sends if position changed significantly (>32 blocks / 2 chunks).
    /// </summary>
    public void UpdateCameraPosition(Vector3 position)
    {
        var dx = position.X - _lastSentX;
        var dz = position.Z - _lastSentZ;
        if (float.IsNaN(_lastSentX) || dx * dx + dz * dz > 32 * 32)
        {
            _pendingCameraX = position.X;
            _pendingCameraZ = position.Z;
            SendCameraPosition();
        }
    }

    private void SendCameraPosition()
    {
        if (_ws?.ReadyState != 1) return;
        _lastSentX = _pendingCameraX;
        _lastSentZ = _pendingCameraZ;
        _ws.Send($"{{\"x\":{_pendingCameraX:F0},\"z\":{_pendingCameraZ:F0}}}");
    }

    private void OnOpen(Event e)
    {
        e.Dispose();
        Console.WriteLine("[ChunkStream] WebSocket connected");
        SendCameraPosition();
    }

    private void OnMessage(MessageEvent msg)
    {
        try
        {
            // Keep the ArrayBuffer in JS - do NOT call ReadBytes()
            var arrayBuffer = msg.GetData<ArrayBuffer>();
            int byteLength = (int)arrayBuffer.ByteLength;

            if (byteLength >= 8)
            {
                // Read only cx/cz (8 bytes) via a tiny Int32Array view - stays in JS
                using var headerView = new Int32Array(arrayBuffer, 0, 2);
                int cx = headerView[0];
                int cz = headerView[1];

                // Cache the ArrayBuffer directly to OPFS (JS -> JS, zero .NET)
                _cache.CacheHeightmap(cx, cz, arrayBuffer);

                ReceivedCount++;
                if (ReceivedCount <= 3 || ReceivedCount % 100 == 0)
                    Console.WriteLine($"[ChunkStream] Chunk ({cx},{cz}) received, total: {ReceivedCount}, frame size: {byteLength}");

                // Pass ArrayBuffer to renderer - data stays in JS
                OnChunkReceived?.Invoke(cx, cz, arrayBuffer);
            }
            else
            {
                Console.WriteLine($"[ChunkStream] Received frame too small: {byteLength} bytes");
                arrayBuffer.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChunkStream] Message error: {ex.Message}");
        }
        finally
        {
            msg.Dispose();
        }
    }

    private void OnClose(CloseEvent e)
    {
        Console.WriteLine($"[ChunkStream] WebSocket closed: code={e.Code} reason={e.Reason} clean={e.WasClean}");
        e.Dispose();
    }

    private void OnError(Event e)
    {
        Console.WriteLine("[ChunkStream] WebSocket error");
        e.Dispose();
    }

    /// <summary>
    /// Parse a binary heightmap frame into rendering data.
    /// Called from .NET when the renderer needs the data - this is the ONE boundary crossing.
    /// </summary>
    public static HeightmapData? ParseFrame(byte[] frame)
    {
        if (frame.Length < 12) return null;

        int offset = 0;
        int cx = BitConverter.ToInt32(frame, offset); offset += 4;
        int cz = BitConverter.ToInt32(frame, offset); offset += 4;
        int paletteCount = BitConverter.ToInt32(frame, offset); offset += 4;

        var palette = new List<string>(paletteCount);
        for (int i = 0; i < paletteCount; i++)
        {
            if (offset + 4 > frame.Length) return null;
            int strLen = BitConverter.ToInt32(frame, offset); offset += 4;
            if (offset + strLen > frame.Length) return null;
            palette.Add(System.Text.Encoding.UTF8.GetString(frame, offset, strLen));
            offset += strLen;
        }

        // 256 int32 heights + 256 ushort blockIds + 256 int32 seabedHeights + 256 ushort seabedBlockIds
        int expectedRemaining = 256 * 4 + 256 * 2 + 256 * 4 + 256 * 2;
        if (offset + expectedRemaining > frame.Length) return null;

        var heights = new int[256];
        Buffer.BlockCopy(frame, offset, heights, 0, 256 * 4); offset += 256 * 4;

        var blockIds = new ushort[256];
        Buffer.BlockCopy(frame, offset, blockIds, 0, 256 * 2); offset += 256 * 2;

        var seabedHeights = new int[256];
        Buffer.BlockCopy(frame, offset, seabedHeights, 0, 256 * 4); offset += 256 * 4;

        var seabedBlockIds = new ushort[256];
        Buffer.BlockCopy(frame, offset, seabedBlockIds, 0, 256 * 2);

        return new HeightmapData(cx, cz, palette, heights, blockIds, seabedHeights, seabedBlockIds);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ws != null)
        {
            _ws.OnOpen -= OnOpen;
            _ws.OnMessage -= OnMessage;
            _ws.OnClose -= OnClose;
            _ws.OnError -= OnError;

            if (_ws.ReadyState == 1)
                _ws.Close();

            _ws.Dispose();
            _ws = null;
        }
    }

    /// <summary>
    /// Parse a binary frame header from a JS ArrayBuffer. Only palette strings cross to .NET.
    /// Binary data (heights, blockIds, seabed) stays in JS for CopyFromJS.
    /// </summary>
    public static (int cx, int cz, List<string> palette, int binaryDataOffset)? ParseFrameHeader(ArrayBuffer frameBuffer)
    {
        int byteLength = (int)frameBuffer.ByteLength;
        if (byteLength < 12) return null;

        // Read header (cx, cz, paletteCount) - 12 bytes via Int32Array
        using var headerView = new Int32Array(frameBuffer, 0, 3);
        int cx = headerView[0];
        int cz = headerView[1];
        int paletteCount = headerView[2];
        int offset = 12;

        // Read the palette section into .NET (variable-length strings).
        // Binary data at the end is exactly 3072 bytes (256*4 + 256*2 + 256*4 + 256*2).
        // Palette section = everything between the header (12 bytes) and the binary data.
        int binaryDataSize = 256 * 4 + 256 * 2 + 256 * 4 + 256 * 2; // 3072 bytes
        int paletteSize = byteLength - offset - binaryDataSize;
        if (paletteSize <= 0) return null;
        using var paletteBytesView = new Uint8Array(frameBuffer, offset, paletteSize);
        var paletteBytes = paletteBytesView.ReadBytes();

        var palette = new List<string>(paletteCount);
        int localOff = 0;
        for (int i = 0; i < paletteCount; i++)
        {
            if (localOff + 4 > paletteBytes.Length) return null;
            int strLen = BitConverter.ToInt32(paletteBytes, localOff); localOff += 4;
            if (localOff + strLen > paletteBytes.Length) return null;
            palette.Add(System.Text.Encoding.UTF8.GetString(paletteBytes, localOff, strLen));
            localOff += strLen;
        }

        int binaryDataOffset = offset + localOff;
        int expectedRemaining = 256 * 4 + 256 * 2 + 256 * 4 + 256 * 2;
        if (binaryDataOffset + expectedRemaining > byteLength) return null;

        if (palette.Count > 0 && palette.Count <= 3)
            Console.WriteLine($"[ParseHeader] cx={cx} cz={cz} palette[0]={palette[0]} count={paletteCount} paletteSize={paletteSize} binaryOffset={binaryDataOffset} totalLen={byteLength}");

        return (cx, cz, palette, binaryDataOffset);
    }

    /// <summary>
    /// Parse a binary frame from .NET byte[] (legacy/fallback path).
    /// </summary>
    public static (int cx, int cz, List<string> palette, int binaryDataOffset)? ParseFrameHeader(byte[] frame)
    {
        if (frame.Length < 12) return null;

        int offset = 0;
        int cx = BitConverter.ToInt32(frame, offset); offset += 4;
        int cz = BitConverter.ToInt32(frame, offset); offset += 4;
        int paletteCount = BitConverter.ToInt32(frame, offset); offset += 4;

        var palette = new List<string>(paletteCount);
        for (int i = 0; i < paletteCount; i++)
        {
            if (offset + 4 > frame.Length) return null;
            int strLen = BitConverter.ToInt32(frame, offset); offset += 4;
            if (offset + strLen > frame.Length) return null;
            palette.Add(System.Text.Encoding.UTF8.GetString(frame, offset, strLen));
            offset += strLen;
        }

        int expectedRemaining = 256 * 4 + 256 * 2 + 256 * 4 + 256 * 2;
        if (offset + expectedRemaining > frame.Length) return null;

        return (cx, cz, palette, offset);
    }
}

/// <summary>Parsed heightmap data ready for rendering.</summary>
public record HeightmapData(
    int X, int Z,
    List<string> Palette,
    int[] Heights,
    ushort[] BlockIds,
    int[] SeabedHeights,
    ushort[] SeabedBlockIds);
