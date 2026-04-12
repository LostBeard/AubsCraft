# WebXR + WebGPU Binding - Spec Status and Browser Support

**Date:** 2026-04-12
**Source:** W3C Editor's Draft (https://immersive-web.github.io/WebXR-WebGPU-Binding/)

---

## Spec Status

**Editor's Draft** as of March 31, 2026. NOT a W3C Recommendation yet. "Under development and may change."

## Key Interfaces

### XRGPUBinding (main entry point)
- Requires XRSession created with `"webgpu"` feature
- Requires GPUDevice requested with `xrCompatible: true`
- Methods:
  - `createProjectionLayer()` - allocates stereo color/depth textures
  - `getViewSubImage(layer, view)` - get texture for specific eye
  - `getPreferredColorFormat()` - recommended texture format
  - Methods for quad/cylinder/equirect/cube layers (experimental)

### XRGPUSubImage
- Contains color and optional depth/stencil textures per eye
- `getViewDescriptor()` returns descriptor selecting array layer (left=0, right=1)

### Rendering pattern (from spec)
```javascript
const subImage = binding.getViewSubImage(layer, view);
const viewDesc = subImage.getViewDescriptor();
const colorView = subImage.colorTexture.createView(viewDesc);
// Use colorView as color attachment in render pass
```

## Session setup (from spec)
```javascript
// Request session with webgpu feature
const session = await navigator.xr.requestSession("immersive-vr", {
  requiredFeatures: ["webgpu"]
});

// Get XR-compatible GPU device
const adapter = await navigator.gpu.requestAdapter({ xrCompatible: true });
const device = await adapter.requestDevice();

// Create binding
const binding = new XRGPUBinding(session, device);
const layer = binding.createProjectionLayer({
  colorFormat: binding.getPreferredColorFormat()
});
session.updateRenderState({ layers: [layer] });
```

## Browser Support

**UNKNOWN - needs testing.** Key questions:
1. Does Chrome stable support `XRGPUBinding`? (Likely behind origin trial or flag)
2. Does Quest Browser support it? (Probably not yet)
3. Is there a polyfill?

**Non-projection layers:** "Not yet supported by any user agent" per the spec.

## Fallback Plan

If XRGPUBinding isn't available:
- Use `XRWebGLLayer` with WebGL2 context (proven in TJ's THREE.js demos)
- SpawnDev.ILGPU supports WebGL backend
- Same ILGPU kernel, different GPU backend = zero meshing code changes
- Only the render output target changes (WebGL framebuffer vs WebGPU texture)

## SpawnDev.BlazorJS WebXR Wrappers

**Already exist:** 65+ classes at `SpawnDev.BlazorJS\JSObjects\WebXR\`
Including `XRGPUBinding.cs`, `XRGPUSubImage.cs`, `XRProjectionLayer.cs`
These wrappers exist but the WebGPU path hasn't been tested in a demo yet.

## What We Need to Test (before implementation)

1. Open Quest Browser, navigate to a test page
2. Check `navigator.xr.isSessionSupported('immersive-vr')` - should be true
3. Check if `navigator.gpu` exists on Quest Browser
4. Try creating a session with `requiredFeatures: ['webgpu']` - will it fail?
5. If WebGPU XR fails, try WebGL fallback path (VRDemo2 pattern)

## References
- Spec: https://immersive-web.github.io/WebXR-WebGPU-Binding/
- MDN WebXR: https://developer.mozilla.org/en-US/docs/Web/API/WebXR_Device_API
- TJ's demos: `SpawnDev.BlazorJS.ThreeJS\...\Demo\Pages\VRDemo2.razor.cs` (manual WebXR)
- TJ's wrappers: `SpawnDev.BlazorJS\...\JSObjects\WebXR\` (65+ classes)
