# SpawnScene UI Architecture - Reference for AubsCraft

**Date:** 2026-04-13
**Source:** SpawnScene at D:/users/tj/Projects/SpawnScene/SpawnScene/SpawnScene/UI/

---

## Key Pattern: Entire UI is GPU-Rendered via WebGPU - Zero HTML

SpawnScene's UI is a custom retained-mode tree rendered entirely via WebGPU. NO Blazor HTML components. NO DOM overlays on the canvas.

---

## Component Inventory

| File | Purpose |
|------|---------|
| `UIElement.cs` | Base class - retained-mode tree, parent-child, hit testing, bounds |
| `UIRenderer.cs` | Batched 2D quad renderer (4096 quads, single draw call, LoadOp.Load preserves 3D scene) |
| `UIShaders.cs` | WGSL vertex+fragment shader (screen pixel coords to NDC, solid color or atlas text) |
| `FontAtlas.cs` | Runtime bitmap font atlas via OffscreenCanvas (1024x1024, 4 sizes: 12/16/24/32px) |
| `InputManager.cs` | Polling-based input (mouse/keyboard/gamepad, pending buffer pattern) |
| `UIButton.cs` | Clickable button with hover/pressed states, auto-size, OnClick callback |
| `UILabel.cs` | Text label with auto-sizing, configurable font size and color |
| `UIPanel.cs` | Container with background + border, child positioning |
| `UISlider.cs` | Horizontal drag slider with value label, OnChanged callback |
| `UIImage.cs` | GPU texture display element |

## UIRenderer Internals

- Up to 4096 quads per frame (6 vertices each, 8 floats per vertex: pos2 + uv2 + color4)
- Single vertex buffer + single draw call for the main batch
- Images get separate draw calls (one bind group per unique texture)
- Render pass uses `LoadOp.Load` to preserve 3D scene underneath
- Methods: `DrawRect()`, `DrawText()`, `DrawImage()`, `MeasureText()`

## Font System

- 1024x1024 bitmap atlas generated at runtime via OffscreenCanvas
- 4 font sizes: Caption (12px), Body (16px), Heading (24px), Title (32px)
- White glyphs on transparent background, tinted by vertex color
- ASCII range 32-126

## Vertex Shader (2D Screen Space)

```wgsl
let ndc_x = input.pos.x / u.viewport.x * 2.0 - 1.0;
let ndc_y = 1.0 - input.pos.y / u.viewport.y * 2.0;
out.clip_pos = vec4<f32>(ndc_x, ndc_y, 0.0, 1.0);
```

**For VR:** Replace with model-view-projection transform for world-space panels.

## WebXR Integration (XRService)

**Dual-path rendering:**
1. Try `XRGPUBinding` (native WebGPU XR) - renders directly to XR GPU textures
2. Fallback: `XRWebGLLayer` + `WebGLXRBlit` bridge (WebGPU renders to OffscreenCanvas, blit to WebGL XR compositor)

**Per-frame flow:**
1. `session.RequestAnimationFrame()` fires callback
2. Extract `XRViewerPose` with per-eye views
3. For each eye: get view matrix, projection matrix, color/depth textures
4. Render scene with per-eye MVP
5. Matrix conversion: WebXR column-major -> System.Numerics row-major

**Render loop exclusion:** Canvas RAF and XR RAF never run simultaneously. `_xrActive` flag gates the canvas loop.

**Reference space:** `"local-floor"` for standing VR.

## What AubsCraft Needs to Adapt

1. **3D-space UI panels** - Current UIRenderer is 2D screen-space (z=0). VR needs world-space vertex shader with model-view-projection per panel. Quad batching and font atlas stay the same.
2. **Per-eye rendering** - Each eye needs its own frustum culling + draw calls with its own MVP matrix.
3. **XR input** - InputManager needs XRInputSource (thumbstick, trigger, grip). BlazorJS has XRInputSource, XRHand, XRJointSpace wrappers.
4. **Performance** - Quest budget: ~750K-1M triangles, 11ms frame time at 90Hz. Dynamic vertex budget essential.

## Directly Reusable Components

- XRService (session management, matrix conversion)
- WebGLXRBlit (WebGPU-to-WebGL bridge)
- UIElement tree (base class, hit testing, parent-child)
- UIRenderer (batched quad renderer - extend for 3D projection)
- FontAtlas (runtime bitmap font generation)
- InputManager (extend for XR controllers)
- All UI elements (Button, Label, Panel, Slider, Image)
