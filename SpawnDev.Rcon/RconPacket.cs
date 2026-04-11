using System.Buffers.Binary;
using System.Text;

namespace SpawnDev.Rcon;

/// <summary>
/// Source RCON packet types.
/// https://developer.valvesoftware.com/wiki/Source_RCON_Protocol
/// </summary>
public enum RconPacketType : int
{
    /// <summary>
    /// Response to a command or auth response.
    /// </summary>
    Response = 0,

    /// <summary>
    /// Command packet sent to the server.
    /// </summary>
    Command = 2,

    /// <summary>
    /// Authentication request sent to the server.
    /// </summary>
    Auth = 3,
}

/// <summary>
/// Represents a single Source RCON protocol packet.
/// Packet format: [Length:int32] [RequestId:int32] [Type:int32] [Body:null-terminated string] [Padding:null byte]
/// Length = RequestId + Type + Body + 2 null bytes = body.Length + 10
/// </summary>
public class RconPacket
{
    /// <summary>
    /// Request ID for correlating responses to requests. -1 indicates auth failure.
    /// </summary>
    public int RequestId { get; set; }

    /// <summary>
    /// Packet type (Auth, Command, or Response).
    /// </summary>
    public RconPacketType Type { get; set; }

    /// <summary>
    /// Packet body content (command string or response text).
    /// </summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// Serializes this packet to bytes for sending over TCP.
    /// </summary>
    public byte[] ToBytes()
    {
        var bodyBytes = Encoding.UTF8.GetBytes(Body);
        var length = 4 + 4 + bodyBytes.Length + 1 + 1; // id + type + body + null + null
        var buffer = new byte[4 + length]; // length prefix + payload

        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0), length);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), RequestId);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), (int)Type);
        bodyBytes.CopyTo(buffer, 12);
        // Two null bytes at end are already zero from array init

        return buffer;
    }

    /// <summary>
    /// Parses a packet from a buffer that already has the length prefix stripped.
    /// Buffer should contain: [RequestId:4] [Type:4] [Body:variable] [null] [null]
    /// </summary>
    public static RconPacket FromPayload(ReadOnlySpan<byte> payload)
    {
        var requestId = BinaryPrimitives.ReadInt32LittleEndian(payload);
        var type = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        // Body runs from offset 8 to end minus 2 null bytes
        var bodyLength = payload.Length - 8 - 2;
        var body = bodyLength > 0
            ? Encoding.UTF8.GetString(payload.Slice(8, bodyLength))
            : string.Empty;

        return new RconPacket
        {
            RequestId = requestId,
            Type = (RconPacketType)type,
            Body = body,
        };
    }
}
