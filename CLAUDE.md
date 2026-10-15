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

- **JS side:** Binary chunk data (fetched ArrayBuffers), the texture atlas, WebGPU buffers
- **.NET side:** Application logic, SignalR hub client, service orchestration
- **GPU side:** Meshing, rendering, all compute

**NEVER pull data from JS to .NET without Captain's explicit approval.** `ReadBytes()` is BANNED. Data enters JS and goes to GPU via `CopyFromJS`. The .NET side orchestrates but doesn't touch the bulk data.

### GPU Compute - ILGPU for Everything

- **ALL mesh generation runs on GPU** via SpawnDev.VoxelEngine's ILGPU meshers (`ColumnMesher` for full columns, `HeightmapMesher` for the far tier). AubsCraft has no mesh kernels of its own.
- **No CPU fallbacks.** ILGPU runs on all backends. If you're tempted to write a CPU loop for compute, write a kernel instead.
- **WebGPU maxStorageBuffersPerShaderStage = 10** (Chrome). Kernels must stay under this limit. Use struct packing to combine related ArrayView parameters. ILGPU v4.9.1+ validates this at runtime.

### Rendering Pipeline

- **The world is drawn by `SpawnDev.VoxelEngine`** (`VoxelWorldRenderer` here): packed 8-byte quads (`PackedQuad`), vertex pulling, reversed-Z (`depth32float`), opaque + plant + water passes (water alpha blended, no depth write).
- **One draw per column** (or far chunk): the quad carries its section index (`EmbedSectionIndex`), one origin per column. Recording the frame's browser calls was the whole frame cost, so keep draw and bind counts low (F3 shows them).
- **Quads live in a few big buffers** (`QuadArena`, 16 MiB blocks; the meshers compact straight into it): a pass binds a block, not a buffer per column (about 45 real binds for 1,800 draws). Draw with `list.FirstQuad`. The remaining cost is the draws themselves (about 2.5 us each in the browser); the next lever is render bundles.
- **Nothing is drawn or meshed past the fog** (`FarWindow`, `DayLighting.FogEndChunks` = 30 chunks): fog is total from 450 blocks, so farther terrain is the fog colour and only costs draws; the frame is cleared to the fog colour so what is not drawn looks exactly like what is fogged, at any hour. Far chunks keep their 3 KB heightmap on the GPU but hold a MESH only within the window (mesh at draw radius + 2, free at + 6). The draw distance setting is capped at 30.
- **Loading is paced** (`IngestGovernor`): every mesh waits for a turn, granted per frame by how fast frames are (30 fps target, never below 0.5 turns a frame), nearer work first, re-meshes of neighbours last and coalesced (once per column however many neighbours arrived). A weak GPU keeps its frame rate while the world streams in.
- **Block ids are the server's GLOBAL ids** (`GlobalBlockRegistry`, `/api/world/blocks`, `/api/world/chunk-ids/{x}/{z}`, WebSocket `?format=ids`): the bytes go from the fetched ArrayBuffer to a GPU buffer with `CopyFromJS`, never into .NET. `VoxelBlockTables` turns names into classes and materials.
- **Texture atlas:** 256x256 (16x16 grid), `atlas.rgba` (65KB static file), sliced on the GPU into a texture array (a tile index is its layer).
- **HUD:** SpawnDev.GameUI in the render worker (`ViewerHud`): stats, compass, minimap, player list, chat, notices. SignalR stays on the page; `HudFeedBuffer` batches what it delivers to the worker.
- **Gate:** `AubsCraft.Viewer.Tests` (Playwright, installed Chrome) shoots the real viewer; see PLANS.md Phase V.

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

**The client is AOT compiled** (`RunAOTCompilation` + `WasmStripILAfterAOT=false` so ILGPU still has IL to compile
kernels from, + `WasmDedup=false` for a ~7 minute publish instead of an hour). `deploy-aubscraft.bat` deletes the
client's Release bin/obj first so every AOT build is clean. `dotnet build` (Debug) is not AOT and stays fast.

**Always check the build timestamp** in the browser console on startup to verify you're running the latest code.

## Project Structure

```
AubsCraft/
  AubsCraft.Admin/           # Blazor WASM frontend
    Pages/                   # Razor pages (Map.razor = 3D viewer)
    Services/                # Client services (GpuContextService, RenderWorkerService)
    Rendering/               # MapRenderService (loop), VoxelWorldRenderer (the world), ViewerHud, camera, block tables
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
| `Rendering/MapRenderService.cs` | The frame loop and canvas: camera, day/night clock, asks the voxel renderer to draw, then the HUD |
| `Rendering/VoxelWorldRenderer.cs` | The world: columns + far chunks meshed on the GPU, drawn by VoxelEngine; the minimap texture |
| `Rendering/VoxelBlockTables.cs` | Block names -> mesh class and material (textures, tint, water) |
| `Rendering/ViewerHud.cs` | The GPU HUD (SpawnDev.GameUI) |
| `Services/GpuContextService.cs` | ILGPU context + accelerator |
| `Services/RenderWorkerService.cs` | Web Worker orchestration: column streaming, data worker, HUD feed |
| `Services/WorldCacheService.cs` | OPFS: the camera position per server |
| `Server/Services/ServerMonitorService.cs` | Server status polling (3s cycle), SignalR push |
| `Server/Hubs/ServerHub.cs` | SignalR hub - structured data push to clients |
| `Server/Program.cs` | Server startup, HTTP endpoints, WebSocket setup |

## SpawnDev Libraries Used

- **SpawnDev.SpawnJS** (+ SpawnDev.SpawnJS.Blazor) - JS interop (never write raw JavaScript). Ported from SpawnDev.BlazorJS 2026-10-02.
- **SpawnDev.SpawnJS.WebWorkers** - the render worker (`RenderWorkerHost` / `RenderWorkerService`)
- **SpawnDev.SpawnJS.TangoADB** - ADB over WebUSB for the `/quest` installer
- SpawnJS handles are MANUAL: dispose every wrapper you get back (`using var layout = pipeline.GetBindGroupLayout(0);`).
- `@ref` elements: `ElementRef<HTMLCanvasElement>` field + `.Get()` (SpawnDev.SpawnJS.Blazor).
- **SpawnDev.ILGPU** - GPU compute (WebGPU backend)
- **SpawnDev.VoxelEngine** - the voxel renderer and meshers (local feed builds until a release)
- **SpawnDev.GameUI** - the HUD
- Use DI correctly - never bypass the service container

### Banned Patterns - NO EXCEPTIONS

- **NEVER use `eval()`** - SpawnDev.SpawnJS has typed wrappers for every browser API
- **NEVER use `IJSRuntime`** - always inject `SpawnJSRuntime`. IJSRuntime requires Captain's explicit consent.
- **NEVER use `window.__globals`** to pass state between JS and .NET - use C# fields and SpawnJS typed properties
- **NEVER use `AddEventListener` with raw strings** - use ActionEvent properties (OnPointerLockChange, OnClick, etc.)
- **NEVER call JS every frame** for state that can be tracked by an event handler updating a C# field

**If you are unsure how to do something with SpawnDev.SpawnJS or any SpawnDev library - ASK TJ.** He wrote them. He knows every API. Guessing leads to eval() and IJSRuntime hacks. Asking takes 30 seconds and gets the right answer.

## Reference Codebase

Lost Spawns at `D:\users\tj\Projects\Lost\Lost\LostSpawns` has proven render patterns (VoxelMesher, Camera, FrustumCuller).
