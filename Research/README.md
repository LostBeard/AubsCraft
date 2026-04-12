# AubsCraft Research

Reference data for building the AubsCraft platform. This folder is a permanent knowledge base - research here stays useful across sessions, agents, and future features.

---

## Documents

### Minecraft Data
- **[minecraft-block-textures.md](minecraft-block-textures.md)** - Atlas layout, per-face blocks, extraction methods, biome tinting
- **[server-plugin-data.md](server-plugin-data.md)** - All 22 server plugins, data formats, file locations, RCON capabilities

### Architecture + Implementation
- **[local-world-cache.md](local-world-cache.md)** - IndexedDB cache design, load sequence, offline mode, incremental updates
- **[water-transparency-implementation.md](water-transparency-implementation.md)** - Two-pass rendering spec, alpha blending, depth buffer handling
- **[paper-plugin-development.md](paper-plugin-development.md)** - Plugin structure, build, deploy, GriefPrevention API, communication patterns

### Feature Planning
- **[brainstorm-viewer-features.md](brainstorm-viewer-features.md)** - Drone cam, player tools, base viewer, claim visualization, streaming
- **[brainstorm-beyond-the-map.md](brainstorm-beyond-the-map.md)** - Full platform vision: creative mode, AI villagers, VR, chat, profiles
- **[player-wanted-features.md](player-wanted-features.md)** - Tier 1/2/3 demand ranking, competitive analysis, features by player type

### VR / WebXR
- **[webxr-vr-deep-dive.md](webxr-vr-deep-dive.md)** - Full VR technical plan, Quest performance budgets, interaction design, implementation phases
- **[webxr-webgpu-binding-status.md](webxr-webgpu-binding-status.md)** - XRGPUBinding spec status, browser support unknowns, fallback plan

### AI + Voice
- **[web-speech-api-reference.md](web-speech-api-reference.md)** - Speech recognition + synthesis for AI villager voice chat, VR integration

### Performance + Benchmarks
- **[cache-benchmark-results.md](cache-benchmark-results.md)** - IDB vs OPFS benchmark: OPFS region files win (118 MB/s write, 310 MB/s read)

### UI + Controls
- **[ui-layout-and-controls.md](ui-layout-and-controls.md)** - Full control schemes for PC, gamepad, mobile, VR + UI layouts + fullscreen modes + Gamepad API

### Admin + Player Systems
- **[admin-tools-plan.md](admin-tools-plan.md)** - 12-section admin tools: player management, grief detection, in-viewer tools, analytics
- **[account-linking-design.md](account-linking-design.md)** - MC character to web account linking flow, plugin, API, security, Bedrock/Geyser support
- **[spectator-cam-design.md](spectator-cam-design.md)** - Full spectator system: follow modes, tracker plugin, smooth interpolation, privacy, streaming
- **[chat-bridge-design.md](chat-bridge-design.md)** - Web-to-game chat, AubsCraftChat plugin, location sharing, screenshots, VR voice input
- **[claim-visualization-design.md](claim-visualization-design.md)** - GP API, ILGPU claim geometry, colors, UI, admin tools

---

## Asset Sources

**Minecraft client textures:**
`C:\Users\TJ\AppData\Roaming\.minecraft\versions\26.1.2\26.1.2.jar`
Path inside jar: `assets/minecraft/textures/block/`

**Minecraft server (Paper 1.21.5):**
`M:\opt\minecraft\server\`
Server jars contain NO textures - client-side only.

**Paper API jar (for plugin compilation):**
`M:\opt\minecraft\server\libraries\io\papermc\paper\paper-api\1.21.5-R0.1-SNAPSHOT\`

## SpawnDev Reference Code

**WebXR wrappers (65+ classes):** `D:\users\tj\Projects\SpawnDev.BlazorJS\...\JSObjects\WebXR\`
**WebXR demos:** `D:\users\tj\Projects\SpawnDev.BlazorJS.ThreeJS\...\Demo\Pages\VRDemo*.razor.cs`
**IndexedDB patterns:** `D:\users\tj\Projects\Redundo\...\Services\FileService.cs`
**ILGPU ShaderDebug IDB:** `D:\users\tj\Projects\SpawnDev.ILGPU\...\Services\ShaderDebugService.cs`

## What Goes Here

- Minecraft data formats (Anvil/MCA, NBT, region files, chunk structure)
- Block/texture specifications (atlas layouts, per-face mappings, biome tinting)
- WebGPU/WGSL reference notes (pipeline config, shader patterns, alpha blending)
- WebXR specs and browser compatibility notes
- Plugin development patterns and API references
- Performance findings (vertex budgets, draw call limits, GPU memory)
- Web API references (Speech, WebRTC, IndexedDB, WebSocket)
- Offline copies of key web resources
