namespace AubsCraft.Admin.Rendering;

/// <summary>
/// Per-section visibility connectivity + BFS visible-set computation.
/// Sodium-style graph occlusion culling: each 16x16x16 section gets a
/// 36-bit connectivity matrix (6 face × 6 face) saying "from face A you
/// can reach face B via flood-fill through transparent blocks". At render
/// time we BFS from the camera's section using this graph to determine
/// which sections are reachable. Sections not reached = definitely
/// occluded (caves, enclosed rooms, undisplayed underground voids).
///
/// Reference: tomcc.github.io/2014/08/31/visibility-1.html (Tomcc's
/// "Advanced Cave Culling Algorithm") and CaffeineMC/sodium-fabric's
/// VisibilityGraph. Algorithm ported from SpawnDev.VoxelEngine's
/// `Culling/VisibilityGraph.cs` (which AubsCraft does not consume
/// directly today; see commit message of this change for context).
/// </summary>
internal static class SectionVisibility
{
    public const int PosX = 0, NegX = 1, PosZ = 2, NegZ = 3, PosY = 4, NegY = 5;

    private static readonly int[] OppositeFace = { NegX, PosX, NegZ, PosZ, NegY, PosY };

    private static readonly (int dx, int dy, int dz)[] FaceOffsets =
    {
        (1, 0, 0),    // +X
        (-1, 0, 0),   // -X
        (0, 0, 1),    // +Z
        (0, 0, -1),   // -Z
        (0, 1, 0),    // +Y
        (0, -1, 0),   // -Y
    };

    /// <summary>Words in a section bitset: cell (x, y, z) is bit ((z &amp; 3) * 16 + x) of word (y * 4 + (z &gt;&gt; 2)).</summary>
    public const int SectionWords = 64;

    /// <summary>Bitset word and bit for section cell (x, y, z), each 0..15.</summary>
    public static (int word, int bit) CellBit(int x, int y, int z) => (y * 4 + (z >> 2), ((z & 3) << 4) + x);

    // x = 0 / x = 15 bit of every 16-bit z lane
    private const ulong LaneX0 = 0x0001_0001_0001_0001UL;
    private const ulong LaneX15 = 0x8000_8000_8000_8000UL;

    /// <summary>
    /// Compute the 36-bit connectivity mask for a single 16x16x16 section.
    /// `transparent` is the section as a 64-word bitset (<see cref="CellBit"/>); a set bit means
    /// "sight passes through this cell". Bit (entryFace * 6 + exitFace) of the result is set if a
    /// flood fill from the see-through cells on entryFace reaches see-through cells on exitFace.
    /// The fill grows the whole frontier at once with shifts and masks (no queue, no per-cell work),
    /// and all-see-through / all-solid sections (most of a chunk) return without filling.
    /// </summary>
    public static long ComputeConnectivity(ReadOnlySpan<ulong> transparent)
    {
        bool any = false, all = true;
        for (int w = 0; w < SectionWords; w++)
        {
            any |= transparent[w] != 0;
            all &= transparent[w] == ulong.MaxValue;
        }
        if (all) return AllConnected;
        // Self-connection bits (face A -> face A) are always set; they're never queried.
        if (!any) return SelfOnly;

        long connectivity = SelfOnly;
        Span<ulong> reach = stackalloc ulong[SectionWords];
        for (int entryFace = 0; entryFace < 6; entryFace++)
        {
            bool seeded = false;
            for (int w = 0; w < SectionWords; w++)
            {
                reach[w] = FaceMask(entryFace, w) & transparent[w];
                seeded |= reach[w] != 0;
            }
            if (!seeded) continue;
            Flood(reach, transparent);
            for (int exitFace = 0; exitFace < 6; exitFace++)
            {
                if (exitFace == entryFace) continue;
                for (int w = 0; w < SectionWords; w++)
                {
                    if ((reach[w] & FaceMask(exitFace, w)) != 0)
                    {
                        connectivity |= 1L << (entryFace * 6 + exitFace);
                        break;
                    }
                }
            }
        }
        return connectivity;
    }

    /// <summary>The cells of word w that lie on the given section face.</summary>
    private static ulong FaceMask(int face, int w) => face switch
    {
        NegX => LaneX0,
        PosX => LaneX15,
        NegZ => (w & 3) == 0 ? 0xFFFFUL : 0UL,
        PosZ => (w & 3) == 3 ? 0xFFFFUL << 48 : 0UL,
        NegY => w < 4 ? ulong.MaxValue : 0UL,
        PosY => w >= SectionWords - 4 ? ulong.MaxValue : 0UL,
        _ => 0UL,
    };

    /// <summary>Grows `reach` through `transparent` (6-connected) until it stops changing.</summary>
    private static void Flood(Span<ulong> reach, ReadOnlySpan<ulong> transparent)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int w = 0; w < SectionWords; w++)
            {
                ulong r = reach[w];
                ulong g = r
                    | ((r << 1) & ~LaneX0)   // x + 1 (not across z lanes)
                    | ((r >> 1) & ~LaneX15)  // x - 1
                    | (r << 16) | (r >> 16); // z +/- 1 inside the word
                int zw = w & 3;
                if (zw > 0) g |= reach[w - 1] >> 48;  // z + 1 from the previous word's last lane
                if (zw < 3) g |= reach[w + 1] << 48;  // z - 1 from the next word's first lane
                if (w >= 4) g |= reach[w - 4];        // y + 1
                if (w < SectionWords - 4) g |= reach[w + 4]; // y - 1
                g &= transparent[w];
                if (g != r)
                {
                    reach[w] = g;
                    changed = true;
                }
            }
        }
    }

    /// <summary>
    /// BFS from the camera section through the connectivity graph. Returns
    /// the set of visible (sx, sy, sz) section coordinates. Sections that
    /// don't have a stored connectivity (not loaded yet) are treated as
    /// fully connected so we don't accidentally hide visible-but-still-loading
    /// chunks; this can over-render briefly but never under-renders.
    /// The search is bounded by horizontal (XZ) distance from the camera section,
    /// matching the renderer's draw-distance test, and by the world's section rows
    /// [minSy, maxSy]. Returns null when the camera section is sealed solid (camera
    /// inside terrain): graph culling from there would hide the whole world, so the
    /// caller should draw without it, as Sodium does for a camera inside an opaque block.
    /// </summary>
    public static HashSet<(int sx, int sy, int sz)>? ComputeVisibleSections(
        (int sx, int sy, int sz) cameraSection,
        Func<(int sx, int sy, int sz), long?> getConnectivity,
        int maxDistance, int minSy, int maxSy)
    {
        var camConn = getConnectivity(cameraSection) ?? AllConnected;
        if (camConn == SelfOnly) return null;

        var visible = new HashSet<(int sx, int sy, int sz)> { cameraSection };
        var queue = new Queue<((int sx, int sy, int sz) coord, int entryFace)>();
        int maxDistSq = maxDistance * maxDistance;

        for (int face = 0; face < 6; face++)
        {
            // Camera section "exits" through every face (we entered from no
            // specific face, so seed permissively).
            var neighbor = Neighbor(cameraSection, face);
            queue.Enqueue((neighbor, OppositeFace[face]));
        }

        while (queue.Count > 0)
        {
            var (coord, entryFace) = queue.Dequeue();
            if (coord.sy < minSy || coord.sy > maxSy) continue;
            int dx = coord.sx - cameraSection.sx, dz = coord.sz - cameraSection.sz;
            if (dx * dx + dz * dz > maxDistSq) continue;
            if (!visible.Add(coord)) continue;

            var conn = getConnectivity(coord) ?? AllConnected;
            for (int exitFace = 0; exitFace < 6; exitFace++)
            {
                if (exitFace == entryFace) continue;
                if (!HasFaceToFace(conn, entryFace, exitFace)) continue;
                queue.Enqueue((Neighbor(coord, exitFace), OppositeFace[exitFace]));
            }
        }

        return visible;
    }

    private const long AllConnected = (1L << 36) - 1; // bits 0..35 all set

    // Only the always-set self bits (face A -> face A): no face reaches any other face.
    private const long SelfOnly = (1L << 0) | (1L << 7) | (1L << 14) | (1L << 21) | (1L << 28) | (1L << 35);

    private static bool HasFaceToFace(long conn, int entryFace, int exitFace)
        => (conn & (1L << (entryFace * 6 + exitFace))) != 0;

    private static (int sx, int sy, int sz) Neighbor((int sx, int sy, int sz) c, int face)
    {
        var (dx, dy, dz) = FaceOffsets[face];
        return (c.sx + dx, c.sy + dy, c.sz + dz);
    }

}
