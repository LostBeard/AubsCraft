# UI Layout and Control Schemes

**Date:** 2026-04-12
**Covers:** PC (keyboard+mouse), Gamepad (controller), Mobile (touch), VR (WebXR controllers)

---

## 1. Gamepad API Reference

Source: MDN (https://developer.mozilla.org/en-US/docs/Web/API/Gamepad_API/Using_the_Gamepad_API)

### Detection
```javascript
window.addEventListener("gamepadconnected", (e) => {
    // e.gamepad.index, e.gamepad.id, e.gamepad.buttons.length, e.gamepad.axes.length
});
```

### Polling (in render loop)
```javascript
const gp = navigator.getGamepads()[0];
// Axes: gp.axes[0..3] = left stick X/Y, right stick X/Y (-1.0 to 1.0)
// Buttons: gp.buttons[0..15].pressed (bool) and .value (0.0-1.0 for triggers)
```

### Standard Button Mapping
| Index | Xbox | PlayStation | Action in AubsCraft |
|-------|------|------------|-------------------|
| 0 | A | Cross | Select / Interact |
| 1 | B | Circle | Back / Cancel |
| 2 | X | Square | Block palette / Quick action |
| 3 | Y | Triangle | Toggle UI panels |
| 4 | LB | L1 | Descend / Previous tool |
| 5 | RB | R1 | Ascend / Next tool |
| 6 | LT | L2 | Speed boost (analog) |
| 7 | RT | R2 | Place block (analog) |
| 8 | Back | Share | Toggle minimap |
| 9 | Start | Options | Pause menu / Settings |
| 10 | L3 (stick press) | L3 | Sprint toggle |
| 11 | R3 (stick press) | R3 | Toggle pointer lock |
| 12-15 | D-pad | D-pad | Quick hotbar / Camera modes |

### Axes
| Axis | Control | Action |
|------|---------|--------|
| 0 | Left stick X | Strafe left/right |
| 1 | Left stick Y | Move forward/back |
| 2 | Right stick X | Look left/right (yaw) |
| 3 | Right stick Y | Look up/down (pitch) |

### Dead zone
Apply a dead zone of ~0.15 to axes to prevent drift:
```csharp
float ApplyDeadzone(float value, float threshold = 0.15f)
    => MathF.Abs(value) < threshold ? 0f : value;
```

### SpawnDev.BlazorJS Gamepad wrapping
Check if `Gamepad`, `GamepadEvent` wrappers exist in SpawnDev.BlazorJS.
If not, the wrapping is minimal - just poll `navigator.getGamepads()` each frame.

---

## 2. PC Layout (Keyboard + Mouse)

### Current controls
| Key | Action |
|-----|--------|
| W/A/S/D | Move forward/left/back/right |
| Space | Ascend |
| Shift | Descend |
| Mouse move | Look (when pointer locked) |
| Click canvas | Lock pointer |
| Escape | Unlock pointer |

### Planned additions
| Key | Action |
|-----|--------|
| Tab | Toggle player list overlay |
| M | Toggle minimap |
| C | Toggle coordinates display |
| T | Open chat |
| F | Toggle fly speed (slow/fast) |
| F2 | Screenshot (capture canvas as PNG) |
| F5 | Toggle camera mode (first-person / orbit / top-down) |
| 1-9 | Block palette quick select (in edit mode) |
| E | Open block palette (in edit mode) |
| Q | Toggle edit mode (if permitted) |
| R | Undo last edit |
| / | Command input (like Minecraft) |
| Ctrl+Click | Block info (show block type, coordinates) |

### UI panels (always visible)
```
+--------------------------------------------------+
| [Logo] AubsCraft Admin    [Player] [Light] [VR]  |
|                                                    |
| Sidebar:    Main viewport:                         |
| Dashboard   +----------------------------------+   |
| Players     |                                  |   |
| World       |         3D World View            |   |
| Stats       |                                  |   |
| Plugins     |  [Compass]            [Minimap]  |   |
| Map  <--    |                                  |   |
| Activity    |  [Coords: 228, 64, -243]         |   |
| Console     |                                  |   |
|             |  [FPS: 60] [Chunks: 3101]        |   |
|             +----------------------------------+   |
|             [Chat input________________________]   |
+--------------------------------------------------+
```

### Collapsible sidebar
- Click hamburger or press Tab to collapse sidebar
- In fullscreen/streaming mode: sidebar hidden entirely
- Sidebar state remembered across sessions

---

## 3. Gamepad Layout

### Navigation
- **Left stick**: Move (forward/back/strafe)
- **Right stick**: Look (yaw/pitch)
- **LT (analog)**: Speed boost (proportional)
- **LB/RB**: Ascend/Descend
- **L3**: Sprint toggle

### Interaction
- **A**: Select/Interact (click on claim, player, block)
- **B**: Back/Close panel
- **X**: Quick action (place block in edit mode)
- **Y**: Toggle UI panels

### Camera modes (D-pad)
- **D-up**: Free fly (default)
- **D-right**: Orbit mode
- **D-down**: Top-down view
- **D-left**: Follow player mode

### Menu
- **Start**: Settings/pause
- **Back**: Toggle minimap + coordinates

### Gamepad UI cursor
When gamepad is active and pointer lock is off:
- Right stick controls a virtual cursor
- A button clicks
- Navigate menus with D-pad
- Cursor auto-hides when pointer lock is active (3D navigation mode)

### Detection and auto-switch
```csharp
// In the render loop, check for gamepad
var gamepads = JS.Call<Gamepad[]>("navigator.getGamepads");
if (gamepads?.Length > 0 && gamepads[0] != null)
{
    // Gamepad connected - read axes and buttons
    ProcessGamepadInput(gamepads[0]);
}
```

Auto-detect: if gamepad input is detected, show gamepad-friendly UI (larger buttons, controller prompts). If keyboard/mouse detected, switch back to PC UI.

---

## 4. Mobile Layout (Touch)

### Touch zones
```
+----------------------------------+
|  [Menu]  [Status bar]  [Cam]    |  <- Top bar (thin)
|                                  |
|                                  |
|     3D World View                |
|     (full screen)                |
|                                  |
|                                  |
|  [Joystick]        [Buttons]    |  <- Bottom overlay
|  (left thumb)      (right side) |
+----------------------------------+
```

### Touch controls
| Gesture | Action |
|---------|--------|
| One finger drag (center) | Look (rotate camera) |
| Two finger pinch | Zoom in/out |
| Two finger drag | Pan camera |
| Tap | Select (block, player, claim) |
| Long press | Context menu (info, teleport, bookmark) |
| Virtual joystick (left) | Move forward/back/strafe |

### Virtual joystick
- Appears where left thumb touches (not fixed position)
- Disappears when thumb lifts
- Same as many mobile games (Minecraft Pocket Edition uses this)
- Radius ~80px, 50% opacity

### Mobile-specific UI
- Sidebar becomes a bottom sheet (swipe up from bottom)
- All buttons are minimum 44x44px touch targets
- No hover states (touch has no hover)
- Landscape orientation preferred, portrait supported
- Status bar: minimal (FPS, chunk count)

### PWA considerations
- Full-screen mode (no browser chrome)
- Orientation lock to landscape when in 3D view
- Touch-action: none on canvas (prevent browser gestures)

---

## 5. VR Layout (WebXR)

### Controller mapping (Quest Touch controllers)
| Input | Left Hand | Right Hand |
|-------|-----------|------------|
| Thumbstick | Move (strafe/forward) | Snap turn (horizontal), look (vertical) |
| Trigger | Speed boost | Select / Place block |
| Grip | Grab world (scale/pan in god mode) | Remove block |
| A/X button | Toggle menu | Confirm / Interact |
| B/Y button | Back | Toggle edit mode |
| Menu button | System menu | - |

### VR HUD elements
```
     [Compass ring around head - always visible]

[Left wrist]                    [Right hand]
 - Block palette                 - Laser pointer
 - Quick menu                    - Block preview
 - Player list                   - Info tooltip

     [Floor level]
      - Claim boundaries (colored transparent volumes)
      - Player markers (floating name tags)
      - Coordinate grid (optional, togglable)
```

### Wrist menu (left hand)
- Appears when player looks at their left wrist (natural gesture)
- Rendered as a floating panel attached to hand position
- Contains:
  - Block palette (grid of block icons)
  - Camera mode selector
  - Player list (tap to teleport to player)
  - Settings (comfort, draw distance, etc.)
  - Exit VR button

### Floating panels
- Info panels (claim details, player stats) rendered as 3D quads
- Positioned at comfortable reading distance (~1.5m from eyes)
- Can be pinned in world space or follow the player
- Slightly transparent background with readable text
- SDF font rendering for crisp text at any angle/distance

### VR comfort settings
| Setting | Options | Default |
|---------|---------|---------|
| Turn mode | Snap (30/45/60/90) / Smooth | Snap 45 |
| Movement speed | 0.5x / 1x / 2x / 4x | 1x |
| Vignette on move | Off / Low / Medium / High | Medium |
| Height | Auto / Manual calibrate | Auto |
| Dominant hand | Left / Right | Right |
| Scale | Miniature / Normal / Giant | Normal |

### VR-specific rendering adjustments
- Reduce UI text size (VR resolution is lower per-pixel)
- Use world-space UI instead of screen-space
- No CSS/HTML overlays in VR (not visible in headset)
- All UI rendered as WebGPU geometry in the scene
- High contrast colors for readability

---

## 6. Adaptive UI System

### Input detection priority
1. Check for active WebXR session -> VR mode
2. Check for gamepad connected -> Gamepad mode
3. Check for touch events on canvas -> Mobile mode
4. Default -> PC keyboard+mouse mode

### Mode switching
- Automatic on input detection
- Manual override in settings
- Button prompts change with mode (show keyboard icons vs gamepad icons vs gesture hints)
- Layout adapts smoothly (no jarring transitions)

### Shared state
All control modes read/write the same camera and interaction state:
```csharp
public class InputState
{
    // Movement (normalized, -1 to 1)
    public float MoveX { get; set; }    // strafe
    public float MoveZ { get; set; }    // forward/back
    public float MoveY { get; set; }    // up/down
    public float SpeedMultiplier { get; set; } = 1f;

    // Look (delta per frame)
    public float LookDeltaX { get; set; }
    public float LookDeltaY { get; set; }

    // Actions (one-shot)
    public bool Select { get; set; }
    public bool Back { get; set; }
    public bool ToggleMenu { get; set; }
    public bool ToggleEdit { get; set; }
    public bool PlaceBlock { get; set; }
    public bool RemoveBlock { get; set; }
    public bool Undo { get; set; }
    public bool Screenshot { get; set; }
}
```

Each input mode writes to this shared state. The camera and game logic reads from it.
This decouples input handling from game logic - add new input modes without changing game code.

---

## 7. Fullscreen Mode

### Two levels of fullscreen

**Level 1: Full-page mode** (hide AubsCraft admin UI)
- Hides sidebar, header, nav, footer - canvas takes the full browser window
- Triggered by: button click, F11 key, or double-click canvas
- Shows minimal HUD overlay on the canvas (FPS, coords, compass)
- Escape exits back to normal layout
- This is the default for streaming/OBS since the browser chrome is still visible for the streamer

**Level 2: Browser fullscreen** (hide browser chrome too)
- Uses the Fullscreen API: `element.requestFullscreen()`
- Hides browser address bar, tabs, bookmarks, taskbar
- Canvas is the ONLY thing on screen
- Best for immersive viewing, VR-like experience on desktop
- Combined with pointer lock = full Minecraft-like experience
- Escape exits fullscreen (browser standard behavior)

### Implementation

```csharp
// Full-page mode: toggle CSS class
private bool _fullPageMode;

void ToggleFullPage()
{
    _fullPageMode = !_fullPageMode;
    StateHasChanged();
}

// Browser fullscreen: use Fullscreen API
async Task ToggleBrowserFullscreen()
{
    var doc = JS.Get<Document>("document");
    if (doc.FullscreenElement == null)
    {
        // Enter fullscreen on the canvas element
        await _canvasRef.RequestFullscreen();
    }
    else
    {
        await doc.ExitFullscreen();
    }
}
```

### Razor layout
```html
@if (!_fullPageMode)
{
    <!-- Normal layout: sidebar + header + canvas -->
    <NavMenu />
    <div class="main-content">
        <header>...</header>
        <canvas id="map-canvas" .../>
    </div>
}
else
{
    <!-- Full-page: canvas only + floating HUD -->
    <div class="fullpage-container">
        <canvas id="map-canvas" style="width:100vw; height:100vh; display:block;" .../>
        <div class="fullpage-hud">
            <span class="hud-fps">@Renderer.Fps FPS</span>
            <span class="hud-coords">@Camera.Position</span>
            <button class="hud-exit" @onclick="ToggleFullPage">Exit</button>
        </div>
    </div>
}
```

### HUD overlay in fullscreen
Minimal floating elements on top of the canvas:

```
+--------------------------------------------------+
|  [Exit]                              [Settings]   |
|                                                    |
|  [Compass: N]                                     |
|                                                    |
|              (3D World - full screen)              |
|                                                    |
|                                                    |
|  X: 228  Y: 64  Z: -243          60 FPS | 3101   |
|  [Players: 2]                     [Minimap]        |
+--------------------------------------------------+
```

- Semi-transparent dark background on HUD elements
- Auto-hide after 3 seconds of no input, show on mouse move
- Always visible in VR mode (rendered as world-space UI)

### Keyboard shortcuts
| Key | Action |
|-----|--------|
| F11 | Cycle: normal -> full-page -> browser fullscreen -> normal |
| Escape | Exit fullscreen / unlock pointer |
| H | Toggle HUD visibility |

Note: F key is reserved for frequently-used game actions (interact, fly toggle, etc.).

### Streaming/OBS mode
URL parameter: `?stream=true`
- Enters full-page mode automatically
- Hides ALL HUD elements (pure canvas)
- Optional: `?stream=true&overlay=fps,coords` to show specific elements
- `?stream=true&resolution=1920x1080` to lock canvas size
- OBS captures via "Browser Source" pointed at this URL

### Fullscreen API browser support
- Chrome: Full support
- Edge: Full support
- Firefox: Full support
- Safari: Full support (prefixed on older versions)
- Quest Browser: Full support
- The API is Baseline Widely Available

### SpawnDev.BlazorJS
Check if `Element.RequestFullscreen()` and `Document.ExitFullscreen()` are wrapped.
Likely yes - these are standard DOM methods. If not, trivial to add:
```csharp
// On any Element (HTMLCanvasElement inherits Element):
await canvasElement.JSRef!.CallVoidAsync("requestFullscreen");

// On Document:
await document.JSRef!.CallVoidAsync("exitFullscreen");
```

### Interaction with pointer lock
- Full-page mode + pointer lock = Minecraft-like experience
- Browser fullscreen + pointer lock = fully immersive
- When pointer lock is active in fullscreen, Escape first releases pointer lock, second press exits fullscreen
- This matches Minecraft's behavior exactly

### Performance impact
- Full-page mode: canvas takes full viewport, rendering area increases
- No sidebar DOM = slightly less layout work for the browser
- Canvas size auto-adjusts to viewport (already does this via CSS `width: 100%`)
- Depth buffer and render targets resize automatically via `ResizeObserver` or manual check

## 8. Performance Notes

### Gamepad polling
- `navigator.getGamepads()` is called each frame (16ms at 60fps)
- Very lightweight - no events, just reads cached state
- Zero allocation if we reuse the gamepad reference
- Dead zone filtering is a few float comparisons

### Touch events
- `touchstart`, `touchmove`, `touchend` on the canvas
- Use `passive: true` for scroll prevention
- Virtual joystick math: angle + magnitude from touch delta
- Minimal CPU cost

### VR input
- Controller state comes from `XRInputSource` in the `XRFrame`
- Already in the VR render loop - no extra polling
- Hand tracking (`XRHand`) is more expensive but optional

### UI rendering in VR
- Text rendering is the biggest cost (SDF fonts or pre-rendered textures)
- Limit floating panels to 3-4 visible at once
- Panel LOD: full detail when looked at, simplified when in peripheral
- Total UI cost target: <1ms per frame
