namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// The heightmap frame of the far tier, in the server's GLOBAL block ids (see <see cref="GlobalBlockRegistry"/>), so the client hands the
/// bytes to the GPU as they arrive. Little-endian:
/// <code>
/// int32  chunk X
/// int32  chunk Z
/// int32  how many block names the registry held when the frame was made (covers every id in it)
/// int32  [256]   top block's world Y per column (cell = x + z * 16)
/// uint16 [256]   top block's global id (0 = none)
/// int32  [256]   for a water column, the world Y of the first solid block below it (otherwise -64)
/// uint16 [256]   that block's global id (0 = none)
/// </code>
/// The last four arrays are exactly SpawnDev.VoxelEngine's <c>HeightmapLayout</c> (3072 bytes), which starts at <see cref="DataOffset"/>. After them:
/// <code>
/// uint8  [16]    the biome (BiomeData id) of each 4 x 4 block cell of the chunk, cell (i, j) at j * 4 + i, i along x
/// </code>
/// </summary>
public static class HeightmapWire
{
    public const int HeaderBytes = 12;
    public const int DataOffset = HeaderBytes;
    public const int DataBytes = 256 * 4 + 256 * 2 + 256 * 4 + 256 * 2;
    /// <summary>Where the 16 biome ids start.</summary>
    public const int BiomeOffset = HeaderBytes + DataBytes;
    public const int BiomeBytes = 16;
    public const int FrameBytes = BiomeOffset + BiomeBytes;

    public static byte[] BuildIdsFrame(int chunkX, int chunkZ, HeightmapResult hm, GlobalBlockRegistry registry)
    {
        // Translate this chunk's palette once, then every column through it
        var map = new ushort[hm.Palette.Count];
        for (int i = 0; i < map.Length; i++) map[i] = registry.GetOrAdd(hm.Palette[i]);

        var frame = new byte[FrameBytes];
        var span = frame.AsSpan();
        BitConverter.TryWriteBytes(span.Slice(0, 4), chunkX);
        BitConverter.TryWriteBytes(span.Slice(4, 4), chunkZ);
        // After the translation above: the registry only grows, so this count covers every id written below
        BitConverter.TryWriteBytes(span.Slice(8, 4), registry.Count);

        int o = HeaderBytes;
        for (int c = 0; c < 256; c++, o += 4) BitConverter.TryWriteBytes(span.Slice(o, 4), hm.Heights[c]);
        for (int c = 0; c < 256; c++, o += 2) BitConverter.TryWriteBytes(span.Slice(o, 2), map[hm.BlockIds[c]]);
        for (int c = 0; c < 256; c++, o += 4) BitConverter.TryWriteBytes(span.Slice(o, 4), hm.SeabedHeights[c]);
        // A seabed id of 0 means none; the palette's index 0 is air, which maps to global id 0 too
        for (int c = 0; c < 256; c++, o += 2) BitConverter.TryWriteBytes(span.Slice(o, 2), map[hm.SeabedBlockIds[c]]);
        // The biome cells (plains where the chunk's data had none)
        if (hm.Biomes is { Length: BiomeBytes }) hm.Biomes.CopyTo(span.Slice(BiomeOffset, BiomeBytes));
        else span.Slice(BiomeOffset, BiomeBytes).Fill(AubsCraft.Live.Model.Biomes.BiomeData.Plains);
        return frame;
    }
}
