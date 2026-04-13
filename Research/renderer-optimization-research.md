# High-Performance Minecraft/Voxel Renderer Research

**Date:** 2026-04-13
**Purpose:** Research into rendering optimization techniques from Sodium, Distant Horizons, Prismarine, and web voxel engines - adapted for our WebGPU/ILGPU browser-based Minecraft viewer.

---

## 1. Sodium (CaffeineMC) - The Gold Standard

Sodium is the fastest Minecraft rendering optimizer, rewriting the entire chunk rendering pipeline. Here is how it works internally.

### 1.1 Chunk Compilation Pipeline

Sodium uses **asynchronous chunk compilation** through a worker thread pool managed by `ChunkBuilder`. Tasks are scheduled via `ChunkJobQueue` with four priority levels:

| Priority | Name | Use Case |
|----------|------|----------|
| 0 | ZERO_FRAME_DEFER | Urgent - visible chunk needs immediate mesh |
| 1 | ONE_FRAME_DEFER | Important - chunk entering view soon |
| 2 | ALWAYS_DEFER | Normal background meshing |
| 3 | INITIAL_BUILD | Lowest - initial world load |

Mesh compilation occurs in worker threads using `ChunkBuilderMeshingTask`, which generates vertex data through multiple stages:
- `BlockRenderer` processes block models into quads
- `DefaultFluidRenderer` creates fluid geometry with face merging
- `LevelColorCache` applies biome-based color tinting
- Light pipeline calculates smooth lighting and ambient occlusion
- `TranslucentGeometryCollector` implements distance-based sorting for transparent blocks

Thread safety is achieved through **immutable snapshots** (`ClonedChunkSection`) - the worker reads a frozen copy of world data, never the live state.

### 1.2 Compact Vertex Format

Sodium uses a **20-byte vertex format** (`CompactChunkVertex`):

| Attribute | Format | Size |
|-----------|--------|------|
| Position | u20x3 (quantized XYZ) | ~8 bytes |
| Color | RGBA | 4 bytes |
| Texture UV | u15x2 (quantized) | ~4 bytes |
| Light | Block + sky values | 4 bytes |

This is a 40% memory bandwidth reduction compared to vanilla Minecraft's vertex format. Quantized positions use 20 bits per axis (enough for sub-block precision within a region).

**Applicability to AubsCraft:** Our current format is 11 floats (44 bytes) per vertex. Switching to a packed format could cut vertex buffer size by 50-60%. WebGPU supports u32 vertex attributes that we can unpack in the vertex shader. This is a major optimization target.

### 1.3 Region-Based Rendering

Chunks are organized into `RenderRegion` objects representing **256x256x256 block areas** (8x4x8 chunk sections). Benefits:

- **Fewer draw calls** - chunks within a region are batched into a single draw
- **Efficient GPU buffer management** - each region maintains one `GlBufferArena`
- **Spatial locality** - vertices from nearby chunks are contiguous in memory
- **Selective updates** - only changed regions need buffer re-uploads

Each region's `GlBufferArena` uses **best-fit allocation** with a doubly-linked free list, automatic merging of adjacent free segments, and buffer compaction via copy operations.

**Applicability to AubsCraft:** We should group chunks into regions for batched rendering. With our heightmap + full 3D hybrid approach, regions could be 8x8 chunks (128x128 blocks) for heightmap data and 4x4 chunks for full 3D data.

### 1.4 Draw Call Batching

Sodium renders using `glMultiDrawElementsBaseVertex` - a single API call that issues multiple draw commands from one buffer. Each `MultiDrawBatch` accumulates geometry from multiple chunk sections.

**WebGPU equivalent:** `drawIndirect` with a consolidated indirect buffer. Per Toji's best practices research, using a **single indirect buffer** for all draw commands is critical - Chrome's Dawn backend validates at the buffer level, and 412 draws from separate buffers cost 3ms in validation overhead, while the same 412 draws from one buffer cost 10 microseconds. That is a **300x difference**.

### 1.5 Culling Techniques

Sodium implements three culling layers:

1. **Occlusion Culling** - Graph-based approach (see Section 6 for the Advanced Cave Culling Algorithm that originated in Bedrock and was backported to Java)
2. **Frustum Culling** - Standard camera view frustum test
3. **Fog Occlusion** - Chunks fully obscured by distance fog are skipped

### 1.6 Upload Budget System

Sodium limits GPU uploads per frame using duration budgets based on average frame times. `JobDurationEstimator` and `MeshTaskSizeEstimator` predict how long each upload will take and throttle to avoid frame spikes.

**Applicability to AubsCraft:** We need a per-frame upload budget. Currently chunks can cause frame spikes when many mesh at once. The WebGPU Voxel Engine example caps at 512 chunk mesh uploads per frame.

**Performance impact:** Sodium delivers 2-5x FPS improvement over vanilla Minecraft through reduced CPU-GPU communication, aggressive culling, async building, and batched rendering.

**Complexity to implement:** Medium-high. The region system and compact vertex format are the highest-value targets. Multi-draw indirect is straightforward in WebGPU.

---

## 2. Distant Horizons - LOD for Extreme Draw Distance

Distant Horizons renders simplified chunks beyond the normal render distance, allowing 128-256 chunk render distances on mid-tier hardware.

### 2.1 LOD Level Structure

The mod uses a **quadtree-based LOD system** for terrain simplification:

- **LOD 0:** Full block detail (standard Minecraft resolution)
- **LOD 1:** 2x2 blocks merged into one representative block
- **LOD 2:** 4x4 blocks merged
- **LOD N:** 2^N x 2^N blocks merged

At each level, the representative block is chosen by majority vote or height average of the source blocks. Colors are averaged from source voxels.

### 2.2 LOD Transitions

Distant Horizons handles transitions by:
- **Asynchronous rendering** - far terrain processes on a separate thread to avoid frame drops
- **Progressive loading** - entering a new area shows low-detail models first, upgrading as LOD data finishes building
- **Distance-based selection** - LOD level is determined by distance from camera, with smooth blending at boundaries

### 2.3 Data Storage

Storage is efficient because the mod does not pre-generate every chunk at every LOD level. Instead:
- LOD data is built on-demand from chunks the player has already visited
- Column-based storage (vertical columns of block data)
- Simplified meshes use far fewer vertices than full block meshes

### 2.4 The LOD Error Formula

From the Aokana research paper, a general LOD error formula:

```
LODError = (ChunkSize * StreamingFactor) - distance(ChunkCenter, CameraPos)
```

When LODError > 0, further subdivision is needed. When < 0, the current LOD is sufficient. The `StreamingFactor` controls quality vs. memory tradeoff.

**Applicability to AubsCraft:** This maps directly to our existing hybrid approach - heightmap for distant chunks, full 3D for nearby. We should formalize this into a proper LOD system:
- LOD 0: Full 3D voxel mesh (MinecraftMeshKernel) - near camera
- LOD 1: Simplified heightmap (HeightmapMeshKernel) - mid distance
- LOD 2: Ultra-simplified heightmap (4x4 merged) - far distance
- LOD 3: Single-color flat quads - extreme distance

**Performance impact:** Allows 128-256 chunk render distance without FPS loss on mid-tier hardware. Memory savings are proportional to LOD level squared.

**Complexity to implement:** Medium. We already have the heightmap/full-3D split. Formalizing LOD levels and adding smooth transitions is incremental work.

---

## 3. Prismarine Viewer / Mineflayer - Web-Based Reference

Prismarine Viewer is the most mature web-based Minecraft renderer, built on Three.js.

### 3.1 Architecture

- **Three.js renderer** - standard WebGL-based 3D rendering
- **Worker threads** for chunk meshing - mesh generation happens off the main thread
- **WebSocket proxy** - connects browser to MC servers via WS-to-TCP translation
- **Chunk-based loading** with dirty-section tracking

### 3.2 Optimizations Used

- **Web Workers for meshing** - keeps main thread free for rendering
- **Dirty section tracking** - only re-meshes chunks that changed
- **Distance-based chunk loading** - standard radial loading from camera
- **Three.js buffer geometry** - pre-built geometry objects for blocks

### 3.3 Limitations (What We Can Do Better)

Prismarine uses Three.js (WebGL), which means:
- No compute shaders for mesh generation
- No indirect draw support
- No GPU-driven culling
- CPU-bound meshing is the bottleneck
- Limited draw distance compared to native

**Applicability to AubsCraft:** Prismarine validates the web-based approach but our WebGPU + ILGPU stack is fundamentally more capable. We have compute shaders, indirect draws, and GPU meshing - none of which Prismarine can access via Three.js/WebGL.

---

## 4. Web-Based Voxel Engines

### 4.1 WebGPU Voxel Engine (rowannadon)

A modern WebGPU voxel engine with sophisticated rendering:

**Architecture:**
- **Dual-thread model** - main thread renders at 120 FPS, worker thread generates world at 50 Hz
- **9-pass rendering pipeline:** 5 compute passes (atmospheric LUTs) + 4 render passes (shadow, voxel, sky, post-process)
- **Indirect rendering** - `DrawIndirectCommand` for batch chunk rendering
- **Resource pooling** - 18,000 pre-allocated buffer slots, 768x768x768 3D texture pool

**Key Techniques:**
- **Greedy meshing** with material ID embedding
- **Frustum culling** at chunk level
- **LOD support** via `ChunkData` LOD fields
- **Frame upload limiting** - max 512 chunk meshes per frame to prevent spikes
- **PBR materials** - albedo, metallic, roughness, subsurface scattering
- **Precomputed atmospheric LUTs** - transmittance, multi-scattering, sky view, aerial perspective

**Performance:** Adaptive frame rate targeting monitor refresh (typically 120 FPS). Hybrid sleep + spin-wait for microsecond-accurate frame timing.

### 4.2 noa-engine (fenomas)

Browser voxel engine built on Babylon.js:

- **32x32x32 chunk size** (vs Minecraft's 16x16x384)
- **Greedy meshing** - scans voxel slices, merges adjacent faces of same type
- **Configurable chunk load distance** with separate horizontal/vertical values
- **Trade-off acknowledged:** faster meshing = more draw calls

### 4.3 voxel.js / voxel-engine

Older browser voxel engine (Three.js based):
- Per-chunk mesh objects
- Simple face culling (skip faces between adjacent solid blocks)
- No advanced optimization
- Useful as a baseline for "minimum viable voxel renderer"

### 4.4 Common Pitfalls in Browser Voxel Rendering

| Pitfall | Solution |
|---------|----------|
| Too many draw calls | Batch chunks into regions, use multi-draw indirect |
| Main thread blocking | Move meshing to Web Workers |
| Memory fragmentation | Pre-allocated buffer pools with fixed-size buckets |
| Frame spikes on chunk load | Upload budget limiting (N chunks per frame) |
| Garbage collection pauses | Reuse buffers, avoid allocations in hot paths |
| Texture atlas bleeding | Half-pixel UV insets or array textures |
| WebGPU buffer validation | Consolidate indirect buffers (300x faster) |

**Complexity to implement:** Low-medium. Most of these are architectural patterns we can adopt incrementally.

---

## 5. Mesh Generation Optimization

### 5.1 Meshing Strategy Comparison

From the classic 0fps.net analysis and binary greedy meshing research:

| Strategy | Quads (8x8x8 solid) | Quads (sphere) | Time/Chunk | Complexity |
|----------|---------------------|----------------|------------|------------|
| Naive (6 faces per block) | 3,072 | ~4,770 | Fastest | Trivial |
| Culled (skip interior faces) | 384 | ~2,100 | Fast | Easy |
| Greedy (merge coplanar) | 6 | ~1,670 | Slower | Medium |
| Binary Greedy | 6 | ~1,670 | 74us avg | Medium-hard |

### 5.2 Binary Greedy Meshing

The state-of-the-art for voxel meshing. Algorithm in three steps:

**Step 1 - Binary Occupancy Mask:**
Create a 64x64 array of 64-bit integers where each bit represents a voxel (0=air, 1=solid). This mask is reusable for physics and raycasting.

**Step 2 - Face Culling via Bitwise Operations:**
Generate a 62x62 array of 64-bit masks for each of the 6 faces. Each bit represents face visibility. **Processes 64 faces simultaneously** using bitwise AND/OR/XOR/SHIFT operations.

**Step 3 - Greedy Face Merging:**
Merge adjacent same-type faces into larger quads using bitwise operations. Checks voxel type compatibility during merge.

**Output format:** 8 bytes per quad:
- First 32 bits: 6-bit x, 6-bit y, 6-bit z, 6-bit width, 6-bit height
- Second 32 bits: 8-bit voxel type data

**Performance:** 74 microseconds per chunk (single-threaded, Ryzen 3800X). Range: 50-200us depending on chunk contents.

**Rendering:** All chunks rendered in one `glMultiDrawElementsIndirect` call with backface culling.

### 5.3 Vertex Pooling (Nick McDonald)

A GPU buffer management strategy for voxel worlds:

- **Persistent mapped VBO** divided into fixed-size buckets
- **FIFO free list** for bucket allocation without synchronization
- **Indirect draw commands** (DAIC) per bucket
- **Six buckets per chunk** (one per face orientation) - enables per-face backface culling via masking

**Performance gains:**
- Static rendering: 45-58% improvement over naive approach
- Dynamic meshing: 24-41% faster
- Face masking (backface culling by orientation): 8.4ms down to 6.5ms

**Key insight:** Over-allocating bucket size has zero measurable performance impact. Using a single generous bucket size eliminates complexity without cost.

### 5.4 Run-Based Merging (Vercidium)

A simpler alternative to greedy meshing:
- Blocks combine into runs along X and Y axes
- Runs remain unbroken when covered by blocks above
- Split only for different textures or block states

**Optimization techniques that yielded 5.7x speedup:**
1. Single-dimensional arrays (20% gain) - enables compiler bitshift optimization
2. Direct buffer writing (20% gain) - eliminate intermediate arrays
3. Neighbor chunk caching (10% gain) - avoid cross-chunk lookups
4. GC reduction (10% gain) - Array.Clear instead of realloc
5. Pre-calculated values (5% gain) - avoid redundant math
6. Aggressive inlining (3% gain)

**Applicability to AubsCraft:** Binary greedy meshing is the target for our ILGPU kernels. The bitwise operations map perfectly to GPU compute - 64-bit operations on 64 voxels simultaneously is the kind of parallelism GPUs excel at. The vertex pooling pattern maps to our existing WebGPU buffer management. Run-based merging micro-optimizations apply to any meshing kernel.

---

## 6. Occlusion Culling

### 6.1 The Advanced Cave Culling Algorithm

Originally developed for Minecraft Bedrock by Tommaso Checchi, later backported to Java Edition. This is the single most impactful culling technique for Minecraft-style worlds.

**How it works:**

Each chunk stores a **15-bit connectivity matrix** encoding which of the 6 chunk faces can see which other faces through empty space.

**Construction (per chunk, on block change):**
1. Start from each non-opaque block
2. Run 3D flood fill through empty space
3. Every time the flood fill crosses a chunk boundary face, record that face
4. All reached faces are marked as mutually visible in the connectivity matrix

**Runtime traversal (per frame):**
1. Start BFS from the chunk containing the camera
2. For each chunk, check: "If I entered through face A, which faces can I exit through?"
3. Only traverse to neighbors reachable through the connectivity graph
4. Chunks unreachable from the camera's chunk are culled

**Performance:** Culling ratios of **50% to 99%** of geometry. Underground scenes cull up to 99% of the surface world. Surface scenes with flat terrain cull 50-60% of underground geometry.

**Cost:** 0.1-0.2ms per chunk update, handled asynchronously on background threads. The 15-bit matrix is tiny - no measurable memory overhead.

**Applicability to AubsCraft:** This is extremely relevant for full 3D chunk rendering. When we load underground chunks, we need this to avoid rendering caves visible from the surface and surface visible from caves. For heightmap-only rendering this is less critical since we only render the top surface.

**Complexity:** Medium. The flood fill is straightforward. The BFS traversal integrates into existing chunk visibility determination. The connectivity matrix is only 15 bits per chunk - trivial storage.

### 6.2 Hierarchical Z-Buffer (Hi-Z) Occlusion Culling

GPU-driven occlusion culling using the depth buffer from the previous frame.

**Pipeline:**
1. **Build Hi-Z mipchain** - downsample depth buffer, each level stores max depth of 2x2 texel quad
2. **Cull pass (compute shader)** - for each chunk:
   a. Project bounding box to screen space
   b. Calculate screen-space AABB dimensions
   c. Select mipmap level where AABB is 2x2 pixels: `level = floor(log2(max(width, height)))`
   d. Sample 4 texels from Hi-Z at that level
   e. If chunk's near depth > all 4 sampled depths, chunk is occluded - skip it
3. **Write indirect draw buffer** - visible chunks get their draw commands written; occluded chunks get instanceCount=0

**Two-pass refinement (Aokana):**
- First pass: cull against previous frame's Hi-Z
- Second pass: build current frame's Hi-Z from visible chunks, then re-test previously culled chunks to catch false negatives

**Performance (from VTK WebGPU implementation):**
- 155M triangles: 1.5x speedup
- 624M triangles: 1.6x speedup
- Combined with frustum culling: 5-6x total speedup
- Overhead is significant below 100M triangles - frustum culling alone is faster for smaller scenes

**WebGPU considerations:**
- Hi-Z mipchain requires compute shader passes with synchronization between each level
- At 1280x720, mipchain computation is ~0.5ms
- Must be weighed against scene complexity - only beneficial for large worlds with significant occlusion

**Applicability to AubsCraft:** Valuable for Phase Z (full 3D world) but potentially overkill for current heightmap rendering. Worth implementing when we have full underground rendering where terrain occludes significant geometry. The compute shader pipeline is a natural fit for ILGPU.

**Complexity:** High. Requires depth texture readback to compute shader, mipchain generation, and two-pass rendering. But the pattern is well-documented.

### 6.3 Frustum Culling (GPU-Driven)

Standard frustum culling moved to GPU compute shader:

**Compute shader approach:**
1. Extract 6 frustum planes from view-projection matrix
2. For each chunk, test bounding sphere against all planes
3. Write visibility result to indirect draw buffer

**Buffer layout for indirect draws:**
```
struct DrawIndirectArgs {
    vertexCount: u32,
    instanceCount: u32,  // set to 0 for culled, 1 for visible
    firstVertex: u32,
    firstInstance: u32,
}
```

**Critical WebGPU optimization:** All draw args must be in a **single consolidated buffer**. Chrome/Dawn validates at the buffer level - separate buffers per draw add 300x overhead.

**Applicability to AubsCraft:** We already have CPU frustum culling. Moving it to a compute shader eliminates CPU work and keeps the entire cull-to-draw pipeline on the GPU. This is a natural next step.

**Complexity:** Low-medium. The frustum test is simple math. The main work is buffer management.

### 6.4 Portal Culling

Treats chunk boundaries as "portals" connecting "rooms":
- Only render geometry in the current room and rooms visible through portals
- Portals are the openings between chunks (from the cave culling connectivity data)
- Combine with frustum culling to narrow portal visibility

**Applicability:** Useful for interior/cave scenes. Lower priority than cave culling for our use case.

### 6.5 Distance Culling

Simply skip chunks beyond a maximum distance. Enhanced variants:
- **Fog-based cutoff** - cull at fog distance since fog-obscured geometry is invisible anyway
- **LOD-aware distance** - different cutoffs per LOD level
- **View-direction weighted** - larger distance in the direction the camera faces, shorter behind

---

## 7. Loading Priority Strategies

### 7.1 Radial Loading (Current Approach)

Load chunks in order of distance from camera position, nearest first.

**Minecraft's implementation:** Chunks load in a taxicab-distance circle with radius = simulation distance + 1. Cardinal direction edges are clipped to simulation distance exactly.

**Improvement: Circular vs. Square loading.** Circular loading reduces loaded chunk count by ~25% compared to square loading with the same perceived draw distance.

### 7.2 Camera Direction Weighting

Prioritize chunks in the direction the camera is facing:

```
priority = distance * (1.0 - dot(normalize(chunkDir), cameraForward) * directionBias)
```

Where `directionBias` controls how strongly facing direction affects priority (0.0 = pure radial, 1.0 = strong direction bias).

**Rationale:** Players look forward. Loading what they see first creates the perception of faster loading even if total throughput is identical.

**Implementation:** Use a priority queue sorted by this weighted distance. Re-sort when camera direction changes significantly (>30 degrees).

### 7.3 Hybrid Heightmap + Full 3D Progressive Loading

This is the strategy most relevant to AubsCraft:

1. **Immediate (frame 0):** Render heightmap for all cached chunks - instant world appearance
2. **Progressive near (frames 1-N):** Replace nearest heightmap chunks with full 3D mesh, spiraling outward
3. **Background far:** Build simplified LOD meshes for distant chunks
4. **Idle upgrade:** When GPU is idle, upgrade mid-distance heightmap chunks to higher-detail LOD

**Priority queue ordering:**
```
1. Visible + near + camera-facing (full 3D mesh, highest priority)
2. Visible + near + not camera-facing (full 3D mesh)
3. Visible + mid-distance (heightmap LOD 1)
4. Visible + far (heightmap LOD 2)
5. Not visible but near (full 3D mesh - will become visible on camera turn)
6. Everything else (lowest priority)
```

### 7.4 Hybrid Priority Formula

Combining distance, direction, visibility, and LOD:

```
score = baseDistance
      - (dot(normalize(chunkDir), cameraForward) * 0.3 * maxDistance)  // direction bonus
      + (isInFrustum ? 0 : maxDistance * 0.5)                         // behind camera penalty
      + (hasCache ? 0 : 10)                                           // uncached penalty

lodLevel = floor(score / lodTransitionDistance)
```

### 7.5 Streaming/Unloading Strategy

From Aokana's LOD streaming system:
- When camera moves to a new chunk, recursively evaluate the LOD octree
- Higher LOD chunks load first (they cover more area with less data)
- Child chunks can be unloaded when parent LOD is sufficient
- Closer chunks always load before farther ones at the same LOD level
- Only ~5% of complete scene data needs to be in VRAM at any time

**Applicability to AubsCraft:** The hybrid heightmap/3D approach is already our architecture. Formalizing it with a proper priority queue and LOD transitions would make loading feel much smoother. The camera-direction weighting is low-hanging fruit.

**Complexity:** Low. Priority queue with weighted scoring is straightforward. The LOD streaming is medium complexity.

---

## 8. GPU-Driven Rendering Pipeline (Aokana Framework)

Aokana represents the state of the art for GPU-driven voxel rendering. While it uses Sparse Voxel DAGs (different from our mesh approach), several concepts transfer directly.

### 8.1 Four-Pass Compute Pipeline

Inserted between opaque and transparent rendering:

1. **Chunk Selection** - Frustum cull chunk bounding boxes
2. **Tile Selection** - Divide screen into 8x8 pixel tiles, find contributing chunks per tile, Hi-Z occlusion cull
3. **Ray Marching** - Indirect dispatch on tile-chunk pairs, write to 64-bit visibility buffer
4. **Hi-Z Build** - Construct current frame's hierarchical depth, re-test previously culled tiles

### 8.2 Visibility Buffer Format

64-bit per pixel:
- Upper 24 bits: depth (inverted)
- 3 bits: surface normal (axis-aligned)
- 13 bits: chunk ID
- 24 bits: voxel XYZ coordinates

This compact encoding enables parallel writes via `InterlockedMax()` - effectively a software depth test in compute.

### 8.3 Performance Numbers (RTX 3060Ti)

- 10+ billion voxels at 64K resolution: 6ms per frame
- 2-4x faster than HashDAG at 32K+ resolutions
- Only 5% of scene data in VRAM at any time (streaming LOD)
- Memory: 2% VRAM/disk ratio at 64K (424MB VRAM for 23GB on-disk scene)

**Applicability to AubsCraft:** The full ray marching pipeline is overkill for our mesh-based approach. But the tile-based culling, visibility buffer concept, and streaming LOD system are applicable. The indirect dispatch pattern for culling is directly usable.

---

## 9. Bedrock Edition Optimizations

Minecraft Bedrock (C++) uses several optimizations relevant to our C#/WebGPU approach:

- **Direct hardware access** - C++ allows direct GPU API calls with minimal translation. Our ILGPU transpiler achieves similar directness by compiling C# to WGSL.
- **Fixed memory patterns** - No garbage collection pauses. In WASM, we can minimize GC by pre-allocating buffers and reusing them.
- **Multi-threaded entity processing** - Distributes entity calculations across CPU cores. Web Workers can achieve this.
- **Platform-optimized rendering stack** - DirectX/Metal/Vulkan per platform. WebGPU abstracts this for us.
- **Aggressive update limiting** - Shorter active update distances than Java. We can tune this independently of render distance.

---

## 10. LOD Techniques for Blocky Voxels

### 10.1 POP Buffers (Progressively Ordered Primitives)

From the 0fps.net LOD method:

**Vertex clustering** via progressive rounding:
```
L_i(v) = 2^i * floor(v / 2^i)
```

Each LOD level i rounds vertex coordinates to the nearest multiple of 2^i. Quads whose vertices collapse to the same point at level i are discarded.

**Key advantage:** A single sorted mesh encodes all LOD levels. Offset tables mark where each LOD boundary is. To render LOD level i, draw vertices from index 0 to offset[i]. No separate mesh storage needed.

**LOD level computation (constant time per quad):**
```
function intervalLOD(lo, hi) {
    return countLeadingZeroes(lo XOR hi)
}
```

### 10.2 Geomorphing for Seam-Free Transitions

Instead of hard LOD pops, smoothly interpolate vertex positions between LOD levels:

```
L_t(x) = (ceil(t) - t) * L_floor(t)(x) + (t - floor(t)) * L_ceil(t)(x)
```

Where t is a continuous LOD parameter based on distance. This eliminates visible seams and popping artifacts.

**Stable rounding** iteratively converges LOD parameters across chunk boundaries (2-3 iterations), ensuring consistent vertex positioning.

**Implementation:** Entirely in the vertex shader. No CPU work. The LOD parameter t is a uniform based on distance.

### 10.3 Squashed Face Removal

At coarse LOD levels, multiple faces can collapse to identical positions, causing overdraw. Detect and skip these degenerate faces to maintain efficiency.

**Applicability to AubsCraft:** POP buffers are elegant for our heightmap-to-3D LOD transitions. A single buffer per region with LOD offsets eliminates the need for separate heightmap and 3D meshes. Geomorphing in the vertex shader would give us smooth transitions for free. This is a high-value optimization.

**Complexity:** Medium. The sorting and offset table construction is straightforward. Geomorphing requires vertex shader changes but no additional CPU work.

---

## 11. Recommended Implementation Priority

Based on performance impact, complexity, and relevance to AubsCraft's current architecture:

### Tier 1 - High Impact, Achievable Now

| Technique | Source | Impact | Effort |
|-----------|--------|--------|--------|
| Compact vertex format (20-byte packed) | Sodium | 50-60% VRAM reduction | Medium |
| Consolidated indirect draw buffer | WebGPU best practices | 300x validation speedup | Low |
| Per-frame upload budget (512 chunks max) | WebGPU Voxel Engine | Eliminates frame spikes | Low |
| Camera-direction weighted loading | Chunk loading research | Perceived faster loading | Low |
| Region-based draw batching | Sodium | Fewer draw calls | Medium |

### Tier 2 - High Impact, More Effort

| Technique | Source | Impact | Effort |
|-----------|--------|--------|--------|
| Advanced Cave Culling (15-bit connectivity) | Bedrock/Sodium | 50-99% geometry culling | Medium |
| Binary greedy meshing in ILGPU kernel | Binary greedy meshing | 74us/chunk, optimal quads | Medium-hard |
| Multi-level LOD with smooth transitions | Distant Horizons + POP | Extreme draw distance | Medium |
| GPU frustum culling (compute shader) | Vulkan Guide/Aokana | Zero CPU culling cost | Medium |

### Tier 3 - Future Optimization

| Technique | Source | Impact | Effort |
|-----------|--------|--------|--------|
| Hi-Z occlusion culling | Aokana/VTK | 1.5-6x for complex scenes | High |
| POP buffers with geomorphing | 0fps.net | Seamless LOD, single buffer | Medium-hard |
| Vertex pooling with face-masking | Nick McDonald | Per-face backface culling | Medium |
| Tile-based visibility buffer | Aokana | GPU-driven full pipeline | High |
| LOD streaming (5% VRAM occupancy) | Aokana | Massive worlds | High |

---

## 12. Key Takeaways for AubsCraft

1. **Compact vertex format is the single biggest easy win.** Going from 44 bytes to 20 bytes per vertex cuts buffer sizes in half and improves cache performance. Unpack in the vertex shader.

2. **Consolidate indirect draw buffers immediately.** The 300x validation overhead difference is documented and proven. One buffer, all draws.

3. **Binary greedy meshing maps perfectly to ILGPU.** 64-bit bitwise operations on 64 voxels simultaneously is ideal GPU work. Target: 74us per chunk or better with GPU parallelism.

4. **Cave culling is the most impactful culling technique for Minecraft.** 15 bits per chunk, 50-99% geometry reduction. Must-have for full 3D underground rendering.

5. **Camera-direction loading is free performance.** Weighted priority queue costs nothing and makes loading feel 2x faster subjectively.

6. **LOD transitions should use geomorphing, not hard pops.** The vertex shader approach costs nothing at runtime and eliminates visible transitions.

7. **Upload budgets prevent frame spikes.** Cap at 512 chunk uploads per frame. Use priority queue to ensure most important chunks upload first.

8. **Pre-allocated buffer pools eliminate allocation overhead.** The WebGPU Voxel Engine uses 18,000 pre-allocated slots. Over-allocation has zero measurable cost.

---

## Sources

### Sodium
- [Chunk Rendering Pipeline - DeepWiki](https://deepwiki.com/CaffeineMC/sodium/3.1-chunk-rendering)
- [Sodium Mod - Modrinth](https://modrinth.com/mod/sodium)
- [CaffeineMC/sodium - DeepWiki](https://deepwiki.com/CaffeineMC/sodium)
- [Sodium Complete Breakdown - PlanetMinecraft](https://www.planetminecraft.com/blog/sodium-the-best-minecraft-optimization-mod-complete-breakdown/)

### Distant Horizons
- [Distant Horizons - CurseForge](https://www.curseforge.com/minecraft/mc-mods/distant-horizons)
- [Distant Horizons - GitLab](https://gitlab.com/distant-horizons-team/distant-horizons)
- [Distant Horizons FAQ](https://blog.curseforge.com/distant-horizons-frequently-asked-questions/)

### Web Voxel Engines
- [WebGPU Voxel Engine - DeepWiki](https://deepwiki.com/rowannadon/WebGPU-Voxel-Engine)
- [noa-engine - GitHub](https://github.com/fenomas/noa)
- [Prismarine Viewer - GitHub](https://github.com/PrismarineJS/prismarine-viewer)

### Meshing
- [Binary Greedy Meshing - GitHub](https://github.com/cgerikj/binary-greedy-meshing)
- [Meshing in a Minecraft Game - 0fps.net](https://0fps.net/2012/06/30/meshing-in-a-minecraft-game/)
- [Vertex Pooling - Nick McDonald](https://nickmcd.me/2021/04/04/high-performance-voxel-engine/)
- [Voxel World Optimisations - Vercidium](https://vercidium.com/blog/voxel-world-optimisations/)

### Culling
- [Advanced Cave Culling Algorithm - Tommaso Checchi](https://tomcc.github.io/2014/08/31/visibility-1.html)
- [Culling Techniques - Voxel.Wiki](https://voxel.wiki/categories/culling/)
- [WebGPU Occlusion Culling in VTK - Kitware](https://www.kitware.com/webgpu-occlusion-culling-in-vtk/)
- [Compute-Based Culling - Vulkan Guide](https://vkguide.dev/docs/gpudriven/compute_culling/)

### WebGPU
- [WebGPU Indirect Draw Best Practices - Toji.dev](https://toji.dev/webgpu-best-practices/indirect-draws.html)
- [WebGPU Frustum Culling Demo - GitHub](https://github.com/toji/webgpu-bundle-culling)
- [WebGPU Speed and Optimization - webgpufundamentals.org](https://webgpufundamentals.org/webgpu/lessons/webgpu-optimization.html)

### LOD
- [LOD Method for Blocky Voxels - 0fps.net](https://0fps.net/2018/03/03/a-level-of-detail-method-for-blocky-voxels/)
- [Aokana GPU-Driven Voxel Framework - arXiv](https://arxiv.org/html/2505.02017v1)
- [Quadtree LOD - GameDeveloper](https://www.gamedeveloper.com/programming/continuous-lod-terrain-meshing-using-adaptive-quadtrees)
- [Seamless LOD Transitions - dexyfex.com](https://dexyfex.com/2016/07/14/voxels-and-seamless-lod-transitions/)

### Chunk Loading
- [Minecraft Chunk Loading - Minecraft Wiki](https://minecraft.wiki/w/Chunk)
- [Chunk Loading Prioritization - Minecraft Forum](https://www.minecraftforum.net/forums/minecraft-java-edition/suggestions/79031-chunk-loading-re-write-with-prioritization)
- [Sliding Window - Voxel.Wiki](https://voxel.wiki/wiki/sliding-window/)
