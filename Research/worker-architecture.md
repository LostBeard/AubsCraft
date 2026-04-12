# Web Worker Architecture - Future Optimization

**Date:** 2026-04-12
**Status:** Ideas phase - optimize current pipeline first before implementing

---

## The Vision

Move the entire rendering pipeline off the main WASM thread using SpawnDev.BlazorJS.WebWorkers. Main thread becomes pure UI coordinator.

## Why Workers

The main Blazor WASM thread is single-threaded. During chunk loading, HeightmapMesher CPU work drops FPS to 10. Even after moving to ILGPU kernels, there's still coordination overhead (buffer uploads, state management) that competes with UI updates.

## Architecture Options

### Option A: Dedicated Data Worker
- Worker handles: WebSocket receive, OPFS cache read/write, binary parsing
- Main thread handles: ILGPU dispatch, WebGPU rendering, UI
- Data crosses via SharedArrayBuffer or transferable ArrayBuffer
- Simplest to implement, biggest bang for buck

### Option B: Dedicated Render Worker
- Worker handles: ALL rendering (OffscreenCanvas + WebGPU), ILGPU dispatch, mesh generation
- Main thread handles: UI only (Blazor components, input events, StateHasChanged)
- Worker sends back stats (FPS, chunk count) for UI display
- Canvas transferred to worker via transferControlToOffscreen()
- Most separation, best FPS stability

### Option C: Full Worker App
- SpawnDev.BlazorJS.WebWorkers runs entire Blazor WASM service container in a worker
- All services (VoxelEngineService, MapRenderService, ChunkStreamService, WorldCacheService) run in worker
- Main thread is just the Razor UI shell
- Maximum isolation, cleanest architecture

## SpawnDev.BlazorJS.WebWorkers Capabilities

- Run full Blazor WASM DI container in a Web Worker
- Services registered in worker have access to all browser APIs via BlazorJSRuntime
- SharedArrayBuffer for zero-copy data sharing between main thread and worker
- OffscreenCanvas for GPU rendering from worker
- WebGPU available from workers (device, queue, compute, render)

## Prerequisites (must be done FIRST)

1. HeightmapMesher -> ILGPU HeightmapMeshKernel (eliminate CPU meshing)
2. CopyFromJS integration (OPFS -> GPU direct)
3. Measure what's actually on the main thread after GPU optimization
4. THEN decide which worker architecture makes sense based on real profiling data

## WebGPU in Workers - Key Facts

- `navigator.gpu` available in dedicated workers
- `GPUDevice` can be used from worker
- `OffscreenCanvas` transferred via `canvas.transferControlToOffscreen()`
- `GPUCanvasContext` works on OffscreenCanvas
- SpawnDev.ILGPU accelerator creation works from workers (already tested in Wasm backend)
- SharedArrayBuffer enables zero-copy buffer sharing

## When to Implement

After the current pipeline is fully optimized and we have profiling data showing what's still on the main thread. Don't add workers to work around CPU meshing - fix the meshing first.
