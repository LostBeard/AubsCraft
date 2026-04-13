# AR Tabletop Editor - Captain's Vision

**Date:** 2026-04-13
**Priority:** This is the feature TJ is most excited about. Design everything to support it.

---

## The Vision

Two players, two modes, one world:

**TJ (AR mode - immersive-ar):** Sees the Minecraft world as a miniature diorama sitting on his real table via Quest 3S passthrough. The entire world is a tabletop model he can rotate, zoom, and interact with. Editor tools let him place blocks, set markers, modify terrain - god-mode from above. He can see Aubs' player avatar moving through the diorama in real-time as she builds.

**Aubs (VR mode - immersive-vr):** Fully immersed inside the world at 1:1 scale. Building, exploring, riding her roller coaster. She sees TJ's edits appear in real-time - blocks he places from the tabletop view materialize in her world.

**Both connected via the server.** Real-time sync. Same world, different perspectives.

---

## Technical Requirements

### AR Session (Quest 3S Passthrough)
- WebXR `immersive-ar` mode with passthrough compositing
- Quest 3S shows the real room with the virtual world composited on top
- `XRHitTest` to detect the real table surface and place the world on it
- The world renders with transparency/alpha around edges so the table is visible

### World Placement and Scale
- **Initial placement:** Tap the table to place the diorama
- **Scale controls:** Pinch gesture to zoom (make world bigger/smaller on table)
- **Rotation:** Grab and twist to rotate the world
- **Default scale:** Entire render distance fits on a ~60cm table area
- **Scale range:** From "city block" (see individual blocks) to "whole world" (terrain overview)

### Rendering the Diorama
- Same voxel mesh data, just rendered at miniature scale
- Camera is the AR headset position, looking down at the table
- Orthographic or very wide FOV perspective for tabletop overview
- LOD is natural here - at tabletop scale, LOD 4 looks perfect (individual blocks are tiny)
- Much less vertex pressure than VR mode (world is far away = heavy LOD)

### Player Markers in Diorama
- Query player positions via RCON/SignalR (already works)
- Render as colored cubes or name labels at world positions
- At diorama scale, players are tiny dots with name tags
- **Aubs' avatar is visible** moving through the miniature world in real-time
- Optional: render player skins as tiny voxel models (future)

### Editor Tools (AR Mode)
- **Block placement:** Point at diorama surface, select block type from palette, tap to place
- **Selection:** Draw box selection on the diorama to select regions
- **Copy/paste:** Select region, lift it up, place it elsewhere
- **Markers:** Place waypoints, labels, area boundaries visible in both AR and VR
- **Claim visualization:** GriefPrevention claims rendered as colored volumes in the diorama
- **Time control:** Slider to change time of day (instant visual feedback on the diorama)
- **Weather toggle:** See rain/snow effects on the miniature world

### Real-Time Sync
- Server pushes block changes via SignalR
- When Aubs places a block in VR, it appears in TJ's diorama within 3 seconds
- When TJ places a block in AR editor, it appears in Aubs' VR world
- Player position updates via existing RCON polling

### UI in AR Mode
- Floating tool palette near the diorama (world-locked)
- Block type selector (radial menu or palette strip)
- Settings panel (same as VR, world-locked)
- Stats overlay (optional, less important in AR editor)

---

## Why This Is Special

No Minecraft tool does this. Not QuestCraft, not Vivecraft, not Bedrock VR, not any third-party viewer. The closest thing is tabletop games like Demeo or Moss, but those are pre-built levels - not live multiplayer Minecraft worlds.

AubsCraft would be the FIRST tool that lets a parent oversee and edit their child's Minecraft world from an AR tabletop view while the child plays inside it in VR. That's not just a technical achievement - it's a new way for families to play together.

---

## Implementation Notes

- WebXR `immersive-ar` is supported on Quest 3S via Quest Browser
- `XRHitTest` API detects horizontal surfaces (tables)
- SpawnDev.BlazorJS has XR wrappers including `XRHitTestSource`, `XRHitTestResult`
- The diorama is just the same mesh rendered with a different model matrix (scale + position on table)
- Editor commands go through the existing RCON or a new server API
- Player positions already available via `GetPlayerPositions` hub method

---

## Phasing

1. **First:** Get basic VR working (immersive-vr, fly around)
2. **Second:** Add AR mode (immersive-ar, table placement, diorama view)
3. **Third:** Editor tools (block placement from AR)
4. **Fourth:** Player markers (see Aubs in the diorama)
5. **Fifth:** Two-way editing (AR edits appear in VR)

The diorama VIEW is relatively simple (just scale + position the world on a table). The EDITOR is the complex part (block modification API, undo/redo, permission checks).
