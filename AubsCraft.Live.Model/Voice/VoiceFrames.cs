using System.Buffers.Binary;
using System.Text;

namespace AubsCraft.Live.Model.Voice;

/// <summary>
/// The wire format of voice, between a browser, the panel and the server's plugin. A message is a binary frame: a type byte, then:
/// <list type="bullet">
/// <item><b>1 Mic</b>, browser to panel: <c>f32 x, f32 y, f32 z</c> (where the speaker is in the world), a flags byte (1 whispering), then one Opus packet (48 kHz mono, 20 ms).</item>
/// <item><b>1 Mic</b>, panel to plugin: the same with a <c>u16 speaker id</c> after the type byte, so the plugin keeps one voice channel for each web speaker.</item>
/// <item><b>2 Voice</b>, panel to browser (someone is speaking): <c>u8 name length, name (UTF-8), f32 x, f32 y, f32 z, flags, Opus</c> - from a player in the game (plugin), or another browser.</item>
/// <item><b>3 Listeners</b>, panel to plugin: <c>u8</c> how many browsers are listening (the plugin sends players' voices to the panel only while someone is).</item>
/// <item><b>4 Left</b>, panel to plugin: <c>u16 speaker id</c> of a browser that has gone: its voice channel is closed.</item>
/// <item><b>5 PlayerVoice</b>, plugin to panel: <c>u8 name length, name, f32 x, f32 y, f32 z, flags, Opus</c> (the same as <b>2</b>; the panel relays it).</item>
/// </list>
/// All numbers little-endian.
/// </summary>
public static class VoiceFrames
{
    public const byte Mic = 1, Voice = 2, Listeners = 3, Left = 4, PlayerVoice = 5;
    public const int MaxFrameBytes = 4096;
    public const byte Whispering = 1;

    public sealed record Speech(string Name, float X, float Y, float Z, byte Flags, byte[] Opus);

    /// <summary>A browser's mic frame (what <see cref="Mic"/> says first).</summary>
    public static byte[] WriteMic(float x, float y, float z, byte flags, ReadOnlySpan<byte> opus)
    {
        var bytes = new byte[1 + 12 + 1 + opus.Length];
        bytes[0] = Mic;
        Floats(bytes.AsSpan(1), x, y, z);
        bytes[13] = flags;
        opus.CopyTo(bytes.AsSpan(14));
        return bytes;
    }

    /// <summary>Read a browser's mic frame; false when it is not one.</summary>
    public static bool TryReadMic(ReadOnlySpan<byte> frame, out float x, out float y, out float z, out byte flags, out ReadOnlySpan<byte> opus)
    {
        x = y = z = 0; flags = 0; opus = default;
        if (frame.Length < 15 || frame[0] != Mic || frame.Length > MaxFrameBytes) return false;
        x = BinaryPrimitives.ReadSingleLittleEndian(frame[1..]);
        y = BinaryPrimitives.ReadSingleLittleEndian(frame[5..]);
        z = BinaryPrimitives.ReadSingleLittleEndian(frame[9..]);
        if (!Finite(x) || !Finite(y) || !Finite(z)) return false;
        flags = frame[13];
        opus = frame[14..];
        return true;
    }

    /// <summary>The mic frame for the plugin: the speaker's id put in.</summary>
    public static byte[] ToPlugin(ushort speakerId, ReadOnlySpan<byte> micFrame)
    {
        var bytes = new byte[micFrame.Length + 2];
        bytes[0] = Mic;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(1), speakerId);
        micFrame[1..].CopyTo(bytes.AsSpan(3));
        return bytes;
    }

    /// <summary>What a browser hears: someone speaking, from where.</summary>
    public static byte[] WriteVoice(string name, float x, float y, float z, byte flags, ReadOnlySpan<byte> opus) => WriteSpeech(Voice, name, x, y, z, flags, opus);

    /// <summary>What the plugin reports of a player speaking.</summary>
    public static byte[] WritePlayerVoice(string name, float x, float y, float z, byte flags, ReadOnlySpan<byte> opus) => WriteSpeech(PlayerVoice, name, x, y, z, flags, opus);

    private static byte[] WriteSpeech(byte type, string name, float x, float y, float z, byte flags, ReadOnlySpan<byte> opus)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name.Length > 40 ? name[..40] : name);
        var bytes = new byte[2 + nameBytes.Length + 12 + 1 + opus.Length];
        bytes[0] = type;
        bytes[1] = (byte)nameBytes.Length;
        nameBytes.CopyTo(bytes.AsSpan(2));
        int at = 2 + nameBytes.Length;
        Floats(bytes.AsSpan(at), x, y, z);
        bytes[at + 12] = flags;
        opus.CopyTo(bytes.AsSpan(at + 13));
        return bytes;
    }

    /// <summary>Read a <see cref="Voice"/> or <see cref="PlayerVoice"/> frame.</summary>
    public static Speech? ReadSpeech(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2 + 13 || frame[0] is not (Voice or PlayerVoice) || frame.Length > MaxFrameBytes) return null;
        int nameLength = frame[1];
        if (frame.Length < 2 + nameLength + 13) return null;
        var name = Encoding.UTF8.GetString(frame.Slice(2, nameLength));
        int at = 2 + nameLength;
        float x = BinaryPrimitives.ReadSingleLittleEndian(frame[at..]), y = BinaryPrimitives.ReadSingleLittleEndian(frame[(at + 4)..]), z = BinaryPrimitives.ReadSingleLittleEndian(frame[(at + 8)..]);
        if (!Finite(x) || !Finite(y) || !Finite(z)) return null;
        return new Speech(name, x, y, z, frame[at + 12], frame[(at + 13)..].ToArray());
    }

    public static byte[] WriteListeners(int count) => [Listeners, (byte)Math.Min(count, 255)];

    public static byte[] WriteLeft(ushort speakerId)
    {
        var bytes = new byte[3];
        bytes[0] = Left;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(1), speakerId);
        return bytes;
    }

    private static void Floats(Span<byte> to, float x, float y, float z)
    {
        BinaryPrimitives.WriteSingleLittleEndian(to, x);
        BinaryPrimitives.WriteSingleLittleEndian(to[4..], y);
        BinaryPrimitives.WriteSingleLittleEndian(to[8..], z);
    }

    private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
}
