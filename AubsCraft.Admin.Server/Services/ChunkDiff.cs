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
        return Extras(a) == Extras(b) ? -1 : 0;
    }

    // What a viewer shows of a chunk beyond its blocks, as one piece of text. Not the records themselves: a sign holds its lines in an array, and a record compares an array by reference, so every sign looked changed
    private static string Extras(ChunkResult c)
    {
        var text = new System.Text.StringBuilder();
        foreach (var s in c.SignTexts ?? []) text.Append(s.X).Append(',').Append(s.Y).Append(',').Append(s.Z).Append(':').AppendJoin('\u0001', s.Lines).Append(':').Append(s.Color).Append(s.Glowing).Append(';');
        foreach (var b in c.Banners ?? []) text.Append(b.X).Append(',').Append(b.Y).Append(',').Append(b.Z).Append(':').AppendJoin('/', b.Layers).Append(';');
        foreach (var e in c.BlockEntities ?? []) text.Append(e).Append(';');
        return text.ToString();
    }

    /// <summary>
    /// A number that stands for what a viewer shows of a chunk: the same blocks and extras give the same number whatever their palette's order. It is kept for a chunk that has gone from the panel's memory, so a save that
    /// changed nothing still sends no viewer after it. Good for this process only (a string's hash code differs from run to run).
    /// </summary>
    public static long ContentHash(ChunkResult c)
    {
        var names = new int[c.Palette.Count];
        for (int i = 0; i < names.Length; i++) names[i] = c.Palette[i].GetHashCode();
        // FNV-1a over a whole block at a time
        unchecked
        {
            long h = -3750763034362895579L;
            foreach (var id in c.Blocks) h = (h ^ (id < names.Length ? names[id] : id)) * 1099511628211L;
            return (h ^ Extras(c).GetHashCode()) * 1099511628211L;
        }
    }
}
