# WebGPU Rendering Optimizations for AubsCraft

**Date:** 2026-04-13
**Author:** Tuvok (research)
**Source:** Web research + WebGPU spec analysis

---

## Priority-Ordered Implementation Roadmap

| Priority | Technique | Impact | Effort | Browser Support | Notes |
|----------|-----------|--------|--------|-----------------|-------|
| 1 | **Index Buffers** | High | Low | All | 33% fewer vertex shader invocations |
| 2 | **Vertex Packing** | Very High | Medium | All | 44 bytes -> 8 bytes/vertex (82% reduction) |
| 3 | **Indirect Draws** | High | Low-Medium | All | Eliminates CPU-GPU sync for vertex counts |
| 4 | **Texture Array** | Medium | Low | All | Correct mipmapping, unlocks greedy meshing UVs |
| 5 | **Render Bundles** | Medium | Low | All | 2-5x CPU draw submission speed |
| 6 | **GPU Frustum Culling** | Medium | Medium | All | Removes CPU from per-frame visibility |
| 7 | **Front-to-back sort** | Low-Med | Trivial | All | Free Early-Z, skip depth pre-pass entirely |
| 8 | **Subgroup Operations** | Medium | Med-High | Chrome only | Reduces atomic contention in mesh kernels |
| 9 | **Multi-Draw Indirect** | High | Low change | Chrome experimental | Design buffers for it now, upgrade when shipped |

---

## 1. Indirect Draw Calls

Instead of `pass.draw(vertexCount)`, the GPU reads draw parameters from a `GPUBuffer`. A compute shader writes these - the CPU never needs to know vertex counts.

**Critical Chrome/Dawn optimization (from Toji's best practices):** Packing all indirect draw args into ONE buffer is **300x faster** than separate buffers. Chrome/D3D12 validates per-buffer. Always use ONE indirect buffer, not one per chunk.

**ILGPU compatibility:** The mesh kernel already uses atomic counters. Change: have the kernel write to the indirect buffer instead. Requires `STORAGE | INDIRECT` usage flags on the buffer. Access via `ArrayView<int>`.

**Sources:** [Toji.dev indirect draw best practices](https://toji.dev/webgpu-best-practices/indirect-draws.html), [MDN drawIndexedIndirect](https://developer.mozilla.org/en-US/docs/Web/API/GPURenderPassEncoder/drawIndexedIndirect)

---

## 2. GPU-Driven Frustum Culling

Compute shader tests each chunk's AABB against 6 frustum planes. If visible, writes draw params to indirect buffer. If not, skips (instanceCount = 0).

**Pipeline:** Upload frustum planes as uniform (96 bytes) -> dispatch compute (1 thread per chunk) -> render from indirect buffer. Zero CPU involvement in visibility.

**Recommendation:** Write the culling shader in raw WGSL (~30 lines of frustum math). Keep mesh generation in ILGPU. At current scale (200-400 chunks), CPU culling isn't the bottleneck - the win comes at larger view distances.

**Sources:** [Toji webgpu-bundle-culling](https://github.com/toji/webgpu-bundle-culling), [VTK WebGPU Occlusion Culling](https://www.kitware.com/webgpu-occlusion-culling-in-vtk/)

---

## 3. Multi-Draw Indirect (Future)

Collapses 200+ `drawIndirect` calls into a single `multiDrawIndirect` call.

**Status:** Chrome experimental only (`chromium-experimental-multi-draw-indirect`). Not in Firefox or Safari. 6-12+ months from standardization.

**Recommendation:** Do NOT depend on this now. Design the indirect buffer layout to be multi-draw-ready. When it ships, upgrading is a one-line change.

---

## 4. Vertex Buffer Packing

**Current: 44 bytes per vertex** (pos.xyz + normal.xyz + color.rgb + uv.xy, all float32)

**Target: 8 bytes per vertex (two uint32):**
- Position: 5+9+5 = 19 bits (chunk-relative, 0-15 XZ, 0-383 Y)
- Normal: 3 bits (only 6 possible face normals)
- Block ID: 12 bits (up to 4096 types)
- AO: 2 bits per vertex
- UV corner: 2 bits (4 possible corners)

Vertex shader reconstructs: `worldPos = chunkOrigin + localPos`. Normal from 6-entry lookup table. Color from block ID palette.

**Impact:** 82% memory reduction. 5.5x fewer cache lines per vertex. For 200 chunks: ~528 MB -> ~96 MB vertex data.

**ILGPU compatibility:** Fully compatible. Kernels output `ArrayView<int>` with bitwise packing. All backends support shift/OR/AND.

**Proven implementations:** Vercidium (4 bytes/vert), Exile engine (8 bytes/vert), Sodium (20 bytes/vert).

**Sources:** [Vercidium voxel optimizations](https://vercidium.com/blog/voxel-world-optimisations/), [Exile voxel meshing](https://thenumb.at/Voxel-Meshing-in-Exile/), [Voxel.Wiki vertex pulling](https://voxel.wiki/wiki/vertex-pulling/)

---

## 5. Index Buffers

**Current:** 6 vertices per quad (264 bytes at 44 bytes/vert)
**Indexed:** 4 vertices + 6 uint16 indices per quad (188 bytes at 44 bytes/vert)

**Win:** 33% fewer vertex shader invocations (2 of 6 are duplicates). 29% memory reduction at current vertex size.

**Implementation:** Pre-generate a shared static index buffer with repeating pattern [0,1,2,0,2,3,4,5,6,4,6,7...]. Use `mappedAtCreation: true` for zero-copy init. Kernel emits 4 vertices per face instead of 6.

**Recommendation:** Implement BEFORE vertex compression. Simpler change, immediate benefit.

---

## 6. Texture Array vs Atlas

**Current:** 256x256 atlas (16x16 grid). Problems: mipmap bleeding at tile edges, broken UV tiling for greedy meshing.

**Better:** `texture_2d_array<f32>` - each block type is a separate layer. Perfect mipmapping per layer. UV wrapping works naturally. Enables greedy meshing with tiled UVs.

**WebGPU:** `device.createTexture({ size: [16, 16, blockTypeCount], dimension: '2d' })`. In WGSL: `textureSample(blockTextures, blockSampler, uv, blockId)`.

**Impact:** Correct visuals at distance (no bleeding), unlocks greedy meshing UV tiling.

**Sources:** [Voxel.Wiki texture atlas](https://voxel.wiki/wiki/texture-atlas/), [0fps.net atlas wrapping](https://0fps.net/2013/07/09/texture-atlases-wrapping-and-mip-mapping/)

---

## 7. Render Bundles

Pre-record draw commands into reusable objects. Execute with `renderPass.executeBundles([bundle])`.

**Win:** 2-5x faster CPU draw submission. Record once when chunk set changes, replay every frame until chunks load/unload.

**Works with indirect draws:** Bundle records `drawIndirect` calls. Buffer contents change per frame (GPU culling updates them), but bundle structure stays the same.

**Sources:** [Toji render bundle best practices](https://toji.dev/webgpu-best-practices/render-bundles.html)

---

## 8. Depth Pre-Pass - SKIP

**Recommendation:** Skip the depth pre-pass entirely. Instead:
1. Sort chunk draw calls front-to-back (trivial - sort by distance to camera)
2. Precompute time-of-day as uniforms (eliminate per-pixel trig)

Front-to-back sort activates hardware Early-Z for free, giving 80% of the depth pre-pass benefit at near-zero cost. Voxel geometry has inherently limited overdraw compared to complex 3D scenes.

---

## 9. Subgroup Operations

Threads within a warp (32-64) communicate directly without shared memory. `subgroupExclusiveAdd` gives each thread a unique output offset cooperatively - one atomic per subgroup instead of one per thread.

**Impact:** 2-3x for atomic-bottlenecked kernels. The mesh kernel's atomic counter is likely a bottleneck for chunks with many visible faces.

**Limitation:** Chrome only (shipped 134+). Not in Firefox/Safari. ILGPU doesn't expose subgroup ops - would need raw WGSL.

---

## 10. Buffer Management - Current Approach is Good

`queue.writeBuffer()` is the recommended WebGPU path. The browser optimizes it internally. Ring buffers not needed at current scale.

**Only changes needed:**
- `mappedAtCreation: true` for the static shared index buffer
- `STORAGE | INDIRECT` usage flags for indirect draw buffer
- Reduce data volume via vertex packing (not upload mechanism)

---

## Key Design Groups

**Group A: Vertex Format Redesign** (do together)
- Index buffers + vertex packing + texture array
- Reduces per-face data from 264 bytes to ~44 bytes (6x)

**Group B: GPU-Driven Rendering** (do together)
- Indirect draws + GPU culling + render bundles
- Removes CPU from visibility/draw decisions

Both groups are independent and can be pursued in parallel.
