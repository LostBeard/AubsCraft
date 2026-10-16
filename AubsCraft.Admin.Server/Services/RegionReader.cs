using AubsCraft.Live.Model.Biomes;
using AubsCraft.Live.Model.BlockEntities;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Reads Minecraft Anvil format region files (.mca).
/// Each region file contains up to 32x32 chunks (1024 total).
/// </summary>
public sealed class RegionReader
{
    private const int SectorSize = 4096;
    private const int HeaderSize = 8192; // 4096 bytes offsets + 4096 bytes timestamps
    private const int SectionsPerChunk = 24; // Y range -64 to 319 = 384 blocks = 24 sections of 16
    private const int SectionHeight = 16;
    private const int ChunkWidth = 16;
    private const int TotalHeight = SectionsPerChunk * SectionHeight; // 384

    /// <summary>
    /// Opens a region file the running Minecraft server may be writing. File.OpenRead denies other writers,
    /// which on Windows fails with a sharing violation while the server holds the file open (Linux has no
    /// such locks, so production never saw it); the reader must allow concurrent writes.
    /// </summary>
    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// Reads a single chunk column from a region file.
    /// Returns a flat ushort array [16 * 384 * 16] indexed as x + z*16 + y*256.
    /// Block ID 0 = air. Other IDs are indices into the returned palette.
    /// </summary>
    public static ChunkResult? ReadChunk(string regionPath, int localX, int localZ)
    {
        using var fs = OpenShared(regionPath);
        using var reader = new BinaryReader(fs);

        // Read the chunk offset from the header
        // Each entry is 4 bytes: 3 bytes offset (in 4KB sectors), 1 byte size (in sectors)
        var headerIndex = 4 * ((localX & 31) + (localZ & 31) * 32);
        fs.Position = headerIndex;
        var offsetData = reader.ReadInt32BigEndian();
        var sectorOffset = offsetData >> 8;
        var sectorCount = offsetData & 0xFF;

        if (sectorOffset == 0 || sectorCount == 0)
            return null; // Chunk not generated

        // Seek to the chunk data
        fs.Position = (long)sectorOffset * SectorSize;
        var dataLength = reader.ReadInt32BigEndian();
        var compressionType = reader.ReadByte(); // 1=gzip, 2=zlib, 3=uncompressed
        var compressedData = reader.ReadBytes(dataLength - 1);

        if (compressionType != 2)
            throw new NotSupportedException($"Unsupported compression type: {compressionType}");

        // Parse the NBT data
        var nbt = NbtReader.ReadCompressed(compressedData);
        return ParseChunkNbt(nbt);
    }

    /// <summary>
    /// Lists all chunks that exist in a region file.
    /// Returns list of (localX, localZ) pairs for populated chunks.
    /// </summary>
    public static List<(int localX, int localZ)> ListChunks(string regionPath)
    {
        var chunks = new List<(int, int)>();
        using var fs = OpenShared(regionPath);
        using var reader = new BinaryReader(fs);

        for (int i = 0; i < 1024; i++)
        {
            var offsetData = reader.ReadInt32BigEndian();
            if ((offsetData >> 8) != 0)
            {
                var localX = i % 32;
                var localZ = i / 32;
                chunks.Add((localX, localZ));
            }
        }
        return chunks;
    }

    /// <summary>A chunk column from its NBT (public for the tests).</summary>
    public static ChunkResult ParseChunkNbt(NbtCompound root)
    {
        var blocks = new ushort[ChunkWidth * TotalHeight * ChunkWidth];
        var palette = new List<string> { "minecraft:air" }; // ID 0 is always air

        // Minecraft 1.18+ stores sections as a list
        var sections = root.GetList("sections");
        if (sections == null || sections.Count == 0)
            return new ChunkResult(blocks, palette);

        // The biomes of the column's surface: those of the highest section that holds any block (see BiomeCells)
        int topYOffset = -1;
        byte[]? biomes = null;
        // The blocks the game draws with a model (chests, beds, signs, banners, heads, shulker boxes): where they are and how they face, read from the raw properties
        // (the palette's names do not keep them), so the viewer can draw them
        List<BlockEntitySpot>? blockEntities = null;

        for (int i = 0; i < sections.Count; i++)
        {
            try
            {
            var section = sections.GetCompound(i);
            var sectionY = section.GetByte("Y");
            // Convert from signed section Y to our array offset
            // In 1.18+, sections range from Y=-4 (section Y = -4) to Y=19
            var yOffset = ((int)(sbyte)sectionY + 4) * SectionHeight; // -4 -> 0, 0 -> 64, 19 -> 368
            if (yOffset < 0 || yOffset >= TotalHeight) continue;

            if (!section.ContainsKey("block_states")) continue;
            var blockStates = section.GetCompound("block_states");

            // Parse the section palette
            var sectionPalette = blockStates.GetList("palette");
            if (sectionPalette == null || sectionPalette.Count == 0) continue;

            // Map section palette indices to global palette indices
            var paletteMap = new ushort[sectionPalette.Count];
            BlockEntitySpot?[]? entityOf = null;
            for (int p = 0; p < sectionPalette.Count; p++)
            {
                var entry = sectionPalette.GetCompound(p);
                if (BlockEntityKinds.Classify(entry.GetString("Name"), entry.ContainsKey("Properties") ? entry.GetCompound("Properties") : null) is { } entitySpot)
                {
                    entityOf ??= new BlockEntitySpot?[sectionPalette.Count];
                    entityOf[p] = entitySpot;
                }
                // The block as the viewer tells it apart: its name and the properties that change its look (a log lying down is not an upright one)
                var blockName = BlockStateNamer.Current.Key(entry.GetString("Name"), entry.ContainsKey("Properties") ? entry.GetCompound("Properties") : null);
                var globalIndex = palette.IndexOf(blockName);
                if (globalIndex < 0)
                {
                    globalIndex = palette.Count;
                    palette.Add(blockName);
                }
                paletteMap[p] = (ushort)globalIndex;
            }

            // If the section has only one block type (single palette entry), fill it
            if (sectionPalette.Count == 1)
            {
                var blockId = paletteMap[0];
                if (blockId == 0) continue; // All air, skip
                NoteBiomes(section, yOffset, ref topYOffset, ref biomes);
                if (entityOf?[0] is { } whole)
                    for (int y = 0; y < SectionHeight && (blockEntities?.Count ?? 0) < BlockEntityCodec.MaxPerColumn; y++)
                        for (int z = 0; z < ChunkWidth; z++)
                            for (int x = 0; x < ChunkWidth; x++)
                                (blockEntities ??= new()).Add(whole with { X = x, Y = yOffset + y - 64, Z = z });
                for (int y = 0; y < SectionHeight; y++)
                    for (int z = 0; z < ChunkWidth; z++)
                        for (int x = 0; x < ChunkWidth; x++)
                            blocks[x + z * ChunkWidth + (yOffset + y) * ChunkWidth * ChunkWidth] = blockId;
                continue;
            }

            NoteBiomes(section, yOffset, ref topYOffset, ref biomes);

            // Unpack the packed long array
            if (!blockStates.ContainsKey("data")) continue;
            var dataObj = blockStates["data"];
            if (dataObj is not long[] data || data.Length == 0) continue;

            var bitsPerBlock = Math.Max(4, (int)Math.Ceiling(Math.Log2(sectionPalette.Count)));
            var mask = (1L << bitsPerBlock) - 1;
            var blocksPerLong = 64 / bitsPerBlock;

            // Minecraft 1.18+ packs entries aligned within each long (no spanning)
            int blockIndex = 0;
            for (int longIdx = 0; longIdx < data.Length && blockIndex < 4096; longIdx++)
            {
                var packed = data[longIdx];
                for (int b = 0; b < blocksPerLong && blockIndex < 4096; b++)
                {
                    var paletteIndex = (int)(packed & mask);
                    packed >>= bitsPerBlock;

                    if (paletteIndex >= 0 && paletteIndex < paletteMap.Length)
                    {
                        var localY = blockIndex / 256;
                        var localZ = (blockIndex % 256) / 16;
                        var localX = blockIndex % 16;
                        var idx = localX + localZ * ChunkWidth + (yOffset + localY) * ChunkWidth * ChunkWidth;
                        if (idx >= 0 && idx < blocks.Length)
                            blocks[idx] = paletteMap[paletteIndex];
                        if (entityOf?[paletteIndex] is { } found && (blockEntities?.Count ?? 0) < BlockEntityCodec.MaxPerColumn)
                            (blockEntities ??= new()).Add(found with { X = localX, Y = yOffset + localY - 64, Z = localZ });
                    }
                    blockIndex++;
                }
            }
            }
            catch { } // Skip malformed sections
        }

        return new ChunkResult(blocks, palette, biomes, blockEntities);
    }

    // A section with blocks that is higher than any seen so far: its biomes are the column's
    private static void NoteBiomes(NbtCompound section, int yOffset, ref int topYOffset, ref byte[]? biomes)
    {
        if (yOffset <= topYOffset) return;
        var cells = BiomeCells(section);
        if (cells == null) return;
        topYOffset = yOffset;
        biomes = cells;
    }

    /// <summary>
    /// The biome of each 4 x 4 block cell of a section's layer 1 (blocks 4..7 up), as <see cref="BiomeData"/> ids, cell (i, j) at j * 4 + i with i along x and j along z; null when the section has no
    /// biome data. A section stores 4 x 4 x 4 biome cells (a palette of names and, with more than one, packed indices of ceil(log2(n)) bits, at least one, aligned within each long).
    /// </summary>
    public static byte[]? BiomeCells(NbtCompound section)
    {
        if (!section.ContainsKey("biomes")) return null;
        var biomes = section.GetCompound("biomes");
        var palette = biomes.GetList("palette");
        if (palette == null || palette.Count == 0) return null;
        var ids = new byte[palette.Count];
        for (int p = 0; p < ids.Length; p++) ids[p] = BiomeData.IdOf(palette.GetString(p));
        var cells = new byte[16];
        if (palette.Count == 1)
        {
            System.Array.Fill(cells, ids[0]);
            return cells;
        }
        var data = biomes.GetLongArray("data");
        int bits = Math.Max(1, (int)Math.Ceiling(Math.Log2(palette.Count)));
        int perLong = 64 / bits;
        long mask = (1L << bits) - 1;
        const int layer = 1;
        for (int j = 0; j < 4; j++)
            for (int i = 0; i < 4; i++)
            {
                int index = (layer * 4 + j) * 4 + i;          // y, then z, then x
                int word = index / perLong;
                if (word >= data.Length) { cells[j * 4 + i] = BiomeData.Plains; continue; }
                int paletteIndex = (int)((data[word] >> ((index % perLong) * bits)) & mask);
                cells[j * 4 + i] = paletteIndex < ids.Length ? ids[paletteIndex] : BiomeData.Plains;
            }
        return cells;
    }
}

/// <summary>
/// Result of reading a chunk column: block IDs and the chunk's palette. A palette entry is a block's name, with the properties that change its look when it has
/// any (<see cref="BlockStateNamer"/>): <c>minecraft:oak_log[axis=x]</c>. Code that wants the plain name uses <see cref="BlockStateNamer.NameOf"/>.
/// </summary>
public record ChunkResult(ushort[] Blocks, List<string> Palette, byte[]? Biomes = null, List<BlockEntitySpot>? BlockEntities = null);

internal static class BinaryReaderExtensions
{
    public static int ReadInt32BigEndian(this BinaryReader reader)
    {
        Span<byte> buf = stackalloc byte[4];
        reader.BaseStream.ReadExactly(buf);
        return (buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3];
    }
}
