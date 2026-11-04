using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AubsCraft.Admin.Server.Tests;

/// <summary>One chunk of a region file the tests write: its status as the game names it, and which sections hold what (each section filled with one block).</summary>
public sealed record RegionChunk(int LocalX, int LocalZ, string Status, (sbyte Y, string Block)[] Sections, uint Stamp, bool Corrupt = false);

/// <summary>
/// Writes real region files, byte for byte in the game's format (a header of places and times, then each chunk in sectors of its own, zlib compressed NBT), so the tests read them through the
/// panel's real path.
/// </summary>
public static class RegionFileWriter
{
    private const int Sector = 4096;

    public static void Write(string path, IEnumerable<RegionChunk> chunks)
    {
        var header = new byte[2 * Sector];
        using var body = new MemoryStream();
        int nextSector = 2;
        foreach (var chunk in chunks)
        {
            var nbt = Nbt(chunk);
            using var zipped = new MemoryStream();
            using (var z = new ZLibStream(zipped, CompressionLevel.Fastest, leaveOpen: true)) z.Write(nbt);
            var data = zipped.ToArray();
            if (chunk.Corrupt) Array.Fill(data, (byte)0x5A);
            var record = new byte[5 + data.Length];
            BinaryPrimitives.WriteInt32BigEndian(record, data.Length + 1);
            record[4] = 2;   // zlib
            data.CopyTo(record, 5);
            int sectors = (record.Length + Sector - 1) / Sector;
            body.Write(record);
            body.Write(new byte[sectors * Sector - record.Length]);
            int index = chunk.LocalX + chunk.LocalZ * 32;
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(index * 4), (nextSector << 8) | sectors);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(Sector + index * 4), chunk.Stamp);
            nextSector += sectors;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        file.Write(header);
        body.Position = 0;
        body.CopyTo(file);
    }

    private static byte[] Nbt(RegionChunk chunk)
    {
        using var ms = new MemoryStream();
        void U8(int v) => ms.WriteByte((byte)v);
        void I32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); ms.Write(b); }
        void Str(string s) { var b = Encoding.UTF8.GetBytes(s); U8(b.Length >> 8); U8(b.Length); ms.Write(b); }
        void Tag(int type, string name) { U8(type); Str(name); }
        const int ByteTag = 1, StringTag = 8, ListTag = 9, CompoundTag = 10, IntTag = 3;

        Tag(CompoundTag, "");
        Tag(IntTag, "DataVersion"); I32(4325);
        Tag(StringTag, "Status"); Str(chunk.Status);
        Tag(ListTag, "sections");
        U8(CompoundTag); I32(chunk.Sections.Length);
        foreach (var (y, block) in chunk.Sections)
        {
            Tag(ByteTag, "Y"); U8((byte)y);
            Tag(CompoundTag, "block_states");
            Tag(ListTag, "palette");
            U8(CompoundTag); I32(1);
            Tag(StringTag, "Name"); Str(block);
            U8(0);   // the palette entry
            U8(0);   // block_states
            U8(0);   // the section
        }
        U8(0);   // the root
        return ms.ToArray();
    }
}
