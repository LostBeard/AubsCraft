# Water Transparency - Implementation Reference

**Date:** 2026-04-12
**Status:** Research complete, ready for implementation

---

## Architecture

Two-pipeline, single-pass rendering:
1. Opaque pipeline (existing) - draws all solid blocks with depth write ON
2. Transparent pipeline (new) - draws water with alpha blending, depth write OFF, depth test ON

All mesh generation stays on GPU via ILGPU MinecraftMeshKernel. No CPU fallbacks.

## GPUBlendState for Transparent Pipeline

```
Color: src-alpha + one-minus-src-alpha (add)
Alpha: one + one-minus-src-alpha (add)
DepthWriteEnabled: false
DepthCompare: less
```

Do NOT clear depth buffer between opaque and transparent draws.

## Vertex Format

Stay at 11 floats (44 bytes). Do NOT expand to 12.
Hard-code water alpha = 0.6 in the shader. Per-vertex alpha is premature until stained glass needs it.

## GPU Kernel Changes (MinecraftMeshKernel.cs)

Add `ArrayView<int> transparencyFlags` parameter (1 int per palette entry, 0=opaque, 1=transparent).

Face emission logic:
- Opaque blocks emit faces toward air OR transparent neighbors
- Water blocks emit faces toward air OR opaque neighbors (NOT toward other water)
- Two separate output buffers + atomic counters (opaque vertices, water vertices)

## WGSL Shader

Two fragment entry points in same module:
- `fs_main` (existing) - opaque, alpha discard for leaves, outputs alpha=1.0
- `fs_transparent` (new) - no discard, outputs alpha=0.6, fog fades alpha too

## Sorting

Per-chunk back-to-front sort using existing distance calculation. Water chunks drawn in descending distance order. No per-face sorting needed for flat water.

## Files to Change

1. MapRenderService.cs - second pipeline, water slots, fs_transparent, two-phase draw
2. MinecraftMeshKernel.cs - transparency flags, dual output, neighbor check logic
3. ChunkMesher.cs - split output to opaque/water arrays
4. HeightmapMesher.cs - split output to opaque/water arrays
5. VoxelEngineService.cs - build flags buffer, handle dual kernel output
6. Map.razor - upload water meshes separately, build flags from palette

## ILGPU Performance Notes

- Transparency flags buffer is tiny (< 200 ints) - negligible GPU memory
- Dual output buffers double the atomic counter pressure but that's still fast
- The kernel runs once per chunk, not per frame - water mesh is cached like opaque mesh
- Back-to-front sort is CPU-side per-chunk (O(n log n) where n = water chunk count), not per-face
- Water vertex buffer uses same sub-allocation free-list pattern as opaque buffer
