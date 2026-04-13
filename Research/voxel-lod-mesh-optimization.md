# Voxel Engine LOD and Mesh Optimization Research

Research for AubsCraft 3D world viewer - Blazor WASM + SpawnDev.ILGPU (WebGPU).
Conducted 2026-04-13.

---

## Current State

The viewer currently uses two ILGPU compute kernels:

- **HeightmapMeshKernel** - Distant chunks rendered as top-face-only heightmap (2.5D). 256 threads (16x16 grid), one thread per column. Outputs opaque + water vertex streams.
- **MinecraftMeshKernel** - Nearby chunks with full 3D voxel mesh. One thread per block (16x16x384 = 98,304 threads per chunk). Each thread checks 6 neighbors and emits faces toward air/transparent blocks. Outputs opaque + water streams.

Both kernels output 11 floats per vertex (position.xyz + normal.xyz + color.rgb + uv.xy) with 6 vertices per face (no index buffer), using atomic counters for variable-length output.

**Key bottleneck:** Each visible block face = 6 vertices x 11 floats = 66 floats = 264 bytes. A single chunk with moderate surface area can produce tens of thousands of faces, and we need hundreds of chunks loaded simultaneously.

---

## 1. Greedy Meshing

### How It Works

Greedy meshing reduces polygon count by merging adjacent coplanar faces that share the same block type/texture into larger rectangular quads. Instead of emitting one quad per visible block face, the algorithm finds the largest possible rectangle of identical faces and emits a single quad for the entire merged region.

**Algorithm steps (per face direction, per slice):**

1. Build a 2D mask of visible faces for one slice of the chunk (e.g., all +Y faces at height y=64).
2. For each unprocessed face in the mask, greedily expand it:
   - Extend along the primary axis as far as possible while the block type matches.
   - Then extend along the secondary axis as far as every row in that direction matches.
3. Mark all cells covered by the resulting rectangle as processed.
4. Emit a single quad for the merged rectangle (with scaled UVs to tile the texture).
5. Repeat until the entire mask is processed.

This is repeated for all 6 face directions across all slices.

**Binary Greedy Meshing** is an optimized variant that uses bitwise operations on 64-bit integers to process 64 faces simultaneously. The cgerikj/binary-greedy-meshing implementation:

1. Creates a binary occupancy mask - a 64x64 array of 64-bit integers (0 = air, 1 = opaque).
2. Face culling via bitwise shift and AND - processes 64 faces at once.
3. Face merging via bitwise operations - 64 faces merged simultaneously, checking voxel type compatibility.
4. Output: 8 bytes per quad (6-bit x, 6-bit y, 6-bit z, 6-bit width, 6-bit height + voxel type).

### Performance Improvement

Vertex count reduction is dramatic and depends on terrain complexity:

| Scenario | Naive (culled) Quads | Greedy Quads | Reduction |
|----------|---------------------|--------------|-----------|
| 8x8x8 solid cube | 384 | 6 | 64x (98.4%) |
| Typical terrain chunk | ~5,000-15,000 | ~500-2,000 | 5-10x (80-95%) |
| Flat plains | ~256 | ~1 per material | 200x+ |
| Complex cave systems | ~8,000 | ~2,000-4,000 | 2-4x |

The 0fps.net analysis proves greedy meshing is within 8x of the mathematically optimal mesh - a constant-factor guarantee.

Binary greedy meshing benchmarks: **50-200 microseconds per 64x64x64 chunk** single-threaded on CPU (average 74us on Ryzen 3800x).

### Complexity to Implement

**Medium-High.** The algorithm itself is straightforward but has subtleties:
- Must handle different textures per face direction (top/side/bottom UVs in our atlas).
- Tinted blocks (grass, leaves with flag=3) need separate merge groups since tint varies.
- Water and plant blocks need their own merge passes.
- UV tiling must be scaled to the merged quad dimensions.
- Cross-chunk boundary faces need neighbor chunk data (already a limitation in our current kernel).

### GPU Compute Feasibility

**Partially feasible, with caveats.**

The current MinecraftMeshKernel's per-block parallelism (one thread per block) is fundamentally incompatible with greedy meshing because greedy meshing requires scanning across blocks sequentially to find merge regions.

Two GPU approaches:

**Approach A: Hybrid GPU face-cull + CPU greedy merge**
- GPU kernel culls invisible faces and writes a compact face list (block type + position + face direction).
- CPU (or a second compute pass) performs the greedy merge on the face list.
- This keeps the expensive neighbor-check on GPU while doing the inherently sequential merge on CPU.

**Approach B: Per-slice GPU parallelism**
- Launch one thread per 2D slice (e.g., one thread for each Y-level of each face direction = 384 * 6 = 2,304 threads per chunk).
- Each thread processes its slice independently using the binary greedy approach with bitwise ops.
- This is parallelizable because slices are independent.
- Tricky part: variable-length output per thread requires prefix-sum or atomic allocation.

Approach B maps well to ILGPU. Each thread handles a 16x16 binary mask for one slice/direction, performs bitwise greedy merge, and atomically appends quads to the output buffer.

### Notable Implementations

- [cgerikj/binary-greedy-meshing](https://github.com/cgerikj/binary-greedy-meshing) - C/C++, bitwise ops, 74us/chunk, open source
- [Vercidium/voxel-mesh-generation](https://github.com/Vercidium/voxel-mesh-generation) - C#, open source, run-based optimization
- [0fps.net meshing analysis](https://0fps.net/2012/06/30/meshing-in-a-minecraft-game/) - Foundational theory, proof of 8x optimality bound
- [roboleary/GreedyMesh](https://github.com/roboleary/GreedyMesh) - jMonkeyEngine, handles complex voxel data

---

## 2. Voxel LOD Systems

### 2a. Distance-Based LOD (What We Already Do)

Our current two-tier system (HeightmapMeshKernel for distant, MinecraftMeshKernel for nearby) is the right foundation. The research confirms this is the standard approach.

**Recommended LOD tiers for expansion:**

| Tier | Distance | Representation | Vertex Budget |
|------|----------|----------------|---------------|
| LOD 0 | 0-4 chunks | Full 3D mesh, greedy-meshed | ~1,000-3,000 quads/chunk |
| LOD 1 | 4-8 chunks | Full 3D mesh, aggressive greedy (merge across similar types) | ~500-1,500 quads/chunk |
| LOD 2 | 8-16 chunks | 2x2 block grouping (each LOD block = 2x2x2 real blocks, majority vote) | ~200-500 quads/chunk |
| LOD 3 | 16-32 chunks | Heightmap (current HeightmapMeshKernel) | ~256-512 quads/chunk |
| LOD 4 | 32+ chunks | Color-only heightmap (no texture, flat-shaded) | ~128 quads/chunk |

**Transition between tiers:** Swap the mesh buffer when a chunk crosses a distance threshold. Use hysteresis (different thresholds for upgrading vs downgrading) to prevent thrashing when the camera is near a boundary.

### 2b. Octree-Based LOD

An octree of voxel grids is the standard structure for multi-resolution voxel data. Each LOD level doubles the volume covered:

- LOD 0: 16x16x16 voxels covering 16x16x16 blocks (1:1 mapping)
- LOD 1: 16x16x16 voxels covering 32x32x32 blocks (each voxel = 2x2x2 blocks, majority-vote)
- LOD 2: 16x16x16 voxels covering 64x64x64 blocks (each voxel = 4x4x4 blocks)

This forms a natural octree - 8 LOD 0 nodes nest inside 1 LOD 1 node.

**For Minecraft specifically:** Octree LOD works well for terrain but struggles with single-block details (torches, signs, fences) that disappear at lower LODs. This is acceptable for an admin viewer where structural overview matters more than decoration fidelity.

**GPU feasibility:** The LOD reduction kernel (downsampling 2x2x2 blocks to 1 LOD voxel) is trivially parallelizable - one thread per LOD voxel, read 8 source blocks, pick the majority type. This maps perfectly to ILGPU.

### 2c. Transvoxel Algorithm

The Transvoxel algorithm solves crack-free transitions between different LOD levels. It is an extension of Marching Cubes that generates special "transition cells" at the boundary between meshes of different resolutions.

**How it works:**
- Where two LOD levels meet, transition cells sample 9 points from the high-resolution side.
- These 9 points create 512 possible cases, collapsed into 73 equivalence classes.
- Lookup tables provide triangle patterns that perfectly fill seams.

**Relevance to AubsCraft:** **Low.** Transvoxel is designed for smooth/isosurface voxel terrain (like No Man's Sky), not blocky Minecraft-style. Our blocks have hard edges by design. The LOD transitions we need are simpler - just swap between mesh detail levels. Cracks aren't visible because our blocks are opaque and axis-aligned.

**Recommendation:** Skip Transvoxel. Use simple mesh-swap LOD transitions. The heightmap LOD already handles the distant case well.

### 2d. Heightmap LOD (Already Implemented)

Our HeightmapMeshKernel is the right approach for distant chunks. Potential improvements:
- **Merge heightmap quads** across adjacent same-height, same-type columns (greedy meshing on the 2D heightmap).
- **Skip underwater seabed** at very far distances.
- **Color-only mode** for extreme distances: just colored quads, no texture atlas lookup.

### Performance Improvement

A well-tuned LOD system typically reduces total scene vertex count by 70-90% compared to rendering everything at full detail. The key insight: a chunk 32 blocks away occupies maybe 20 pixels on screen. Rendering it with 10,000 faces when 100 would be indistinguishable is pure waste.

### Complexity to Implement

**Medium.** The infrastructure (distance calculation, mesh swap) is straightforward. The hard part is building quality LOD meshes that look good at each tier and transition smoothly. Our existing two-tier system means we already have the framework - we just need to add intermediate tiers.

### GPU Compute Feasibility

**Excellent.** LOD reduction (downsampling blocks) is embarrassingly parallel. Each LOD level can be generated by a simple ILGPU kernel.

---

## 3. Chunk Mesh Caching

### How It Works

Since chunks change rarely compared to how often they're rendered, caching the generated mesh and only regenerating when blocks change is essential.

**Cache architecture:**

```
Block Data Change -> Set Dirty Flag -> Queue for Re-mesh -> GPU Kernel -> Cache New Mesh -> Render from Cache
```

**Key strategies:**

**A. Dirty Flag System**
- Each loaded chunk has a `isDirty` boolean.
- When block data changes (player builds/breaks, server push), set `isDirty = true`.
- The mesh generation loop only re-meshes dirty chunks.
- After mesh generation completes, clear the flag.

**B. Neighbor Invalidation**
- When a block at a chunk boundary changes, the neighboring chunk's mesh may also need updating (because face culling depends on neighbor block data).
- Set dirty flags on up to 6 neighboring chunks when a boundary block changes.
- Implementation: check if modified block coordinates are at x=0, x=15, z=0, z=15, y=0, or y=383.

**C. Throttled Re-meshing**
- Don't re-mesh all dirty chunks in one frame - spread the work across frames.
- Budget: 1-2 chunk re-meshes per frame (at 60fps, that's 60-120 chunks/second).
- Priority queue: re-mesh closer chunks first, as they're most visually impactful.

**D. Multi-tier Cache**
- **GPU buffer cache:** The rendered mesh stays in the WebGPU vertex buffer until invalidated.
- **OPFS/IndexedDB cache:** Serialized block data (already implemented in WorldCacheService) with version stamp.
- **Memory cache:** Keep deserialized block data in JS memory for fast re-meshing.

**E. Cache Key Design**
```
CacheKey = (chunkX, chunkZ, dataVersion)
```
Where `dataVersion` increments on any block change in that chunk.

### Performance Improvement

- **Eliminates redundant GPU compute:** Without caching, every frame re-meshes every visible chunk. With caching, only changed chunks are re-meshed. For a typical session, this eliminates 99%+ of mesh generation work.
- **Reduces CPU/GPU sync points:** No kernel dispatch means no buffer readback, no command queue flush.
- **Steady frame time:** Amortizing re-mesh across frames prevents stutter.

### Complexity to Implement

**Low-Medium.** The dirty flag system is simple. Neighbor invalidation adds modest complexity. The throttled re-mesh queue is the most work but is well-understood pattern.

### GPU Compute Feasibility

N/A - this is an orchestration pattern, not a compute task. The cached meshes are GPU buffers rendered directly.

### Notable Implementations

- [Let's Make a Voxel Engine - Chunk Management](https://sites.google.com/site/letsmakeavoxelengine/home/chunk-management) - Detailed dirty flag + async rebuild
- [Vercidium voxel world optimizations](https://vercidium.com/blog/voxel-world-optimisations/) - C#, 5.7x speedup through cache + memory optimizations

---

## 4. Instanced Rendering

### How It Works

Instead of emitting unique vertex data for every face, instanced rendering draws a template geometry (a single quad) multiple times with per-instance data (position, block type, face direction).

**Two approaches for voxels:**

**A. Traditional Instancing**
- Define one quad as the template geometry (4 vertices).
- Per-instance buffer contains: chunk-local position (3 bytes), face direction (3 bits), block type (1 byte) = ~4-5 bytes per instance.
- GPU transforms each instance to world space using instance data.
- One draw call per chunk (or even per face-direction group within a chunk).

**B. Vertex Pulling (preferred for WebGPU)**
- Store face data in a storage buffer (SSBO) - just 4-8 bytes per face.
- Vertex shader uses `vertex_index` to calculate which face to read and which vertex of that face to generate.
- No vertex buffer at all - the shader generates vertex positions from the compact face data.
- WebGPU supports read-only storage buffer access in vertex shaders.

**Vertex pulling memory comparison for one face:**
| Method | Bytes per Face |
|--------|---------------|
| Current (6 verts x 11 floats x 4 bytes) | 264 bytes |
| Indexed (4 verts x 11 floats x 4 bytes + 6 indices x 2 bytes) | 188 bytes |
| Instanced (4 template verts + 8 byte instance) | ~8 bytes per face |
| Vertex pulling (storage buffer) | 4-8 bytes per face |

### Performance Improvement

- **Memory reduction:** 33x-66x less GPU memory per face compared to our current format.
- **Draw call reduction:** One indirect draw call per chunk instead of one draw per face.
- **Bandwidth reduction:** Far less data transferred GPU-side per frame.

**However, there are tradeoffs:**
- Vertex pulling adds ALU work in the vertex shader to unpack positions.
- For voxels with many different textures per face, the per-instance data grows.
- Instancing works best when many faces share identical geometry - which is exactly what greedy meshing produces.

### Complexity to Implement

**Medium-High.** Requires:
- New vertex shader that generates geometry from packed face data.
- New output format from the mesh kernel (compact face descriptors instead of full vertices).
- Indirect draw buffer management.
- Interaction with the two-pass opaque/transparent pipeline.

### GPU Compute Feasibility

**Excellent.** The mesh kernel would actually become simpler - instead of writing 66 floats per face, it writes 4-8 bytes. The vertex shader does the unpacking. This is a well-known pattern in modern voxel renderers.

### WebGPU Specifics

WebGPU indirect draws have a critical best practice: **put all indirect draw arguments in a single GPUBuffer.** Testing showed that separate buffers caused 50% of render time to be spent on validation overhead (3ms out of 6ms), while a single combined buffer reduced this to 10 microseconds - a 300x improvement. (Source: toji.dev WebGPU best practices)

Render bundles can be combined with indirect draws for further CPU-side savings when the scene is relatively stable (which cached chunk meshes are).

### Notable Implementations

- [Vertex Pulling - Voxel.Wiki](https://voxel.wiki/wiki/vertex-pulling/) - 4 bytes per face, SSBO-based
- [Nick's Voxel Blog - Vertex Pooling](https://nickmcd.me/2021/04/04/high-performance-voxel-engine/) - C++, indirect multi-draw, 30-300us/chunk
- [toji.dev - WebGPU Indirect Draws](https://toji.dev/webgpu-best-practices/indirect-draws.html) - Best practices for WebGPU
- [VkGuide - Ascendant Geometry](https://vkguide.dev/docs/ascendant/ascendant_geometry/) - Gigabuffer + indirect draw, 12-byte packed vertices

---

## 5. Mesh Simplification Beyond Greedy Meshing

### 5a. Run-Length Merging (Simpler Than Greedy)

Instead of full 2D greedy meshing, merge faces only along one axis (runs). The Vercidium engine uses this:
- Scan each row of faces along X.
- Merge consecutive same-type faces into a single wide quad.
- Simpler than 2D greedy but captures most of the benefit for typical terrain.

**Reduction:** ~60-80% vertex reduction (vs 80-95% for full greedy).
**Speed:** Faster than full greedy - no 2D rectangle search.
**GPU feasibility:** Excellent - each row is independent, trivially parallelizable.

### 5b. Monotone Polygon Meshing

An alternative to greedy that sweeps columns instead of rectangles:
- Process each column of the 2D face mask.
- Build "monotone polygon chains" from contiguous set bits.
- Triangulate the resulting polygons.

**Reduction:** Similar to greedy for most terrain (within 2x).
**Complexity:** Slightly less than greedy, but produces non-rectangular polygons that complicate UV mapping for textured blocks.

### 5c. Block-Type Grouping at Lower LODs

For distant chunks, merge visually similar blocks:
- Stone, cobblestone, andesite, granite, diorite -> "gray stone"
- Oak log, birch log, spruce log -> "wood"
- All ores -> their base stone type

This increases greedy merge opportunities dramatically because adjacent "different" blocks become "same" at lower LODs.

**Reduction:** Additional 2-5x on top of greedy meshing for LOD 1+.
**Complexity:** Low - just a block-type remapping table.
**GPU feasibility:** Trivial - lookup table in a small buffer.

### 5d. Quadric Error Metrics (Mesh Decimation)

Traditional mesh simplification from 3D graphics: iteratively collapse the least-important edges based on a quadric error metric.

**For Minecraft:** **Not recommended.** Quadric decimation is designed for smooth meshes with curved surfaces. It would destroy the blocky aesthetic and is far more complex than voxel-specific approaches. The block-type grouping + greedy meshing combination achieves better results for voxel data.

### 5e. Compact Vertex Format

Not mesh simplification per se, but reduces memory/bandwidth significantly:

**Current format:** 11 floats x 4 bytes = 44 bytes per vertex, 264 bytes per face.

**Packed format (4 bytes per vertex):**
```
Bits 0-4:   X position (0-31, chunk-local)
Bits 5-9:   Z position (0-31)
Bits 10-18: Y position (0-511)
Bits 19-21: Face direction (0-5)
Bits 22-29: Block type / palette index (0-255)
Bits 30-31: Reserved / AO
```

Normal is implicit from face direction (6 possible values). Color comes from palette lookup. UVs come from atlas lookup + vertex corner (derivable from face direction and vertex_index % 4).

**Reduction:** 44 bytes -> 4 bytes per vertex = 11x memory reduction. With vertex pulling (one uint32 per face, 4 vertices generated in shader), this drops to 4 bytes per face - a 66x reduction from current.

**GPU feasibility:** This is fundamentally a shader technique. The compute kernel packs the data; the vertex shader unpacks it. Both are straightforward in ILGPU/WGSL.

### Notable Implementations

- [Vercidium/voxel-mesh-generation](https://github.com/Vercidium/voxel-mesh-generation) - C# run-length merging, open source
- [Vercidium blog](https://vercidium.com/blog/voxel-world-optimisations/) - Packed vertex format (position + texture + health + normal in one uint)

---

## 6. Additional Optimizations (Bonus Research)

### 6a. Frustum Culling

Only submit draw calls for chunks within the camera's view frustum. Standard technique, should be implemented early.

- Test each chunk's axis-aligned bounding box against 6 frustum planes.
- Eliminates ~50-75% of chunks from the draw call list (depending on FOV).
- **GPU feasibility:** Can run as an ILGPU compute kernel (one thread per chunk, output an indirect draw list).
- **Complexity:** Low. Well-documented algorithm.

### 6b. Occlusion Culling

Skip chunks that are completely hidden behind other chunks (e.g., chunks on the far side of a mountain).

- **Hi-Z approach:** Build a depth pyramid from the previous frame, test chunk bounding boxes against it.
- Eliminates additional 10-30% of chunks beyond frustum culling.
- **GPU feasibility:** Compute shader reads depth pyramid, writes indirect draw args. Well-suited to ILGPU.
- **Complexity:** Medium-High. Requires depth pyramid generation and temporal reprojection.

### 6c. Empty Chunk Skip

If a chunk's block data is entirely air (or entirely underground with no exposed surfaces), skip it entirely. A simple flag set during chunk load or modification.

- **Complexity:** Trivial. Check during data load.
- **Savings:** Eliminates GPU dispatch and draw calls for empty chunks. Significant in the sky and deep underground.

### 6d. Sub-Chunk Sectioning

Instead of one mesh per 16x384x16 chunk, split into 16x16x16 sections (24 sections per chunk). Benefits:
- Finer-grained frustum culling (skip underground sections when looking at the sky).
- Smaller mesh buffers - only the section that changed needs re-meshing.
- Better LOD granularity.
- Matches Minecraft's internal section format.

**Tradeoff:** More draw calls (24x per chunk). Mitigated by indirect multi-draw.

---

## Recommended Implementation Priority

Based on impact vs. effort for the AubsCraft viewer running in WebGPU via ILGPU:

### Phase 1 - Quick Wins (High Impact, Low Effort)
1. **Chunk mesh caching with dirty flags** - Stop re-meshing unchanged chunks. Biggest single performance win for the least code.
2. **Frustum culling** - Skip chunks outside the camera view. ~50-75% draw call reduction.
3. **Empty chunk skip** - Trivial check, significant savings.

### Phase 2 - Major Optimization (High Impact, Medium Effort)
4. **Greedy meshing** (per-slice GPU variant) - 80-95% vertex count reduction. Implement as a new kernel that processes one slice per thread.
5. **Compact vertex format** (packed uint32) - 11x-66x memory reduction. Requires new vertex shader.
6. **Additional LOD tiers** - Add LOD 1 (simplified 3D) and LOD 2 (2x2 grouped) between current full-detail and heightmap.

### Phase 3 - Advanced (Medium Impact, Higher Effort)
7. **Vertex pulling with indirect draws** - Combine with greedy meshing for maximum efficiency. Single draw call per chunk.
8. **Sub-chunk sectioning** - Finer culling granularity, smaller re-mesh units.
9. **Block-type grouping for distant LODs** - Merge similar materials for better greedy merge at distance.
10. **GPU frustum/occlusion culling** - Move culling to compute shaders for truly GPU-driven rendering.

### Not Recommended
- **Transvoxel** - Designed for smooth terrain, not blocky Minecraft. Unnecessary complexity.
- **Quadric mesh decimation** - Wrong tool for voxel geometry.
- **Full octree LOD** - Overkill for an admin viewer. Distance-based tiers are sufficient.
- **Monotone polygon meshing** - Complicates UV mapping without meaningful benefit over greedy.

---

## Key Numbers to Remember

| Metric | Current | After Phase 1 | After Phase 2 | After Phase 3 |
|--------|---------|---------------|---------------|---------------|
| Bytes per face | 264 | 264 | 4-8 | 4-8 |
| Faces per chunk (typical) | 5,000-15,000 | 5,000-15,000 | 500-2,000 | 500-2,000 |
| Re-mesh rate | Every frame | Only dirty | Only dirty | Only dirty |
| Chunks drawn (of 400 visible) | 400 | 150-200 | 150-200 | 100-150 |
| Memory per chunk mesh | ~1-4 MB | ~1-4 MB | ~2-16 KB | ~2-16 KB |
| Draw calls | 1 per chunk | 1 per chunk | 1 per chunk | 1 total (indirect) |

---

## Sources

### Greedy Meshing
- [Meshing in a Minecraft Game - 0fps.net](https://0fps.net/2012/06/30/meshing-in-a-minecraft-game/) - Foundational analysis, optimality proof
- [Binary Greedy Meshing - cgerikj](https://github.com/cgerikj/binary-greedy-meshing) - Bitwise greedy, 74us/chunk, C implementation
- [Vercidium Voxel Mesh Generation](https://github.com/Vercidium/voxel-mesh-generation) - C# run-based optimization
- [Vercidium Blog - Voxel World Optimisations](https://vercidium.com/blog/voxel-world-optimisations/) - C# memory/meshing optimizations, 5.7x speedup
- [Binary Greedy Meshing - Hacker News Discussion](https://news.ycombinator.com/item?id=40087213)
- [Transparency with Binary Greedy Meshing - EngineersBox](https://engineersbox.github.io/website/2024/09/19/transparency-with-binary-greedy-meshing.html)

### LOD Systems
- [Transvoxel Algorithm](https://transvoxel.org/) - Crack-free LOD transitions for smooth voxel terrain
- [Distant Horizons Mod](https://www.curseforge.com/minecraft/mc-mods/distant-horizons) - Minecraft LOD mod, 512 chunk render distance
- [Voxel Engine Octree vs Grid](https://riscadoa.com/game-dev/voxel-engine-1/) - Octree trade-offs for voxel engines
- [NVIDIA Sparse Voxel Octrees](https://research.nvidia.com/sites/default/files/pubs/2010-02_Efficient-Sparse-Voxel/laine2010tr1_paper.pdf) - Efficient GPU octree traversal

### GPU-Driven Rendering
- [Aokana: GPU-Driven Voxel Framework](https://arxiv.org/html/2505.02017v1) - 6ms/frame for 10B+ voxels, SVDAG, Hi-Z culling
- [VkGuide - Ascendant Geometry](https://vkguide.dev/docs/ascendant/ascendant_geometry/) - Gigabuffer, indirect draw, 12-byte vertices
- [Nick's Blog - Vertex Pooling](https://nickmcd.me/2021/04/04/high-performance-voxel-engine/) - Multi-draw indirect, vertex pool
- [WebGPU Marching Cubes](https://www.willusher.io/graphics/2024/04/22/webgpu-marching-cubes/) - WGSL compute mesh gen, stream compaction

### WebGPU Optimization
- [WebGPU Indirect Draws - toji.dev](https://toji.dev/webgpu-best-practices/indirect-draws.html) - Critical: single buffer = 300x validation speedup
- [WebGPU Render Bundles - toji.dev](https://toji.dev/webgpu-best-practices/render-bundles.html) - Pre-recorded command bundles
- [WebGPU Optimization - webgpufundamentals.org](https://webgpufundamentals.org/webgpu/lessons/webgpu-optimization.html) - General WebGPU performance tips
- [Vertex Pulling - Voxel.Wiki](https://voxel.wiki/wiki/vertex-pulling/) - 4 bytes/face, SSBO-based rendering

### Chunk Management
- [Let's Make a Voxel Engine - Chunk Management](https://sites.google.com/site/letsmakeavoxelengine/home/chunk-management) - Dirty flags, async rebuild
- [Let's Make a Voxel Engine - Chunk Optimizations](https://sites.google.com/site/letsmakeavoxelengine/home/chunk-optimizations) - Empty chunk skip, visibility
- [Voxel Performance: Instancing vs Chunking](https://medium.com/@claygarrett/voxel-performance-instancing-vs-chunking-9643d776c11d) - Trade-off analysis

### Culling
- [Let's Make a Voxel Engine - Frustum Culling](https://sites.google.com/site/letsmakeavoxelengine/home/frustum-culling) - Implementation guide
- [Exile Voxel Rendering Pipeline](https://thenumb.at/Voxel-Meshing-in-Exile/) - Instanced rendering, greedy meshing
- [GPU-Driven Rendering Overview - VkGuide](https://vkguide.dev/docs/gpudriven/gpu_driven_engines/) - Compute-based culling
