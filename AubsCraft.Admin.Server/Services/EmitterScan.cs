using AubsCraft.Live.Model.Particles;

namespace AubsCraft.Admin.Server.Services;

/// <summary>Finds the blocks of a column that give off particles (lit campfires and furnaces, fire, torches, candles): the viewer shows their smoke and flames.</summary>
public static class EmitterScan
{
    /// <summary>The emitters of a chunk, lowest first. A column with none of them in its palette is not walked at all.</summary>
    public static List<EmitterSpot> Scan(ChunkResult chunk)
    {
        var spots = new List<EmitterSpot>();
        var kinds = new EmitterKind[chunk.Palette.Count];
        bool any = false;
        for (int i = 0; i < kinds.Length; i++)
        {
            kinds[i] = EmitterClassifier.Classify(chunk.Palette[i]);
            any |= kinds[i] != EmitterKind.None;
        }
        if (!any) return spots;

        var blocks = chunk.Blocks;
        // Layout x + z*16 + y*256, 384 high from y = -64
        for (int i = 0; i < blocks.Length; i++)
        {
            var kind = kinds[blocks[i]];
            if (kind == EmitterKind.None) continue;
            spots.Add(new EmitterSpot(i & 15, (i >> 8) + EmitterCodec.BaseY, (i >> 4) & 15, kind));
            if (spots.Count >= EmitterCodec.MaxPerColumn) break;
        }
        return spots;
    }
}
