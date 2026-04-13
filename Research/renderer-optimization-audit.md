# AubsCraft Renderer Optimization Audit

**Date:** 2026-04-13
**Author:** Tuvok (research/audit)
**Status:** Current state analysis - what exists, what's missing, prioritized bottlenecks

---

## Current Optimization State

### What Already Works Well

| Optimization | Implementation | Quality | Location |
|---|---|---|---|
| **Frustum culling** | CPU-side Gribb-Hartmann 6-plane AABB test, per-chunk, with distance pre-check | Good | `MapRenderService.cs` lines 713, 763, 767, 800 |
| **Two-tier LOD** | Heightmap (1 face/column) for distant, full 3D voxel for radius 3 | Good | `RenderWorkerService.cs` line 201 (`FullRenderRadius = 3`) |
| **Camera-distance loading** | Cached heightmaps sorted by distance before loading. Nearest first. | Good | `RenderWorkerService.cs` lines 219-224 |
| **Adaptive draw distance** | FPS-driven: grows by 2 when FPS > 50, shrinks by 2 when FPS < 30. Range 25-50 chunks. | Good | `MapRenderService.cs` lines 114-115 |
| **GPU-to-GPU heightmap** | `GetHeightmapOutputGPUBuffers()` exposes raw GPUBuffer, `CopyBufferToBuffer` with zero CPU readback | Excellent | `VoxelEngineService.cs` line 199 |
| **Vertex buffer sub-alloc** | Single large buffer with free-list allocation, coalescing adjacent free slots | Good | `MapRenderService.cs` lines 34-38, 487-502 |
| **Backface culling** | `CullMode.Back` + `FrontFace.CCW` on both pipelines | Good | `MapRenderService.cs` lines 197-201, 262-266 |
| **Depth testing** | Opaque: depth write + compare less. Transparent: depth read only (no write) | Correct | `MapRenderService.cs` lines 211-215, 276-280 |
| **Internal face culling** | Kernels only emit faces adjacent to air/transparent blocks | Good | `MinecraftMeshKernel.cs`, `HeightmapMeshKernel.cs` |

### What's Missing - Bottlenecks Ranked by Impact

#### 1. ONE DRAW CALL PER CHUNK (Highest Impact)

**Current:** Each visible chunk = 1 `pass.Draw()` call. 200+ visible chunks = 200+ draw calls per frame (opaque + water).

**Impact:** Draw call overhead is the dominant CPU-side cost at scale. Each call requires CPU-GPU sync.

**Fix:** Indirect draw / multi-draw-indirect. Collapse all chunks into 1-2 draw calls. WebGPU `drawIndirect` lets the GPU specify vertex counts. Or use a mega-buffer approach where all chunk geometry lives in one buffer with offset tracking.

**Settings panel toggle:** Draw call stats display (show count per frame)

#### 2. NO INDEX BUFFERS (High Impact)

**Current:** All geometry uses `pass.Draw()` (non-indexed). Every quad = 6 unique vertices (264 bytes) instead of 4 vertices + 6 indices (188 bytes).

**Impact:** 50% more vertex data transferred and processed than necessary. Two of every quad's four vertices are duplicated.

**Fix:** Switch to indexed rendering (`pass.DrawIndexed()`). Emit 4 unique vertices per face + 6 uint16 indices. The index buffer for quads is a repeating pattern (0,1,2,0,2,3) that can be pre-generated.

**Settings panel toggle:** Not toggleable - always better once implemented.

#### 3. VERTEX FORMAT BLOAT (High Impact)

**Current:** 11 floats = 44 bytes per vertex: position.xyz (12) + normal.xyz (12) + color.rgb (12) + uv.xy (8)

**Possible:** ~16 bytes per vertex:
- Position: 3x uint8 chunk-relative (3 bytes) or 3x int16 (6 bytes)
- Normal: 1 byte (only 6 possible face normals = 3-bit index)
- Color: RGBA8 (4 bytes)
- UV: 2x uint16 or float16 (4 bytes)
- Total: ~12-16 bytes = **64-73% reduction**

**Impact:** Less GPU memory, less bandwidth, faster vertex fetch.

**Settings panel toggle:** Not toggleable - always better once implemented.

#### 4. PER-PIXEL TIME-OF-DAY (Quick Win)

**Current:** `get_sky_colors()` in the fragment shader runs trig ops (`sin`, `cos`) plus multiple `clamp`/`mix` calls PER PIXEL to compute sun direction, ambient color, fog color, and sun strength. These values are constant for the entire frame.

**Fix:** Precompute `sun_dir`, `ambient`, `sun_color`, `fog_color`, `sun_strength` on CPU (or in a tiny compute shader) once per frame. Pass as uniforms. Zero per-pixel trig.

**Impact:** Eliminates redundant trig from every single fragment. Biggest win on fill-rate-limited scenes (lots of visible terrain).

**Settings panel toggle:** Time-of-day on/off (disabled = noon lighting, no trig cost at all)

#### 5. NO GREEDY MESHING (High Impact, Complex)

**Current:** MinecraftMeshKernel emits one quad per exposed block face. Adjacent coplanar faces of the same block type are never merged.

**Fix:** Greedy meshing algorithm merges adjacent same-type coplanar faces into larger quads. A 16x16 flat grass surface becomes 1 quad instead of 256 quads.

**Impact:** 50-80% face count reduction for typical Minecraft terrain. Massive vertex count and draw bandwidth savings.

**Complexity:** Harder to implement on GPU (greedy meshing is inherently sequential per-slice). Usually done on CPU. Could be done as a post-process on the kernel output.

**Settings panel toggle:** Greedy meshing on/off (see immediate FPS difference)

#### 6. BUFFER REALLOCATION CHURN (Medium Impact)

**Current:** `VoxelEngineService.GenerateMeshAsync` calls `Dispose()` + `Allocate1D()` on palette, atlas UV, and block flag GPU buffers every single call (lines 145-155).

**Fix:** Use the existing `EnsureBuffer` grow-only pattern (line 281) for these buffers too. Allocate once, grow only if needed.

**Impact:** Eliminates GPU allocation/deallocation overhead per chunk mesh.

**Settings panel toggle:** Not toggleable - always better.

#### 7. NO MESH CACHING TO DISK (Medium Impact)

**Current:** Raw heightmap data is cached in OPFS. But generated mesh (vertex data) is NOT cached. On every page load, all cached chunks are re-dispatched through the GPU kernel to regenerate mesh.

**Fix:** Cache the generated vertex float arrays in OPFS alongside the raw data. On reload, upload directly to GPU buffer without compute dispatch.

**Impact:** Cold-start loading becomes a pure buffer upload - no kernel compute at all. Especially impactful for the full 3D chunks which are expensive to mesh.

**Settings panel toggle:** "Clear mesh cache" button

#### 8. NO OCCLUSION CULLING (Medium Impact)

**Current:** Frustum culling only. Chunks behind mountains or underground still draw if in frustum.

**Fix options:**
- CPU-based: chunk-to-chunk visibility graph (Sodium approach)
- GPU-based: hierarchical Z-buffer (HZB) occlusion queries
- Simple: cave detection - skip subterranean chunks when camera is on surface

**Impact:** Significant for hilly terrain and underground areas. Less impactful for flat terrain.

**Settings panel toggle:** Occlusion culling on/off + debug visualization

#### 9. UNDERGROUND BLOCK PROCESSING (Medium Impact)

**Current:** MinecraftMeshKernel dispatches 98,304 threads (one per block in 16x384x16). ~90% of blocks in a typical chunk are underground solid stone. Each thread checks 6 neighbors, finds all solid, emits nothing - wasted compute.

**Fix options:**
- **Y-section skipping:** Pre-scan column heights, skip dispatch for deep underground Y ranges
- **Sparse dispatch:** Build a list of "surface-adjacent" blocks, dispatch only those
- **Bitmask pre-pass:** One bit per block indicating "has air neighbor" - compute cheaply, then mesh only flagged blocks

**IMPORTANT:** Must be toggleable. X-ray mode, underground explorer, spectral view (from PLANS.md) all need access to hidden blocks.

**Settings panel toggle:** Underground skip on/off (off = X-ray mode)

---

## Settings Panel Feature Candidates

Based on this audit, the settings panel should expose:

### Performance Toggles
- [ ] Draw distance slider (override adaptive, range 10-100)
- [ ] Full 3D chunk radius slider (1-10, currently fixed at 3)
- [ ] Greedy meshing on/off
- [ ] Underground skip on/off (off = X-ray/debug)
- [ ] Occlusion culling on/off
- [ ] Time-of-day lighting on/off (off = noon)
- [ ] Water transparency on/off (off = opaque water, skip transparent pass)
- [ ] Plants rendering on/off (skip cross-quads)

### Debug/Stats Overlay
- [ ] FPS counter (already exists)
- [ ] Draw calls per frame
- [ ] Visible chunk count (frustum-passed)
- [ ] Total vertex count
- [ ] GPU memory usage
- [ ] Mesh wireframe overlay
- [ ] Frustum culling visualization (show culled chunk outlines)
- [ ] Chunk loading status (show which chunks are heightmap vs 3D vs loading)

### Visual Quality
- [ ] Fog distance slider
- [ ] Ambient occlusion on/off (future)
- [ ] Shadow quality (future)
- [ ] Texture filtering (nearest/linear)

---

## Priority Implementation Order

1. **Per-pixel time-of-day -> uniforms** (quick win, easy, immediate FPS gain)
2. **Buffer reallocation fix** (use EnsureBuffer for palette/atlas/flags)
3. **Index buffers** (29% vertex data reduction, straightforward)
4. **Settings panel + stats overlay** (enables measuring everything else)
5. **Vertex compression** (64% vertex size reduction)
6. **Underground skip** (reduce compute dispatch by ~80% per chunk)
7. **Greedy meshing** (50-80% face reduction, complex but massive impact)
8. **Draw call batching** (indirect draw / mega-buffer, most complex but highest ceiling)
9. **Mesh caching to OPFS** (faster cold start)
10. **Occlusion culling** (advanced, highest complexity)
