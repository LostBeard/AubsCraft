# Open-Source Voxel Engines - GitHub Reference

**Date:** 2026-04-13
**Purpose:** Catalog of voxel engines with documented performance techniques applicable to AubsCraft

---

## TIER 1: High-Value Projects

### 1. binary-greedy-meshing (cgerikj) - 291 stars
**URL:** https://github.com/cgerikj/binary-greedy-meshing
**Language:** C/C++ | **Browser:** No (native OpenGL)

Processes 64 faces simultaneously using 64-bit bitmask operations. Three-stage pipeline: occupancy masks, bitwise hidden face culling, bitwise greedy quad merging.

- **8 bytes per quad** (6-bit coords/dimensions + 8-bit voxel type)
- Vertex pulling + `glMultiDrawElementsIndirect` = single draw call
- **Benchmark: ~74 microseconds per chunk** (single-threaded, 64x64x64)
- ~30x faster than conventional mesh generation

**Study for:** The meshing algorithm. Directly applicable to ILGPU compute shader implementation.

---

### 2. Vercidium voxel-mesh-generation - 126 stars
**URL:** https://github.com/Vercidium/voxel-mesh-generation
**Language:** C# / GLSL | **Browser:** No (native OpenGL)

Run-based face combining (not full greedy meshing). Trades ~20% more triangles for ~390% faster meshing.

- **4 bytes per vertex** (position + texture + health + normal in one uint32)
- Separated mesh generation and VBO buffering for multithreading
- **Benchmark: 0.278-0.535ms per chunk** (32x32x32)

**Study for:** C# implementation directly relevant to our stack. Speed-vs-triangle tradeoff insight.

---

### 3. Voxelize - 628 stars
**URL:** https://github.com/voxelize/voxelize
**Language:** TypeScript (50%) / Rust (48%) | **Browser:** Yes (Three.js frontend)

Rust server + Three.js browser client. Most starred browser voxel engine.

- Multithreaded mesh generation (client and server)
- Multi-stage chunk generation with chunk overflow
- AABB physics, entity collision
- Protocol Buffers for client-server communication

**Study for:** Rust+browser architecture pattern with multiplayer.

---

### 4. Divine Voxel Engine - 252 stars
**URL:** https://github.com/Divine-Star-Software/DivineVoxelEngine
**Language:** TypeScript | **Browser:** Yes (WebGL via Babylon.js, experimental WebGPU)

Most mature TypeScript voxel engine.

- Multi-threaded via Web Workers (world, mesher, generator workers)
- Renderer-independent (Babylon.js, Three.js, custom WebGPU)
- SharedArrayBuffer optional
- Ambient occlusion, smooth lighting, connected textures, colored light

**Study for:** Worker architecture, renderer independence.

---

### 5. wgpu-mc - 586 stars
**URL:** https://github.com/wgpu-mc/wgpu-mc
**Language:** Rust (62%) / Java (30%) / WGSL (7%) | **Browser:** Potentially (wgpu supports WebGPU)

Standalone WebGPU rendering engine replacing Minecraft's OpenGL renderer via Fabric mod.

**Study for:** WGSL shaders are directly reusable reference material.

---

### 6. all-is-cubes - 224 stars
**URL:** https://github.com/kpreid/all-is-cubes
**Language:** Rust (98%) / WGSL (1.3%) | **Browser:** Yes (WASM + wgpu)

Clean modular Rust architecture with WASM browser deployment.

- Core simulation, GPU rendering, CPU raytracing, mesh generation as separate crates
- Recursive block composition (blocks made of blocks)
- Cross-platform: native desktop + WASM browser

**Study for:** Clean separation of concerns, WASM deployment pattern.

---

## TIER 2: Notable Projects

### 7. JuanDiegoMontoya/Voxel_Engine - 20 stars
**URL:** https://github.com/JuanDiegoMontoya/Voxel_Engine
**Language:** C/C++ | **Browser:** No

2 billion voxel world at 90-1500 FPS. Full GPU-driven rendering pipeline.

- GPU generates draw commands, performs frustum + raster occlusion culling
- Single draw call via `glMultiDrawArraysIndirectCount`
- Chunk vertices = just two packed integers
- 18,000 buffer slots for chunk data

**Study for:** Complete GPU-driven pipeline reference.

---

### 8. WebGPU-Voxel-Engine (rowannadon)
**URL:** https://github.com/rowannadon/WebGPU-Voxel-Engine
**Language:** C++ (64%) / WGSL | **Browser:** No (native WebGPU)

Most technically advanced WebGPU voxel engine found.

- Dual-thread: render at 120 FPS, chunk updates at 50 Hz
- Multi-pass rendering: shadow map, main pass, sky pass
- Physically-based atmospheric scattering (Rayleigh/Mie) via precomputed LUTs
- 18,000 buffer slots, 3D texture pool (768^3)
- LOD system, indirect rendering, PBR materials
- Compute shader atmospheric preprocessing

**Study for:** Advanced WebGPU rendering architecture, atmospheric effects.

---

### 9. gpucraft (brendan-duncan) - 26 stars
**URL:** https://github.com/brendan-duncan/gpucraft
**Language:** JavaScript | **Browser:** Yes (WebGPU, live demo)

Pure WebGPU voxel renderer running in browsers. Simple codebase.

**Study for:** WebGPU API usage patterns for voxels.

---

### 10. Rezcraft - 21 stars
**URL:** https://github.com/Shapur1234/Rezcraft
**Language:** Rust + wgpu | **Browser:** Yes (WASM via wasm-pack)

Working WASM browser demo with greedy meshing, colored lighting, sunlight.

**Study for:** Proves wgpu voxel engines run in browsers today.

---

### 11. Voxelis (SVO-DAG) - 85 stars
**URL:** https://github.com/WildPixelGames/voxelis
**Language:** Rust

Sparse Voxel Octree DAG with hash-consing - 99.999% compression via shared subtrees. 4cm voxel resolution without excessive RAM.

**Study for:** DAG compression for LOD and large world storage.

---

## TIER 3: WebGPU Infrastructure

### 12. toji/webgpu-bundle-culling - 56 stars
**URL:** https://github.com/toji/webgpu-bundle-culling
**Language:** JavaScript

By Brandon Jones (Google WebGPU spec editor). Canonical reference for GPU-driven culling in WebGPU.

- Frustum culling via compute shaders
- Render bundles for pre-recorded commands
- Indirect instanced draws with GPU-driven parameters

---

## Key Blog Posts / Technical References

| Title | URL | Key Technique |
|---|---|---|
| Meshing in Minecraft (0fps.net) | https://0fps.net/2012/06/30/meshing-in-a-minecraft-game/ | Foundational greedy meshing reference |
| LOD for Blocky Voxels (0fps.net) | https://0fps.net/2018/03/03/a-level-of-detail-method-for-blocky-voxels/ | POP Buffers - quantization-based LOD |
| Advanced Cave Culling (tomcc) | https://tomcc.github.io/2014/08/31/visibility-1.html | Flood-fill visibility with 15 bits/chunk |
| Vertex Pooling (nickmcd.me) | https://nickmcd.me/2021/04/04/high-performance-voxel-engine/ | Persistent VBO pool + multi-draw-indirect |
| Voxel Meshing in Exile | https://thenumb.at/Voxel-Meshing-in-Exile/ | 16 bytes/quad with instanced rendering |
| Voxel Optimizations (Vercidium) | https://vercidium.com/blog/voxel-world-optimisations/ | 4 bytes/vertex, run-based combining |
| WebGPU Indirect Draws (toji.dev) | https://toji.dev/webgpu-best-practices/indirect-draws.html | Single-buffer indirect = 300x faster |
| WebGPU Render Bundles (toji.dev) | https://toji.dev/webgpu-best-practices/render-bundles.html | Pre-recorded draw commands |
| Texture Atlases (0fps.net) | https://0fps.net/2013/07/09/texture-atlases-wrapping-and-mip-mapping/ | Why texture arrays beat atlases |
| Ascendant Geometry (vkguide.dev) | https://vkguide.dev/docs/ascendant/ascendant_geometry/ | 400MB gigabuffer, GPU compute culling |

---

## Most Applicable to AubsCraft

| Priority | Source | What to Take |
|---|---|---|
| 1 | binary-greedy-meshing | The meshing algorithm for ILGPU compute |
| 2 | Vercidium (C#) | 4-byte vertex format, run-based combining as alternative |
| 3 | toji/webgpu-bundle-culling | GPU-driven culling pattern for WebGPU |
| 4 | Divine Voxel Engine | Multi-worker architecture for browser |
| 5 | Exile voxel meshing | 16 bytes/quad instanced rendering pattern |
| 6 | Sodium (CaffeineMC) | Priority-based async meshing + upload budgets |
| 7 | WebGPU-Voxel-Engine | Advanced atmospheric/shadow techniques |

**Note:** WebGPU mesh shaders are NOT available in browsers (gpuweb issue #3015). Compute shader mesh generation is the current approach - which is exactly what we do with ILGPU.
