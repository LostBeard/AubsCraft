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

    /// <summary>
    /// Compute the 36-bit connectivity mask for a single 16x16x16 section.
    /// `transparentMask` is a 4096-bool array (length 16*16*16) where true
    /// means "sight passes through this cell" (air, water, plants, glass).
    /// Bit layout: bit (entryFace * 6 + exitFace) is set if flood-fill from
    /// air on entryFace reaches air on exitFace.
    /// </summary>
    public static long ComputeConnectivity(ReadOnlySpan<bool> transparentMask)
    {
        long connectivity = 0;
        // Self-connection bits (face A → face A) are always set; they're never
        // queried but seeding helps a future "is face F connected to anything"
        // shortcut.
        for (int f = 0; f < 6; f++) connectivity |= (1L << (f * 6 + f));

        for (int entryFace = 0; entryFace < 6; entryFace++)
        {
            var reachable = FloodFillFromFace(transparentMask, entryFace);
            for (int exitFace = 0; exitFace < 6; exitFace++)
            {
                if (exitFace == entryFace) continue;
                if (FaceReached(reachable, exitFace))
                    connectivity |= (1L << (entryFace * 6 + exitFace));
            }
        }
        return connectivity;
    }

    /// <summary>
    /// BFS from the camera section through the connectivity graph. Returns
    /// the set of visible (sx, sy, sz) section coordinates. Sections that
    /// don't have a stored connectivity (not loaded yet) are treated as
    /// fully connected so we don't accidentally hide visible-but-still-loading
    /// chunks; this can over-render briefly but never under-renders.
    /// </summary>
    public static HashSet<(int sx, int sy, int sz)> ComputeVisibleSections(
        (int sx, int sy, int sz) cameraSection,
        Func<(int sx, int sy, int sz), long?> getConnectivity,
        int maxDistance)
    {
        var visible = new HashSet<(int sx, int sy, int sz)> { cameraSection };
        var queue = new Queue<((int sx, int sy, int sz) coord, int entryFace, int depth)>();

        var camConn = getConnectivity(cameraSection) ?? AllConnected;
        for (int face = 0; face < 6; face++)
        {
            // Camera section "exits" through every face that has any outgoing
            // connection from any other face (we entered from no specific face,
            // so use a permissive seed: any face that connects to anything).
            if (!HasAnyOutgoing(camConn, face)) continue;
            var neighbor = Neighbor(cameraSection, face);
            queue.Enqueue((neighbor, OppositeFace[face], 1));
        }

        while (queue.Count > 0)
        {
            var (coord, entryFace, depth) = queue.Dequeue();
            if (depth > maxDistance) continue;
            if (!visible.Add(coord)) continue;

            var conn = getConnectivity(coord) ?? AllConnected;
            for (int exitFace = 0; exitFace < 6; exitFace++)
            {
                if (exitFace == entryFace) continue;
                if (!HasFaceToFace(conn, entryFace, exitFace)) continue;
                queue.Enqueue((Neighbor(coord, exitFace), OppositeFace[exitFace], depth + 1));
            }
        }

        return visible;
    }

    private const long AllConnected = (1L << 36) - 1; // bits 0..35 all set

    private static bool HasFaceToFace(long conn, int entryFace, int exitFace)
        => (conn & (1L << (entryFace * 6 + exitFace))) != 0;

    private static bool HasAnyOutgoing(long conn, int face)
    {
        // Any of the 6 bits at face*6 .. face*6+5 set?
        long mask = 0x3FL << (face * 6);
        return (conn & mask) != 0;
    }

    private static (int sx, int sy, int sz) Neighbor((int sx, int sy, int sz) c, int face)
    {
        var (dx, dy, dz) = FaceOffsets[face];
        return (c.sx + dx, c.sy + dy, c.sz + dz);
    }

    private static bool[] FloodFillFromFace(ReadOnlySpan<bool> transparent, int face)
    {
        var visited = new bool[4096];
        var queue = new Queue<int>();
        SeedFace(transparent, face, visited, queue);

        while (queue.Count > 0)
        {
            int idx = queue.Dequeue();
            int x = idx & 15;
            int z = (idx >> 4) & 15;
            int y = idx >> 8;
            TryEnqueue(transparent, x + 1, y, z, visited, queue);
            TryEnqueue(transparent, x - 1, y, z, visited, queue);
            TryEnqueue(transparent, x, y + 1, z, visited, queue);
            TryEnqueue(transparent, x, y - 1, z, visited, queue);
            TryEnqueue(transparent, x, y, z + 1, visited, queue);
            TryEnqueue(transparent, x, y, z - 1, visited, queue);
        }
        return visited;
    }

    private static void SeedFace(ReadOnlySpan<bool> transparent, int face, bool[] visited, Queue<int> queue)
    {
        for (int a = 0; a < 16; a++)
            for (int b = 0; b < 16; b++)
            {
                int x, y, z;
                switch (face)
                {
                    case PosX: x = 15; y = b; z = a; break;
                    case NegX: x = 0; y = b; z = a; break;
                    case PosZ: x = a; y = b; z = 15; break;
                    case NegZ: x = a; y = b; z = 0; break;
                    case PosY: x = a; y = 15; z = b; break;
                    case NegY: x = a; y = 0; z = b; break;
                    default: continue;
                }
                int idx = x + (z << 4) + (y << 8);
                if (transparent[idx] && !visited[idx])
                {
                    visited[idx] = true;
                    queue.Enqueue(idx);
                }
            }
    }

    private static bool FaceReached(bool[] visited, int face)
    {
        for (int a = 0; a < 16; a++)
            for (int b = 0; b < 16; b++)
            {
                int x, y, z;
                switch (face)
                {
                    case PosX: x = 15; y = b; z = a; break;
                    case NegX: x = 0; y = b; z = a; break;
                    case PosZ: x = a; y = b; z = 15; break;
                    case NegZ: x = a; y = b; z = 0; break;
                    case PosY: x = a; y = 15; z = b; break;
                    case NegY: x = a; y = 0; z = b; break;
                    default: continue;
                }
                int idx = x + (z << 4) + (y << 8);
                if (visited[idx]) return true;
            }
        return false;
    }

    private static void TryEnqueue(ReadOnlySpan<bool> transparent, int x, int y, int z, bool[] visited, Queue<int> queue)
    {
        if ((uint)x >= 16 || (uint)y >= 16 || (uint)z >= 16) return;
        int idx = x + (z << 4) + (y << 8);
        if (visited[idx]) return;
        if (!transparent[idx]) return;
        visited[idx] = true;
        queue.Enqueue(idx);
    }
}
