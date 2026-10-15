using System.Buffers.Binary;
using System.Text;

namespace AubsCraft.Live.Model;

/// <summary>
/// Reads and writes the live frame (little-endian; the layout is in LivePlugin/FORMAT.md and LivePlugin/src/.../Frames.java).
/// <see cref="Parse"/> trusts nothing: every length is checked against what is left, so a truncated or hostile frame is an
/// <see cref="InvalidDataException"/>, never an out-of-range read or a huge allocation.
/// </summary>
public static class LiveFrameCodec
{
    public const int FormatVersion = 1;
    private static readonly byte[] Magic = "ACL1"u8.ToArray();

    // ---- parse ---------------------------------------------------------------------------------------------------------------

    public static LiveFrame Parse(ReadOnlySpan<byte> data)
    {
        var r = new Reader(data);
        if (data.Length < 4 || !data[..4].SequenceEqual(Magic)) throw new InvalidDataException("Not a live frame (bad magic)");
        r.Skip(4);
        int version = r.U8();
        if (version != FormatVersion) throw new InvalidDataException($"Live frame format {version}, this reader understands {FormatVersion}");
        long fullTime = r.I64();
        long dayTime = r.I64();
        int weather = r.U8();

        int typeCount = r.U16();
        var types = new string[typeCount];
        for (int i = 0; i < typeCount; i++) types[i] = r.Str8();

        int worldCount = r.U8();
        var worlds = new List<LiveWorld>(worldCount);
        for (int w = 0; w < worldCount; w++)
        {
            string name = r.Str8();
            int entityCount = r.U16();
            // 31 bytes an entity at least: a count that cannot fit what is left is a lie, caught before anything is allocated
            r.Need(entityCount * EntityBytes);
            var raw = new RawEntity[entityCount];
            for (int i = 0; i < entityCount; i++)
            {
                int id = r.I32();
                int typeIndex = r.U16();
                if (typeIndex >= typeCount) throw new InvalidDataException($"Entity {id} names type {typeIndex}, the frame has {typeCount}");
                raw[i] = new RawEntity(id, types[typeIndex], r.F32(), r.F32(), r.F32(), r.I8(), r.I8(), r.I8(), r.U16(), r.I16(), r.I16(), r.I16());
            }

            var names = ReadIdStrings(ref r);
            var variants = ReadIdStrings(ref r);
            int playerCount = r.U16();
            var players = new List<LivePlayer>(playerCount);
            for (int i = 0; i < playerCount; i++)
            {
                int entityId = r.I32();
                long most = r.I64(), least = r.I64();
                string playerName = r.Str8();
                string skin = r.Str16();
                bool slim = r.U8() != 0;
                int gameMode = r.U8();
                string held = r.Str8();
                float health = r.U8() / 255f;
                players.Add(new LivePlayer(entityId, FormatUuid(most, least), playerName, skin, slim, gameMode, held, health));
            }

            var entities = new List<LiveEntity>(entityCount);
            foreach (var e in raw)
                entities.Add(new LiveEntity(e.Id, e.Type, e.X, e.Y, e.Z, Degrees(e.Yaw), Degrees(e.Pitch), Degrees(e.Body), e.Flags,
                    e.Vx / 8000f, e.Vy / 8000f, e.Vz / 8000f,
                    names.TryGetValue(e.Id, out var n) ? n : null, variants.TryGetValue(e.Id, out var v) ? v : null));
            worlds.Add(new LiveWorld { Name = name, Entities = entities, Players = players });
        }
        if (r.Remaining != 0) throw new InvalidDataException($"{r.Remaining} bytes after the last world");
        return new LiveFrame { FullTime = fullTime, DayTime = dayTime, Raining = (weather & 1) != 0, Thundering = (weather & 2) != 0, Worlds = worlds };
    }

    private const int EntityBytes = 4 + 2 + 12 + 3 + 2 + 6;

    private readonly record struct RawEntity(int Id, string Type, float X, float Y, float Z, int Yaw, int Pitch, int Body, int Flags, int Vx, int Vy, int Vz);

    private static Dictionary<int, string> ReadIdStrings(ref Reader r)
    {
        int count = r.U16();
        r.Need(count * 5); // an id and a length byte at least
        var map = new Dictionary<int, string>(count);
        for (int i = 0; i < count; i++) map[r.I32()] = r.Str8();
        return map;
    }

    /// <summary>A signed byte of 256ths of a turn to degrees in [-180, 180).</summary>
    private static float Degrees(int angle) => angle * 360f / 256f;

    /// <summary>Java's UUID text form from its two longs.</summary>
    public static string FormatUuid(long most, long least)
    {
        string hex = ((ulong)most).ToString("x16") + ((ulong)least).ToString("x16");
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;

        public Reader(ReadOnlySpan<byte> data) { _data = data; _pos = 0; }
        public readonly int Remaining => _data.Length - _pos;

        public readonly void Need(long n)
        {
            if (n < 0 || n > Remaining) throw new InvalidDataException($"The frame ends early: {n} more bytes are needed at {_pos}, {Remaining} are left");
        }

        public void Skip(int n) { Need(n); _pos += n; }
        public int U8() { Need(1); return _data[_pos++]; }
        public int I8() { Need(1); return (sbyte)_data[_pos++]; }
        public int U16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(_data[_pos..]); _pos += 2; return v; }
        public int I16() { Need(2); var v = BinaryPrimitives.ReadInt16LittleEndian(_data[_pos..]); _pos += 2; return v; }
        public int I32() { Need(4); var v = BinaryPrimitives.ReadInt32LittleEndian(_data[_pos..]); _pos += 4; return v; }
        public long I64() { Need(8); var v = BinaryPrimitives.ReadInt64LittleEndian(_data[_pos..]); _pos += 8; return v; }
        public float F32() { Need(4); var v = BinaryPrimitives.ReadSingleLittleEndian(_data[_pos..]); _pos += 4; return v; }

        public string Str8()
        {
            int n = U8();
            Need(n);
            var s = Encoding.UTF8.GetString(_data.Slice(_pos, n));
            _pos += n;
            return s;
        }

        public string Str16()
        {
            int n = U16();
            Need(n);
            var s = Encoding.UTF8.GetString(_data.Slice(_pos, n));
            _pos += n;
            return s;
        }
    }

    // ---- encode (tests, the demo feed) -----------------------------------------------------------------------------------------

    /// <summary>The frame as the plugin would send it.</summary>
    public static byte[] Write(LiveFrame frame)
    {
        var types = new List<string>();
        var typeIndex = new Dictionary<string, int>();
        foreach (var w in frame.Worlds)
            foreach (var e in w.Entities)
                if (!typeIndex.ContainsKey(e.Type)) { typeIndex[e.Type] = types.Count; types.Add(e.Type); }

        using var ms = new MemoryStream();
        void U8(int v) => ms.WriteByte((byte)v);
        void U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); ms.Write(b); }
        void I16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16LittleEndian(b, (short)v); ms.Write(b); }
        void I32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); ms.Write(b); }
        void I64(long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, v); ms.Write(b); }
        void F32(float v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); ms.Write(b); }
        void Str8(string s) { var b = Clip(Encoding.UTF8.GetBytes(s), 255); U8(b.Length); ms.Write(b); }
        void Str16(string s) { var b = Clip(Encoding.UTF8.GetBytes(s), 65535); U16(b.Length); ms.Write(b); }

        ms.Write(Magic);
        U8(FormatVersion);
        I64(frame.FullTime);
        I64(frame.DayTime);
        U8((frame.Raining ? 1 : 0) | (frame.Thundering ? 2 : 0));
        U16(types.Count);
        foreach (var t in types) Str8(t);
        U8(frame.Worlds.Count);
        foreach (var w in frame.Worlds)
        {
            Str8(w.Name);
            U16(w.Entities.Count);
            foreach (var e in w.Entities)
            {
                I32(e.Id); U16(typeIndex[e.Type]); F32(e.X); F32(e.Y); F32(e.Z);
                U8(ToAngle(e.Yaw)); U8(ToAngle(e.Pitch)); U8(ToAngle(e.BodyYaw)); U16(e.Flags);
                I16(ToVelocity(e.Vx)); I16(ToVelocity(e.Vy)); I16(ToVelocity(e.Vz));
            }
            var named = w.Entities.Where(e => e.Name != null).ToList();
            U16(named.Count);
            foreach (var e in named) { I32(e.Id); Str8(e.Name!); }
            var varied = w.Entities.Where(e => e.Variant != null).ToList();
            U16(varied.Count);
            foreach (var e in varied) { I32(e.Id); Str8(e.Variant!); }
            U16(w.Players.Count);
            foreach (var p in w.Players)
            {
                I32(p.EntityId);
                var hex = p.Uuid.Replace("-", "");
                I64((long)Convert.ToUInt64(hex[..16], 16));
                I64((long)Convert.ToUInt64(hex[16..], 16));
                Str8(p.Name); Str16(p.SkinUrl); U8(p.Slim ? 1 : 0); U8(p.GameMode); Str8(p.HeldItem);
                U8((int)Math.Clamp(MathF.Round(p.Health * 255f), 0, 255));
            }
        }
        return ms.ToArray();
    }

    private static int ToAngle(float degrees) => (sbyte)(int)MathF.Round(degrees % 360f / 360f * 256f);

    private static int ToVelocity(float blocksPerTick) => (int)Math.Clamp(MathF.Round(blocksPerTick * 8000f), -32767, 32767);

    // Cut UTF-8 at a whole character
    private static byte[] Clip(byte[] b, int max)
    {
        if (b.Length <= max) return b;
        int n = max;
        while (n > 0 && (b[n] & 0xC0) == 0x80) n--;
        return b[..n];
    }
}
