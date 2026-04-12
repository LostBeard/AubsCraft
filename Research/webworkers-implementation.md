# Web Worker Implementation Plan

**Date:** 2026-04-12
**Status:** Research complete, ready for implementation

## SpawnDev.BlazorJS.WebWorkers Key Concepts

### Setup
```csharp
// Program.cs
builder.Services.AddBlazorJSRuntime();
builder.Services.AddWebWorkerService(config =>
{
    config.TaskPool.MaxPoolSize = -1; // = navigator.hardwareConcurrency
    config.TaskPool.PoolSize = config.GlobalScope == GlobalScope.Window ? 2 : 0;
});
```

### Core Pattern
- Workers are separate instances of the Blazor WASM app
- All registered services are available in workers
- Call services in workers via expressions, delegates, or interface proxies
- TaskPool provides easy background thread access
- `WebWorkerService.TaskPool.Run(() => service.Method(args))` runs in a worker

### Transferable Objects (critical for performance)
- `OffscreenCanvas` - transferable, **required** (detaches from sender)
- `ArrayBuffer` - transferable (ownership moves to receiver)
- `[WorkerTransfer]` attribute controls transfer behavior on parameters/returns
- `[TransferableList]` for explicit transfer list

### OffscreenCanvas Pattern
```csharp
// Main thread: create OffscreenCanvas from HTML canvas, transfer to worker
using var offscreenCanvas = new OffscreenCanvas(width, height);
// OR: canvas.TransferControlToOffscreen() for existing HTML canvas
await worker.Run(() => RenderService.Init(offscreenCanvas));
// offscreenCanvas is now detached on main thread - worker owns it
```

### Worker-to-Main Communication
```csharp
// Worker calls back to Window
await WebWorkerService.WindowTask.Run(() => SomeWindowMethod(data));
```

## AubsCraft Architecture

### Worker 1: Render Worker
- Owns: OffscreenCanvas, WebGPU device, MapRenderService, ILGPU accelerator
- Does: All GPU dispatch (heightmap kernel, mesh kernel), WebGPU rendering
- Receives: chunk data (transferred ArrayBuffer), camera updates
- Sends back: FPS stats, visible chunk count

### Worker 2: Data Worker (optional, future)
- Owns: WebSocket connection, OPFS cache
- Does: Binary WebSocket receive, OPFS region file read/write
- Sends: chunk data to render worker (transferred ArrayBuffer)

### Main Thread
- Owns: Blazor UI components, input handling, StateHasChanged
- Does: Input events, UI updates, camera position tracking
- Sends: camera position, input state to render worker

## Implementation Steps

### Step 1: Add WebWorkerService to AubsCraft
```csharp
// Program.cs
builder.Services.AddWebWorkerService(config =>
{
    config.TaskPool.MaxPoolSize = 2;
    config.TaskPool.PoolSize = config.GlobalScope == GlobalScope.Window ? 1 : 0;
});
```

### Step 2: Create IRenderWorkerService interface
Define the interface for render operations that run in the worker.

### Step 3: Transfer OffscreenCanvas to worker
```csharp
// In Map.razor after canvas is created:
var offscreen = canvasElement.TransferControlToOffscreen();
await worker.New<IRenderWorkerService>(() => new RenderWorkerService(offscreen));
```

### Step 4: Move rendering loop to worker
The render worker runs requestAnimationFrame on OffscreenCanvas, dispatches ILGPU kernels, uploads meshes.

### Step 5: Main thread sends input
Camera position, mouse delta, keyboard state sent to worker via expressions or events.

## Key Concerns

1. **WebGPU in workers** - `navigator.gpu` available in dedicated workers. ILGPU accelerator creation works.
2. **SharedArrayBuffer** - not required by WebWorkers, but useful for shared data. Requires cross-origin headers.
3. **Serialization** - data passed via postMessage is serialized. Use transferable objects for large data.
4. **Service registration** - all services registered in Program.cs are available in workers automatically.
5. **GlobalScope check** - use `WebWorkerService.GlobalScope` to conditionally register UI-only components.

## SpawnDev.BlazorJS.WebWorkers Source
- Package: NuGet SpawnDev.BlazorJS.WebWorkers
- README: `D:\users\tj\Projects\SpawnDev.BlazorJS.WebWorkers\SpawnDev.BlazorJS.WebWorkers\README.md`
- Demo: `SpawnDev.BlazorJS.WebWorkers.Demo` project in same repo
- Tests: `UnitTestsService.cs` has OffscreenCanvas transfer tests
- Example projects: `D:\users\tj\Projects\SharedWebWorkerExample\`, `D:\users\tj\Projects\BlazorJSWebWorkerFluentUITest\`
