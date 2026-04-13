# Voxel Engine Performance Techniques - Reference Guide

**Date:** 2026-04-13
**Purpose:** Local reference copy of key techniques from published articles and open-source projects

---

## 1. Greedy Meshing

**Sources:** [0fps.net - Meshing in Minecraft](https://0fps.net/2012/06/30/meshing-in-a-minecraft-game/), [Greedy Meshing Visually](https://fluff.blog/2023/04/24/greedy-meshing-visually.html), [Vercidium](https://vercidium.com/blog/voxel-world-optimisations/)

Works on 2D cross-sections. For each slice: find first uncovered visible face, expand into largest rectangle, emit quad, mark visited, repeat.

**Two-phase merge:**
1. Expand along primary axis (e.g., Y for X-faces)
2. Expand strip along secondary axis

**Reduction:** 85-97% polygon reduction on typical terrain. 8x8x8 solid cube: 3,072 quads (naive) -> 6 quads (greedy) = 512x reduction.

---

## 2. Binary Greedy Meshing

**Source:** [cgerikj/binary-greedy-meshing](https://github.com/cgerikj/binary-greedy-meshing)

Processes 64 faces simultaneously using 64-bit integer bitwise ops.

**Three phases:**
1. **Occupancy:** 64x64 array of u64 values, each bit = 1 voxel
2. **Hidden face culling:** Shift occupancy by 1 bit, XOR/AND to find visible faces. 64 faces culled in one operation.
3. **Greedy merge:** Bitwise operations to merge runs of identical faces

**Output:** 8 bytes per quad (4 bytes position+size, 4 bytes voxel type)

**Performance:** ~74 microseconds per chunk (single-threaded). ~30x faster than conventional mesh generation.

---

## 3. Vertex Compression

**Sources:** [Voxel Wiki - Vertex Packing](https://voxel.wiki/wiki/vertex-packing/), [Vercidium](https://vercidium.com/blog/voxel-world-optimisations/), [Nick's Blog](https://nickmcd.me/2021/04/04/high-performance-voxel-engine/)

### Vercidium - 4 bytes per vertex (1 uint32)
```
[6-bit X][6-bit Y][6-bit Z][5-bit texUnit][4-bit health][3-bit normal][2-bit spare]
```

### Binary greedy meshing - 8 bytes per QUAD (not per vertex)
```
word0: [6-bit X][6-bit Y][6-bit Z][6-bit width][6-bit height][2-bit spare]
word1: [8-bit voxel type][24-bit spare]
```

### Sodium - 20 bytes per vertex
```
[3x20-bit position][RGBA color][2x15-bit UV][block+sky light]
```

### Our current format - 44 bytes per vertex
```
[float3 position][float3 normal][float3 color][float2 UV] = 11 floats
```

**Key insight:** Voxel positions are integers (3 bits for 0-7, 5 bits for 0-31). Normals are 6 directions (3 bits). UVs derivable from position+face. Everything fits in 4-8 bytes.

---

## 4. LOD - POP Buffers for Blocky Voxels

**Source:** [0fps.net - LOD for Blocky Voxels](https://0fps.net/2018/03/03/a-level-of-detail-method-for-blocky-voxels/)

Quantization: `L_i(v) = 2^i * floor(v / 2^i)`

Rounds vertex positions to progressively coarser power-of-two grids. Primitives culled when edge collapse occurs.

**Seam handling via geomorphing** (continuous interpolation - no skirts needed):
```
L_t(x) = (ceil(t) - t) * 2^floor(t) * floor(x / 2^floor(t))
       + (t - floor(t)) * 2^ceil(t) * floor(x / 2^ceil(t))
```

**Why NOT Transvoxel:** Transvoxel is for smooth terrain (marching cubes). Blocky voxels use POP buffers instead - simpler and more appropriate for axis-aligned quads.

---

## 5. Cave Culling Algorithm

**Source:** [tomcc - Advanced Cave Culling](https://tomcc.github.io/2014/08/31/visibility-1.html) (developed for MCPE 0.9, later backported to Minecraft PC)

**Offline (per chunk generation):**
1. Flood fill through air blocks in chunk
2. Track which chunk faces the flood fill exits through
3. Build 6x6 boolean visibility matrix: "can face A see face B?"
4. Only 15 bits needed per chunk (symmetric matrix)

**Runtime (BFS from camera chunk):**
1. Start at camera chunk
2. For each chunk in queue, check entry face
3. Only add neighbors if visibility matrix says entry face connects to exit face
4. Prunes entire underground cave systems

**Performance:** 50-99% geometry culled. 99% when underground looking up. 0.1-0.2ms per chunk to build visibility data.

---

## 6. Indirect Draw on WebGPU

**Source:** [toji.dev - Indirect Draw Best Practices](https://toji.dev/webgpu-best-practices/indirect-draws.html)

Buffer layout for drawIndirect: `[vertexCount, instanceCount, firstVertex, firstInstance]` (4 x uint32 = 16 bytes)

**GPU-driven culling pipeline:**
1. Compute shader reads chunk AABBs + frustum planes
2. Writes draw params to indirect buffer (instanceCount=0 for culled)
3. Render pass reads indirect buffer

**CRITICAL:** All indirect args in ONE buffer = 300x faster validation on Chrome/D3D12 vs separate buffers.

**No multi-draw-indirect yet** in standard WebGPU (experimental in Chrome). Workaround: loop of drawIndirect calls at different offsets + render bundles.

---

## 7. Texture Arrays vs Atlases

**Source:** [0fps.net - Texture Atlases, Wrapping and Mip Mapping](https://0fps.net/2013/07/09/texture-atlases-wrapping-and-mip-mapping/)

**Atlas problem:** Mipmapping bleeds neighboring tile colors. UV wrapping selects wrong mip level = grey bands.

**Texture array solution:** Each block type is a separate layer. Mipmapping and wrapping work correctly per-layer. Zero bleeding.

```wgsl
// WGSL texture array sampling
@group(0) @binding(0) var blockTextures: texture_2d_array<f32>;
let color = textureSample(blockTextures, sampler, uv, blockTypeId);
```

**Critical for greedy meshing:** Greedy meshes produce UVs > 1.0 (tiling). Atlas UVs wrap into wrong tiles. Texture array UVs wrap correctly per-layer.

---

## 8. Sodium Architecture

**Source:** [CaffeineMC/sodium](https://github.com/CaffeineMC/sodium), [Sodium DeepWiki](https://deepwiki.com/CaffeineMC/sodium)

**5-stage pipeline:**
1. Visibility - graph-based cave culling traversal
2. Task scheduling - 4 priority levels (ZERO_FRAME_DEFER through INITIAL_BUILD)
3. Async compilation - worker thread pool for parallel meshing
4. GPU upload - StagingBuffer with per-frame upload budgets
5. Rendering - glMultiDrawElementsBaseVertex (batch all chunks in 1 call)

**GPU memory:** GlBufferArena with segment-based allocator, doubly-linked free list, best-fit allocation, auto-merge on dealloc, GPU-side compaction.

**Applicable to our WebGPU renderer:**
- Priority-based async chunk meshing with upload budgets
- Compact vertex format (we can beat Sodium's 20 bytes with 8 bytes)
- Arena-based GPU memory with compaction
- Graph-based occlusion culling
- Indirect draws as our batch mechanism (vs Sodium's glMultiDraw)
