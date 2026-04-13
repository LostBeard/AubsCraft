# CLAUDE.md - AubsCraft

AubsCraft is a real-time Minecraft server admin panel and GPU-accelerated 3D world viewer. Built with Blazor WebAssembly, SpawnDev.ILGPU (WebGPU), and ASP.NET Core (.NET 10). Built for Aubriella's server at mc.spawndev.com.

## Architecture Rules - READ BEFORE WRITING CODE

### Data Transport - SignalR vs JS WebSocket

**This is non-negotiable. Get it right the first time.**

| Data Type | Transport | Why |
|-----------|-----------|-----|
| Small structured data (status, time, events, player positions, chat) | **SignalR** | Push-based, typed hub methods, already connected, 3-second poll cycle via `ServerMonitorService` |
| Large binary data (chunk geometry, heightmaps, block data) | **JS WebSocket** (binary frames at `/api/world/ws`) | Raw binary, zero-copy to GPU via `CopyFromJS`, no .NET serialization overhead |

**NEVER:**
- Poll the server with HTTP for data that SignalR already pushes (time, player count, TPS)
- Create new HTTP endpoints for data that fits on the existing SignalR status push
- Use SignalR for bulk binary chunk data (serialization overhead kills performance)
- Use JS WebSocket for small structured updates (unnecessary complexity)

**When adding new real-time data to the viewer:**
1. First check if `ServerStatusDto` already carries it (it pushes every 3 seconds)
2. If not, add the field to `ServerStatusDto` and query it in `ServerMonitorService.PollAndPushAsync`
3. Only create a new hub method if the data has different push timing needs than the 3-second cycle
4. Only use the binary WebSocket for large binary payloads (chunk data, region files)

### Data Flow - Where Data Lives

- **JS side:** Binary chunk data, texture atlas, OPFS cache, WebGPU buffers
- **.NET side:** Application logic, SignalR hub client, service orchestration
- **GPU side:** Mesh kernels, rendering, all compute

**NEVER pull data from JS to .NET without Captain's explicit approval.** `ReadBytes()` is BANNED. Data enters JS and goes to GPU via `CopyFromJS`. The .NET side orchestrates but doesn't touch the bulk data.

### GPU Compute - ILGPU for Everything

- **ALL mesh generation runs on GPU** via ILGPU kernels (HeightmapMeshKernel, MinecraftMeshKernel)
- **No CPU fallbacks.** ILGPU runs on all backends. If you're tempted to write a CPU loop for compute, write a kernel instead.
- **WebGPU maxStorageBuffersPerShaderStage = 10** (Chrome). Kernels must stay under this limit. Use struct packing to combine related ArrayView parameters. ILGPU v4.9.1+ validates this at runtime.

### Rendering Pipeline

- **WebGPU front-face:** `GPUFrontFace.CCW` + `CullMode.Back` on both opaque and transparent pipelines
- **Two-pass rendering:** Opaque pipeline first, then alpha-blend pipeline for water
- **Vertex format:** 11 floats per vertex (position.xyz + normal.xyz + color.rgb + uv.xy)
- **Texture atlas:** 256x256 (16x16 grid), loaded from `atlas.rgba` (65KB static file)

## Build & Deploy

```bash
# Build
dotnet build AubsCraft.slnx

# Run locally
dotnet run --project AubsCraft.Admin.Server

# Deploy to production
deploy-aubscraft.bat
```

**Deploy after EVERY code change.** Don't batch changes - deploy, verify, iterate.

**Always check the build timestamp** in the browser console on startup to verify you're running the latest code.

## Project Structure

```
AubsCraft/
  AubsCraft.Admin/           # Blazor WASM frontend
    Pages/                   # Razor pages (Map.razor = 3D viewer)
    Services/                # Client services (VoxelEngineService, RenderWorkerService)
    Rendering/               # GPU pipeline (MapRenderService, kernels, camera, culling)
  AubsCraft.Admin.Server/    # ASP.NET Core backend
    Services/                # Server services (WorldDataService, RegionReader, RCON)
    Hubs/                    # SignalR hub (ServerHub)
  SpawnDev.Rcon/             # Standalone RCON client library
  VRDetect/                  # Paper plugin (Java) - VR player detection
  Research/                  # Design docs and planning
  textures/                  # Minecraft block texture PNGs (16x16)
```

## Key Files

| File | What It Does |
|------|-------------|
| `Rendering/MapRenderService.cs` | WebGPU render pipeline, shaders, draw calls |
| `Rendering/HeightmapMeshKernel.cs` | ILGPU kernel - heightmap mesh (distant chunks) |
| `Rendering/MinecraftMeshKernel.cs` | ILGPU kernel - full 3D voxel mesh (nearby chunks) |
| `Services/VoxelEngineService.cs` | GPU buffer management, kernel dispatch |
| `Services/RenderWorkerService.cs` | Web Worker orchestration, chunk loading, data flow |
| `Services/WorldCacheService.cs` | OPFS/IndexedDB chunk caching |
| `Server/Services/ServerMonitorService.cs` | Server status polling (3s cycle), SignalR push |
| `Server/Hubs/ServerHub.cs` | SignalR hub - structured data push to clients |
| `Server/Program.cs` | Server startup, HTTP endpoints, WebSocket setup |

## SpawnDev Libraries Used

- **SpawnDev.BlazorJS** - JS interop (never write raw JavaScript)
- **SpawnDev.ILGPU** - GPU compute (WebGPU backend for mesh kernels)
- Use DI correctly - never bypass the service container

### Banned Patterns - NO EXCEPTIONS

- **NEVER use `eval()`** - SpawnDev.BlazorJS has typed wrappers for every browser API
- **NEVER use `IJSRuntime`** - always inject `BlazorJSRuntime`. IJSRuntime requires Captain's explicit consent.
- **NEVER use `window.__globals`** to pass state between JS and .NET - use C# fields and BlazorJS typed properties
- **NEVER use `AddEventListener` with raw strings** - use ActionEvent properties (OnPointerLockChange, OnClick, etc.)
- **NEVER call JS every frame** for state that can be tracked by an event handler updating a C# field

**If you are unsure how to do something with SpawnDev.BlazorJS or any SpawnDev library - ASK TJ.** He wrote them. He knows every API. Guessing leads to eval() and IJSRuntime hacks. Asking takes 30 seconds and gets the right answer.

## Reference Codebase

Lost Spawns at `D:\users\tj\Projects\Lost\Lost\LostSpawns` has proven render patterns (VoxelMesher, Camera, FrustumCuller).
