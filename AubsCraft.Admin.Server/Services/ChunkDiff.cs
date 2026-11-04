namespace AubsCraft.Admin.Server.Services;

/// <summary>Whether two copies of a chunk differ in anything a viewer shows, and where.</summary>
public static class ChunkDiff
{
    /// <summary>
    /// -1 when the two copies look the same to a viewer. Otherwise which sides of the chunk differ (1 -X, 2 +X, 4 -Z, 8 +Z; 0 when only its inside does): the neighbours on those sides have faces toward
    /// this chunk to work out again. Blocks are compared by name, as each copy has a palette of its own.
    /// </summary>
    public static int BorderOfChange(ChunkResult a, ChunkResult b)
    {
        // Each palette index of a, as an index of b's palette (-1: b has no such block, so a cell with that index differs)
        var byName = new Dictionary<string, int>(b.Palette.Count, StringComparer.Ordinal);
        for (int i = 0; i < b.Palette.Count; i++) byName[b.Palette[i]] = i;
        var map = new int[a.Palette.Count];
        for (int i = 0; i < map.Length; i++) map[i] = byName.TryGetValue(a.Palette[i], out var at) ? at : -1;

        const int Width = 16;
        bool any = false;
        int border = 0;
        int length = Math.Min(a.Blocks.Length, b.Blocks.Length);
        for (int i = 0; i < length; i++)
        {
            int ida = a.Blocks[i];
            if (ida < map.Length && map[ida] == b.Blocks[i]) continue;
            any = true;
            int x = i % Width, z = i / Width % Width;
            if (x == 0) border |= 1;
            if (x == Width - 1) border |= 2;
            if (z == 0) border |= 4;
            if (z == Width - 1) border |= 8;
            if (border == 15) break;
        }
        if (any) return border;
        return Same(a.SignTexts, b.SignTexts) && Same(a.Banners, b.Banners) && Same(a.BlockEntities, b.BlockEntities) ? -1 : 0;
    }

    private static bool Same<T>(List<T>? a, List<T>? b)
    {
        if (a is null or { Count: 0 }) return b is null or { Count: 0 };
        return b != null && a.SequenceEqual(b);
    }
}
