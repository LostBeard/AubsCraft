# WebXR VR Deep Dive - Technical Planning

**Date:** 2026-04-12
**Status:** Research phase - architectural planning before implementation

---

## 1. WebXR API Overview

WebXR is the browser standard for VR and AR. It replaces the deprecated WebVR API. Supported in:
- Chrome 79+ (desktop + Android)
- Edge 79+
- Meta Quest Browser (standalone VR - this is our primary target)
- Samsung Internet
- NOT supported in Firefox (removed), Safari (no plans)

### Key WebXR Concepts

**XRSession**: The VR session. Two types:
- `immersive-vr` - full VR headset takeover (what we want)
- `inline` - renders in a page element (useful for "preview" mode)

**XRReferenceSpace**: The coordinate system
- `local` - seated, small area (good for "tabletop" world view)
- `local-floor` - standing, floor-relative (good for room-scale)
- `bounded-floor` - guardian/boundary aware
- `unbounded` - large area, GPS-based (AR)

**XRFrame**: Each VR frame provides:
- Head pose (position + orientation)
- Controller poses (left + right hand)
- Input state (trigger, grip, thumbstick, buttons)

**XRWebGLLayer / XRGPUBinding**: Renders to the headset
- WebGL: `XRWebGLLayer` wraps a WebGL framebuffer for the headset
- WebGPU: `XRGPUBinding` (newer) provides GPU textures for each eye
- **We need WebGPU path** since our renderer is WebGPU-based

---

## 2. WebGPU + WebXR Integration

This is cutting-edge - WebGPU WebXR support is still evolving.

### Current state (as of early 2026)
- Chrome Canary has `XRGPUBinding` behind a flag
- The spec is at: https://immersive-web.github.io/WebXR-WebGPU-Binding/
- Quest browser may need updates to support this

### The rendering loop changes
Current (flat screen):
```
requestAnimationFrame -> render to canvas
```

VR mode:
```
xrSession.requestAnimationFrame -> get XRFrame -> get views (left eye, right eye)
  -> for each view:
     -> get projection matrix from XRView
     -> get view matrix from XRViewerPose
     -> render to XRGPUBinding texture for that eye
```

### Performance implications - CRITICAL
- **Double rendering**: VR renders the scene TWICE (one per eye)
- Our current 60 FPS at 1080p becomes 2x renders at 90 FPS at higher resolution per eye
- Quest 2: 1832x1920 per eye at 72/90 Hz = ~7M pixels/frame vs ~2M for 1080p flat
- That's roughly **7x the pixel throughput** needed
- Our ILGPU kernel runs once (mesh generation is view-independent) but the draw calls double
- Frustum culling needs per-eye frustums (slight offset)
- The uniform buffer needs per-eye MVP matrices

### Optimization strategies for VR performance
1. **Multi-view rendering**: Render both eyes in one pass with `OVR_multiview2` extension
   - Not available in WebGPU yet, but the spec is discussing it
   - Halves draw calls compared to naive two-pass
2. **Foveated rendering**: Lower resolution in peripheral vision
   - Quest 2/3 supports fixed foveated rendering at the browser level
   - We don't need to implement this - the browser/OS handles it
3. **Aggressive LOD**: Far chunks use heightmap (fewer triangles), nearby use full 3D
   - We already do this! Heightmap vs full 3D kernel is our LOD system
4. **Reduced draw distance in VR**: Start with smaller draw distance, adaptive
5. **Instanced rendering**: If we add repeated elements (fences, torches), instance them
6. **Vertex budget**: Target <2M triangles per frame for Quest standalone
   - Current: ~250K vertices visible = ~83K triangles. Well under budget.

---

## 3. SpawnDev.BlazorJS WebXR Wrappers - ALREADY EXIST

**CRITICAL FINDING: All WebXR wrappers already exist in SpawnDev.BlazorJS!**

Location: `D:\users\tj\Projects\SpawnDev.BlazorJS\SpawnDev.BlazorJS\SpawnDev.BlazorJS\JSObjects\WebXR\`

**65+ wrapper classes** covering the ENTIRE WebXR Device API including:
- Session management: XRSystem, XRSession, XRSessionInit, XRSessionMode
- Spatial tracking: XRReferenceSpace, XRFrame, XRViewerPose, XRView, XRRigidTransform
- Input: XRInputSource, XRInputSourceEvent, XRHand, XRJointSpace, XRJointPose
- Rendering: XRWebGLLayer, XRWebGLBinding, XRGPUBinding, XRGPUSubImage, XRProjectionLayer
- Hit testing: XRHitTestSource, XRHitTestResult, XRTransientInputHitTestSource
- AR: XRAnchor, XRPlane, XRMesh, XRRay, XRLightProbe, XRLightEstimate
- Depth: XRCPUDepthInformation, XRWebGLDepthInformation, XRDepthInformation
- Layers: XRCompositionLayer, XRCubeLayer, XRCylinderLayer, XREquirectLayer, XRQuadLayer
- Camera: XRCamera (for camera access feature)

**NO NEW WRAPPERS NEEDED.** Phase Q1 from the implementation plan is already DONE.

### Working demos in SpawnDev.BlazorJS.THREE

Three working demos at `D:\users\tj\Projects\SpawnDev.BlazorJS.ThreeJS\...\Demo\Pages\`:

1. **VRDemo.razor.cs** (`/ARCubeDemo`) - Spinning colored cube in AR with hit-testing
2. **ARDemoLighting.razor.cs** (`/ARDemoLighting`) - Triangle placed at hit-test surfaces
3. **VRDemo2.razor.cs** (`/VRDemo2`) - MANUAL WebXR rendering (bypasses THREE.js XR manager)

**VRDemo2 is our reference** - it does manual framebuffer binding, viewer pose extraction, and camera matrix setup. This is closest to what we need (we bypass THREE.js too).

### Session creation pattern (proven, from VRDemo.razor.cs)
```csharp
using var navigator = JS.Get<Navigator>("navigator");
using var xr = navigator.XR;
var session = await xr.RequestSession("immersive-vr", new XRSessionInit {
    RequiredFeatures = ["local-floor"]
});
referenceSpace = await session.RequestReferenceSpace(XRReferenceSpaceType.LocalFloor);
```

### Manual render loop pattern (from VRDemo2.razor.cs)
```csharp
// Setup: camera.MatrixAutoUpdate = false
// In XR render loop:
gl.BindFramebuffer(gl.FRAMEBUFFER, session.RenderState.BaseLayer.Framebuffer);
var pose = frame.GetViewerPose(referenceSpace);
var view = pose.Views[0]; // left eye (or iterate for both)
var viewport = session.RenderState.BaseLayer.GetViewport(view);
camera.Matrix.FromArray(view.Transform.Matrix);
camera.ProjectionMatrix.FromArray(view.ProjectionMatrix);
camera.UpdateMatrixWorld(true);
renderer.Render(scene, camera);
```

### Key integration point: WebGPU instead of WebGL
The demos use WebGL (`XRWebGLLayer`). For AubsCraft we need `XRGPUBinding` (WebGPU).
The wrapper EXISTS (`XRGPUBinding.cs`, `XRGPUSubImage.cs`) but hasn't been tested in a demo.
This is the one unknown - does Quest browser support `XRGPUBinding` yet?

### Fallback plan
If `XRGPUBinding` isn't available on Quest:
- Use `XRWebGLLayer` with a WebGL2 context
- SpawnDev.ILGPU supports WebGL backend
- Render to WebGL framebuffer instead of WebGPU texture
- Same ILGPU kernel, different GPU backend - zero code changes in the meshing pipeline

---

## 4. VR Interaction Design

### Navigation in VR

**Fly mode (default)**:
- Left thumbstick: move forward/back/strafe (relative to head direction)
- Right thumbstick: snap turn (45-degree increments, avoids motion sickness)
- Grip buttons: speed boost while held
- No smooth rotation - snap turns only (VR comfort best practice)
- Movement speed: adjustable, default slower than flat mode

**Teleport mode (comfort option)**:
- Point controller, see arc trajectory, release to teleport
- Instant camera reposition, no smooth movement
- Best for VR-sensitive users
- Visualize the arc with a line renderer in the WebGPU shader

**Scale mode (god view)**:
- Pinch gesture (both grips) to zoom in/out
- The world shrinks/grows around the player
- At smallest scale, the entire world fits on a virtual table in front of you
- At largest scale, you're standing inside the world at 1:1 Minecraft scale
- Drag the world with grip to pan (like Google Earth VR)

### Block interaction in VR

**Selection**:
- Point controller at a block, laser beam shows the ray
- Highlighted block face glows
- Block info appears as a floating tooltip near the controller

**Placement (creative mode)**:
- Block palette on the non-dominant hand (like a virtual wrist menu)
- Select a block type by touching it with the other controller
- Point at target face, trigger to place
- Haptic feedback on placement (controller vibration)

**Removal**:
- Point at block, grip button to remove
- Ghost effect (block fades out) before committing
- Undo: button on controller reverses last N actions

### VR UI

**Floating panels**:
- Claim info, player list, chat - rendered as floating panels in 3D space
- Panels follow the player at a comfortable distance
- Pin panels in world space or attach to wrist
- Rendered as quads in the WebGPU scene with text via SDF font rendering or HTML overlay

**Wrist menu**:
- Look at your non-dominant wrist to open the menu (natural gesture)
- Contains: block palette, settings, teleport list, player list
- This is a proven VR UI pattern (Half-Life: Alyx, Population: One)

**Comfort settings**:
- Snap turn angle (30/45/60/90)
- Movement speed
- Vignette on movement (reduces peripheral vision during motion, reduces sickness)
- Height calibration
- Dominant hand selection

---

## 5. Performance Budget for Quest Standalone

Meta Quest 2/3 runs a mobile Snapdragon GPU. Performance constraints:

| Resource | Budget | Our current use |
|----------|--------|----------------|
| Triangles/frame | 750K-1M | ~83K (well under) |
| Draw calls/frame | 100-200 | ~30-50 visible chunks (under) |
| Texture memory | 256MB | ~256KB atlas (negligible) |
| Frame time | 11ms (90Hz) or 13.8ms (72Hz) | ~17ms flat (needs optimization) |
| Vertex buffer | 64MB | ~30M verts x 44B = ~1.3GB (TOO HIGH for Quest) |

### Critical issue: Vertex buffer size
Our 30M vertex buffer is 1.3GB - way too large for Quest standalone. Solutions:
- **Dynamic vertex budget**: On Quest, limit to 5M vertices max (~220MB)
- **Aggressive LOD**: Only full 3D for 1-2 chunk radius in VR, heightmap for everything else
- **Chunk unloading**: Unload chunks behind the player (VR has a natural forward focus)
- **Detect platform**: Check `navigator.xr.isSessionSupported('immersive-vr')` and `navigator.userAgent` for Quest, reduce budgets accordingly

### Frame time budget (11ms for 90Hz)
- GPU mesh generation: 0ms (pre-computed, not per-frame)
- Uniform buffer update: <0.1ms
- Frustum culling: ~0.5ms (CPU)
- Draw calls (2 passes x 2 eyes): ~4-6ms (biggest cost)
- Water transparent pass: ~1-2ms
- Present/compositor: ~1-2ms
- Headroom: ~2-3ms

This is tight but doable if we keep draw calls under 100 and triangles under 500K.

---

## 6. Implementation Phases (detailed)

### Q1. WebXR wrappers (SpawnDev.BlazorJS)
- [ ] XRSystem, XRSession, XRSessionInit
- [ ] XRReferenceSpace, XRFrame, XRViewerPose, XRView
- [ ] XRRigidTransform, XRSpace
- [ ] XRGPUBinding, XRProjectionLayer, XRSubImage
- [ ] Basic "enter VR" / "exit VR" lifecycle
- [ ] Unit tests in SpawnDev.BlazorJS.Test

### Q2. VR rendering
- [ ] "Enter VR" button on the map page
- [ ] Dual-eye rendering (render scene twice with per-eye MVP)
- [ ] Per-eye frustum culling
- [ ] XRGPUBinding texture output
- [ ] Verify on Quest browser
- [ ] Performance profiling: FPS, frame time, draw calls

### Q3. VR navigation
- [ ] Controller input reading (XRInputSource)
- [ ] Fly mode (thumbstick movement)
- [ ] Snap turn (right thumbstick)
- [ ] Teleport mode (arc + trigger)
- [ ] Speed adjustment

### Q4. VR interaction
- [ ] Controller laser pointer (ray from controller)
- [ ] Block face highlighting (glow effect)
- [ ] Block info tooltip
- [ ] Haptic feedback (controller vibration)

### Q5. VR admin/god mode
- [ ] Scale mode (pinch to zoom world)
- [ ] Drag to pan (grip to move world)
- [ ] Floating info panels (claims, players)
- [ ] Voice commands via Web Speech API

### Q6. VR creative mode (base building)
- [ ] Wrist menu with block palette
- [ ] Place blocks with trigger
- [ ] Remove blocks with grip
- [ ] Ghost block preview
- [ ] Claim boundary visualization
- [ ] Undo/redo

### Q7. VR comfort + polish
- [ ] Vignette on movement
- [ ] Height calibration
- [ ] Dominant hand selection
- [ ] Snap turn angle options
- [ ] Performance auto-adjust (reduce draw distance if FPS drops)

### Q8. Mixed reality (Quest 3)
- [ ] Passthrough camera background
- [ ] World as a floating 3D model on your real desk
- [ ] Tap real surfaces to place the world model
- [ ] XRPlane detection for surface placement

---

## 7. Questions to Resolve

1. **WebGPU + WebXR**: Is `XRGPUBinding` available in Quest Browser yet? If not, do we need a WebGL fallback for VR? (Check at implementation time)

2. **SpawnDev.BlazorJS or separate package?**: Should WebXR wrappers live in the core SpawnDev.BlazorJS package or a separate `SpawnDev.BlazorJS.WebXR` NuGet? Separate is cleaner but adds a dependency.

3. **ILGPU in VR**: The kernel generates meshes once, then the renderer draws them per-eye. No kernel changes needed for VR - just the rendering pipeline. Correct?

4. **Quest browser performance**: Need real-world testing. The Quest browser runs Chromium but with lower GPU power. Our heightmap-only view (no full 3D) might be the right default for Quest.

5. **Hand tracking vs controllers**: Quest 3 has excellent hand tracking. Should we support both? Hand tracking is more natural but less precise for block placement.

---

## 8. More Player-Wanted Features (Research)

Based on what Minecraft players commonly want from companion apps:

### Map features players love
- **Seed-based structure finder**: Know the world seed, predict village/temple/fortress locations
- **Chunk border visualization**: Show chunk boundaries (important for farms/redstone)
- **Slime chunk finder**: Seed-based calculation of which chunks spawn slimes
- **Spawn chunk indicator**: Show the always-loaded spawn chunks
- **Light level overlay**: Show where mobs can spawn (light level < 7) - useful for base defense
- **Ore X-ray mode**: Toggle to see only ores through terrain (admin tool for finding cheaters)

### Social features players love
- **Build competition viewer**: Admin sets up a build area, players build, viewers vote from the web
- **Server tour**: Pre-recorded camera paths that new players can "ride" to see the server's best builds
- **Photo mode**: Freeze the camera, add filters/effects, export as image, share URL
- **Time-lapse**: Record the world state over time, play back as an animation showing how the server evolved
- **Guest view**: Non-authenticated users can view the map (read-only, no admin features)

### Utility features
- **Coordinate calculator**: Overworld <-> Nether coordinate converter (divide/multiply by 8)
- **Distance calculator**: Click two points, see distance in blocks and estimated walk time
- **Crop growth timer**: Track farm growth status based on game tick data
- **Redstone viewer**: Highlight powered vs unpowered redstone (if we have block state data)
- **Mob spawner finder**: Scan loaded chunks for spawner blocks, show locations on map

### Competitive features
- **Leaderboards**: Most blocks placed, most time played, most deaths, most mobs killed
- **Achievement race**: Track who gets achievements first, show progress comparison
- **Build ratings**: Players rate each other's builds, top builds featured on the homepage
- **Territory control**: Game mode where factions claim and defend territory, visualized on the map

---

## 9. Performance-First Design Principles for All Features

Every feature should be evaluated against these:

1. **Zero GPU transfers unless rendering**: Data enters the GPU once as vertex buffers. Never read back.
2. **JS for I/O, .NET for logic, ILGPU for compute**: Each sandbox does what it's best at.
3. **IndexedDB is the source of truth**: The cache is always valid. Server updates are deltas.
4. **Lazy loading**: Don't compute or render what's not visible. Frustum cull aggressively.
5. **Budget-aware**: Track vertex count, draw calls, texture memory. Warn when limits approach.
6. **Platform-adaptive**: Detect Quest vs desktop, adjust quality automatically.
7. **Frame-time accounting**: Every feature has a frame-time cost. Know it. Budget it.
8. **Batch operations**: Group similar draw calls. Minimize state changes between draws.
9. **Async everything**: Never block the render thread. Network, cache, parsing - all async.
10. **Measure, don't guess**: Profile before optimizing. The bottleneck is never where you think.
