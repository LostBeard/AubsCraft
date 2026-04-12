using System.IO.Compression;
using System.Text;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Streaming reader for Minecraft's Named Binary Tag (NBT) format.
/// Supports all tag types including the packed long arrays used for block states.
/// </summary>
public sealed class NbtReader
{
    private readonly BinaryReader _reader;

    public NbtReader(Stream stream)
    {
        _reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
    }

    /// <summary>
    /// Reads an NBT compound from a zlib-compressed byte array (the format used in region files).
    /// </summary>
    public static NbtCompound ReadCompressed(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
        using var buffered = new BufferedStream(zlib, 8192);
        var reader = new NbtReader(buffered);
        return reader.ReadRootCompound();
    }

    public NbtCompound ReadRootCompound()
    {
        var tagType = (NbtTagType)_reader.ReadByte();
        if (tagType != NbtTagType.Compound)
            throw new InvalidDataException($"Expected root compound, got {tagType}");
        ReadString(); // root tag name (usually empty)
        return ReadCompound();
    }

    private NbtCompound ReadCompound()
    {
        var compound = new NbtCompound();
        while (true)
        {
            var tagType = (NbtTagType)_reader.ReadByte();
            if (tagType == NbtTagType.End)
                break;
            var name = ReadString();
            compound[name] = ReadTagValue(tagType);
        }
        return compound;
    }

    private object ReadTagValue(NbtTagType tagType) => tagType switch
    {
        NbtTagType.Byte => _reader.ReadByte(),
        NbtTagType.Short => ReadInt16BE(),
        NbtTagType.Int => ReadInt32BE(),
        NbtTagType.Long => ReadInt64BE(),
        NbtTagType.Float => ReadFloatBE(),
        NbtTagType.Double => ReadDoubleBE(),
        NbtTagType.ByteArray => ReadByteArray(),
        NbtTagType.String => ReadString(),
        NbtTagType.List => ReadList(),
        NbtTagType.Compound => ReadCompound(),
        NbtTagType.IntArray => ReadIntArray(),
        NbtTagType.LongArray => ReadLongArray(),
        _ => throw new InvalidDataException($"Unknown NBT tag type: {tagType}")
    };

    private string ReadString()
    {
        var length = ReadUInt16BE();
        if (length == 0) return "";
        var bytes = _reader.ReadBytes(length);
        return Encoding.UTF8.GetString(bytes);
    }

    private NbtList ReadList()
    {
        var elementType = (NbtTagType)_reader.ReadByte();
        var count = ReadInt32BE();
        var list = new NbtList(elementType, count);
        for (int i = 0; i < count; i++)
            list.Add(ReadTagValue(elementType));
        return list;
    }

    private byte[] ReadByteArray()
    {
        var length = ReadInt32BE();
        return _reader.ReadBytes(length);
    }

    private int[] ReadIntArray()
    {
        var length = ReadInt32BE();
        var arr = new int[length];
        for (int i = 0; i < length; i++)
            arr[i] = ReadInt32BE();
        return arr;
    }

    private long[] ReadLongArray()
    {
        var length = ReadInt32BE();
        var arr = new long[length];
        for (int i = 0; i < length; i++)
            arr[i] = ReadInt64BE();
        return arr;
    }

    // NBT uses big-endian encoding
    private short ReadInt16BE()
    {
        Span<byte> buf = stackalloc byte[2];
        _reader.BaseStream.ReadExactly(buf);
        return (short)((buf[0] << 8) | buf[1]);
    }

    private ushort ReadUInt16BE()
    {
        Span<byte> buf = stackalloc byte[2];
        _reader.BaseStream.ReadExactly(buf);
        return (ushort)((buf[0] << 8) | buf[1]);
    }

    private int ReadInt32BE()
    {
        Span<byte> buf = stackalloc byte[4];
        _reader.BaseStream.ReadExactly(buf);
        return (buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3];
    }

    private long ReadInt64BE()
    {
        Span<byte> buf = stackalloc byte[8];
        _reader.BaseStream.ReadExactly(buf);
        return ((long)buf[0] << 56) | ((long)buf[1] << 48) | ((long)buf[2] << 40) | ((long)buf[3] << 32)
             | ((long)buf[4] << 24) | ((long)buf[5] << 16) | ((long)buf[6] << 8) | buf[7];
    }

    private float ReadFloatBE()
    {
        var bits = ReadInt32BE();
        return BitConverter.Int32BitsToSingle(bits);
    }

    private double ReadDoubleBE()
    {
        var bits = ReadInt64BE();
        return BitConverter.Int64BitsToDouble(bits);
    }
}

public enum NbtTagType : byte
{
    End = 0, Byte = 1, Short = 2, Int = 3, Long = 4,
    Float = 5, Double = 6, ByteArray = 7, String = 8,
    List = 9, Compound = 10, IntArray = 11, LongArray = 12
}

/// <summary>
/// A named compound tag containing key-value pairs.
/// </summary>
public sealed class NbtCompound : Dictionary<string, object>
{
    public T Get<T>(string key) => ContainsKey(key) ? (T)this[key] : default!;

    public NbtCompound GetCompound(string key) => Get<NbtCompound>(key);
    public NbtList GetList(string key) => Get<NbtList>(key);
    public string GetString(string key) => Get<string>(key) ?? "";
    public int GetInt(string key) => ContainsKey(key) ? Convert.ToInt32(this[key]) : 0;
    public long GetLong(string key) => ContainsKey(key) ? Convert.ToInt64(this[key]) : 0;
    public long[] GetLongArray(string key) => Get<long[]>(key) ?? [];
    public byte GetByte(string key) => ContainsKey(key) ? Convert.ToByte(this[key]) : (byte)0;
}

/// <summary>
/// A typed list tag.
/// </summary>
public sealed class NbtList : List<object>
{
    public NbtTagType ElementType { get; }

    public NbtList(NbtTagType elementType, int capacity) : base(capacity)
    {
        ElementType = elementType;
    }

    public NbtCompound GetCompound(int index) => (NbtCompound)this[index];
    public string GetString(int index) => (string)this[index];
}
