using ILGPU;
using ILGPU.Runtime;

namespace AubsCraft.Admin.Rendering;

/// <summary>
/// ILGPU kernel that fills deep-underground space before a DISTANT chunk is meshed.
/// One thread per column (256): walking down from the top, every air/water/plant/leaves cell that has at
/// least <see cref="CoverDepth"/> terrain blocks above it becomes <c>fillId</c> (an opaque palette entry),
/// so the mesh kernels emit no cave walls for it. Caves are 65-78% of a chunk's faces and can only be
/// seen from inside the cave system, which a camera chunks away is not in. Near chunks are meshed
/// unfilled, so caves still render around the camera.
/// Block flags: 0 = solid, 1 = plant, 2 = water, 3 = solid tinted, 4 = leaves (leaves never count as cover,
/// so the ground under a tree canopy stays visible).
/// </summary>
public static class CaveFillKernel
{
    private const int ColumnsPerChunk = 256;
    private const int Height = 384;
    private const int CoverDepth = 3;

    public static void Kernel(
        Index1D index,
        ArrayView<int> blocks,
        ArrayView<float> blockFlags,
        int fillId)
    {
        int cover = 0;
        for (int y = Height - 1; y >= 0; y--)
        {
            int i = index + y * ColumnsPerChunk;
            int id = blocks[i];
            float f = id == 0 ? -1f : blockFlags[id];
            bool terrain = id != 0 && (f < 0.5f || (f > 2.5f && f < 3.5f)); // solid or solid tinted, not leaves
            if (terrain)
                cover++;
            else if (cover >= CoverDepth)
                blocks[i] = fillId;
        }
    }
}
