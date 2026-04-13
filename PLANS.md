# AubsCraft Development Plans

Master checklist for the AubsCraft platform - admin panel, 3D world viewer, player tools, and VR.
All features listed are hard-set unless explicitly removed by Captain.
Updated: 2026-04-12

---

## Phase A: Admin Panel (COMPLETE)

- [x] A1. SignalR Hub (real-time server communication)
- [x] A2. Authentication (cookie auth, admin.json, PBKDF2-SHA256)
- [x] A3. Dark Theme (Bootstrap 5.3.3, no flash)
- [x] A4. Log Tailing (FileSystemWatcher + timer, Paper 1.21.5 format)
- [x] A5. Activity Log + Notifications (filterable timeline, toast alerts)
- [x] A6. Chat Panel + Extra Controls (gamemode, teleport, TPS graph)
- [x] A7. BlueMap Embed (iframe)
- [x] A8. GitHub Release Prep
- [x] A9. Plugin Management (read plugin.yml, enable/disable)
- [x] A10. Player Stats + World Stats + Client Detection

## Phase B: 3D World Viewer - Core (COMPLETE)

- [x] B1. WebGPU pipeline + WGSL shader (dual-light, fog, alpha-discard)
- [x] B2. HeightmapMesher (top faces + cliff sides, atlas UVs)
- [x] B3. MinecraftMeshKernel (ILGPU full 3D voxel meshing)
- [x] B4. Texture atlas (80+ textures, 16x16 grid, 256x256)
- [x] B5. FPS camera (WASD + mouse look, pointer lock)
- [x] B6. Frustum culling + adaptive draw distance
- [x] B7. SignalR heightmap streaming + full chunk loading

## Phase C: Viewer Polish - Session 2026-04-12 (COMPLETE)

- [x] C1. Fix +X face position bug (invisible block faces)
- [x] C2. Per-face textures for logs (all 8 types - bark on sides, rings on top/bottom)
- [x] C3. Per-face textures for grass_block (grass_top, grass_side, dirt)
- [x] C4. Per-face textures for podzol + mycelium
- [x] C5. Cross-shaped plant quads in ILGPU kernel (flowers, grass, fern)
- [x] C6. Skip plants in heightmap (clean loading view)
- [x] C7. Pointer lock fix (detect external focus loss, single-click recovery)
- [x] C8. SafeInvokeAsync fix (Dashboard + Activity Log regressions)
- [x] C9. RCON password restore (World Controls working)
- [x] C10. Transparent water - two-pass rendering (opaque + alpha blend)
- [x] C11. Seabed rendering (heightmap includes underwater terrain)
- [x] C12. IndexedDB WorldCacheService (schema, put/get, cache on stream)

## Phase D: Performance + Data Pipeline (IN PROGRESS)

- [x] D1. **JS-side binary WebSocket** - Replace SignalR with JS WebSocket via BlazorJS
  - Binary frames, data stays in JS, straight to IndexedDB
  - .NET only touches data when ILGPU kernel needs it
- [x] D2. **Radial loading from camera** - Chunks render outward from camera position
- [ ] D3. **Incremental updates** - Server sends only changed chunks (timestamp comparison)
- [x] D4. **OPFS region-file cache** - Replaced IndexedDB with OPFS (118 MB/s write, 310 MB/s read)
  - Region-batched files matching Minecraft's 32x32 chunk pattern
  - Benchmark proved: OPFS region is 107x faster writes than IndexedDB
- [ ] D5. **Offline mode** - Viewer works entirely from OPFS cache when server is down
- [ ] D6. **Fix vanishing chunks** - Never clear rendered data on server error
- [ ] D7. **Water grid alignment** - Heightmap water Y matches kernel water Y everywhere
- [ ] D8. **Vertex buffer management** - Dynamic sizing, logging when limits hit
- [x] D9. **Heightmap GPU kernel** - Replace CPU HeightmapMesher with ILGPU kernel

## Phase D2: Input System + UI Layout

- [ ] D2.1. **Unified InputState** - Shared state that all input modes write to, camera/game reads from
- [ ] D2.2. **Gamepad support** - Gamepad API polling, standard mapping, dead zones
  - Left stick: move, Right stick: look, Triggers: speed/interact
  - D-pad: camera mode switching, Face buttons: interact/menu/edit
- [ ] D2.3. **Auto input detection** - WebXR -> VR, Gamepad -> controller, Touch -> mobile, default -> PC
- [ ] D2.4. **Mobile touch controls** - Virtual joystick (left thumb), gestures (pinch/drag/tap)
- [ ] D2.5. **Collapsible sidebar** - Tab key or hamburger, fullscreen mode hides entirely
- [ ] D2.6. **Keyboard shortcuts** - Tab (players), M (minimap), C (coords), T (chat), F2 (screenshot), F5 (camera mode)
- [ ] D2.7. **Gamepad UI cursor** - Virtual cursor via right stick when not in pointer lock
- [ ] D2.8. **Adaptive button prompts** - Show keyboard/gamepad/gesture icons based on active input
- [ ] D2.9. **HUD elements** - Compass, coordinates, minimap corner, FPS counter
- [ ] D2.10. **Fullscreen mode** - Two levels: full-page (hide admin UI) + browser fullscreen (hide browser chrome)
  - Minimal floating HUD overlay (FPS, coords, compass, exit button)
  - Auto-hide HUD after 3s inactivity, show on mouse move
  - F11 cycles: normal -> full-page -> browser fullscreen -> normal
  - F key reserved for game actions (interact/fly toggle/etc.)
  - Escape: releases pointer lock first, then exits fullscreen (matches Minecraft)
- [ ] D2.11. **Streaming/OBS mode** - URL param `?stream=true` for clean capture
  - No HUD, configurable resolution, optional overlay elements
  - OBS Browser Source compatible
- [ ] D2.12. **PWA mobile** - Full-screen, orientation lock, touch-action:none on canvas

## Phase E: Viewer Visual Features

- [ ] E1. **Time-of-day lighting** - Sun direction + ambient from RCON time query, animated cycle
- [ ] E2. **Day/night skybox** - Sky color changes with time, stars at night
- [ ] E3. **Weather rendering** - Rain/snow particles, sky color changes
- [ ] E4. **Entity rendering** - Animals, villagers from server entity data
- [ ] E5. **Per-face textures: remaining blocks** - Pumpkin, melon, TNT, furnace, crafting table, hay, bone
- [ ] E6. **Biome-accurate tinting** - Read biome data, apply correct grass/leaf/water colors per biome
- [ ] E7. **Block lighting/ambient occlusion** - Darken corners and crevices for depth

## Phase F: Map Navigation + HUD

- [ ] F1. **Compass HUD** - N/S/E/W directions on screen
- [ ] F2. **Coordinates display** - Camera X/Y/Z like the game's F3 screen
- [ ] F3. **Minimap** - Small top-down view in corner showing position on full map
- [ ] F4. **Biome labels** - Hover over terrain to see biome name
- [ ] F5. **URL deep-linking** - `?x=228&z=-243&zoom=5` opens directly to a location
- [ ] F6. **Top-down screenshot mode** - Orthographic camera, export shareable PNG
- [ ] F7. **Spawn marker** - Always-visible marker at world spawn
- [ ] F8. **Death markers** - Skull icons where players died (from log parsing)
- [ ] F9. **Bed markers** - Player respawn points (from Essentials data)
- [ ] F10. **Nether portal markers** - Highlight active portals linking dimensions

## Phase G: Multi-User Auth + Admin Levels

- [ ] G1. Multi-user accounts (replace single admin.json with user list)
- [ ] G2. Admin levels/roles (owner, admin, moderator, viewer)
- [ ] G3. Admin management page (create users, set levels from website)
- [ ] G4. Role-based endpoint authorization (dangerous ops require higher level)
- [ ] G5. SignalR hub method authorization by role

## Phase H: Account Linking (MC Character + Web Account)

- [ ] H1. **AubsCraftLink Paper plugin** - `/link <code>` command, validates via server API
- [ ] H2. **Web verification flow** - Create account, get code, link in-game
- [ ] H3. **Bedrock/Geyser support** - Handle Floodgate UUIDs for Bedrock players
- [ ] H4. **Microsoft OAuth option** - Xbox Gamertag to MC UUID mapping
- [ ] H5. **Profile commands** - `/profile`, `/profile public`, `/profile private`, `/unlink`

## Phase I: Golden Shovel Claim Viewer

- [ ] I1. **AubsCraftClaims Paper plugin** - Wraps GriefPrevention API, exposes claims as JSON
- [ ] I2. **Real-time claim events** - Plugin pushes create/resize/delete via WebSocket
- [ ] I3. **3D claim rendering** - Semi-transparent colored volumes for claim boundaries
- [ ] I4. **Click-to-fly** - Click any claim to fly the camera there instantly
- [ ] I5. **Claim info panel** - Owner, size, trust list, creation date, last activity
- [ ] I6. **Neighborhood view** - See who's near your base
- [ ] I7. **Admin claim tools** - Expired/abandoned highlights, one-click removal, overlap detection
- [ ] I8. **Claim analytics** - Size rankings, activity heatmap

## Phase J: Player System on the Map

- [ ] J1. **AubsCraftTracker Paper plugin** - Real-time player position broadcasting
- [ ] J2. **Player list overlay** - Sidebar with all online players, click to fly to them
- [ ] J3. **Player skin rendering** - Skin head icons or simple avatars at positions
- [ ] J4. **Position interpolation** - Smooth movement between RCON position updates
- [ ] J5. **Direction indicator** - Show which way each player is facing
- [ ] J6. **Activity indicator** - Idle, mining, building, fighting, AFK status
- [ ] J7. **Essentials warp markers** - Read warp YAML, show labeled markers on map
- [ ] J8. **Player last-logout markers** - Last position from Essentials userdata

## Phase K: Web-to-Game Chat

- [ ] K1. **AubsCraftChat Paper plugin** - Web-to-game chat bridge via WebSocket
- [ ] K2. **[Web] prefix** - Messages from website show with [Web] tag in-game
- [ ] K3. **Private messages** - `/msg <player>` from web
- [ ] K4. **Location sharing** - "I'm at x=228, z=-243" with clickable camera-fly link
- [ ] K5. **Screenshot sharing** - Capture viewer screenshot, share as clickable thumbnail in chat

## Phase L: Browser Creative Mode (Base Editing)

- [ ] L1. **AubsCraftBuild Paper plugin** - Receives block change commands, validates permissions, applies
- [ ] L2. **Edit Mode UI** - Block palette, place/remove tools, undo/redo
- [ ] L3. **Raycast block targeting** - Click to place on target face, right-click to remove
- [ ] L4. **Ghost block preview** - Transparent/outlined blocks before committing
- [ ] L5. **Claim boundary enforcement** - Server-side validation, players can only edit own claims
- [ ] L6. **Admin override** - Admins can edit anywhere, changes apply immediately
- [ ] L7. **Material cost tracking** - Deduct from player inventory in survival mode
- [ ] L8. **CoreProtect logging** - All web-placed blocks logged for rollback
- [ ] L9. **Submission queue** - Changes queue and apply when player is online (or immediately for admin)
- [ ] L10. **Rate limiting** - Max N blocks per submission for safety

## Phase M: Public Player Profiles

- [ ] M1. **Profile pages** - `map.spawndev.com:44365/player/<name>`
- [ ] M2. **Stats display** - Play time, blocks placed/broken, deaths, achievements
- [ ] M3. **Base gallery** - Player's builds with screenshots
- [ ] M4. **Build showcase** - Featured builds with flyover camera paths
- [ ] M5. **Privacy controls** - Public/private toggle, opt-in only
- [ ] M6. **Achievement tracking** - Read advancement data, display progress

## Phase N: AI Villagers

- [ ] N1. **AubsCraftAI Paper plugin** - Spawns intelligent NPC villagers at designated locations
- [ ] N2. **Text chat interface** - Click villager in viewer, type messages, get AI responses
- [ ] N3. **Voice chat** - Browser microphone, speech-to-text, AI responds, text-to-speech
- [ ] N4. **Villager personalities** - Town Crier, Cartographer, Historian, Quest Giver, Lore Master
- [ ] N5. **World knowledge** - Villagers know server state (who's online, recent events, builds)
- [ ] N6. **CoreProtect integration** - Historian villager can tell you who built what and when
- [ ] N7. **NotQuests integration** - Quest Giver villager ties into quest plugin
- [ ] N8. **Persistent memory** - Per-villager conversation history across sessions
- [ ] N9. **Claude API backend** - AI responses powered by Claude via SpawnDev claude-api

## Phase O: Multi-Dimension Maps

- [ ] O1. **Dimension switcher UI** - Tabs for Overworld, Nether, End
- [ ] O2. **Nether rendering** - Red/orange fog, lava rivers, ceiling, nether block palette
- [ ] O3. **End rendering** - Black/purple void, floating islands, end stone, chorus plants
- [ ] O4. **Portal markers** - Nether portal locations linking dimensions
- [ ] O5. **Same RegionReader** - Nether at `world/DIM-1/region/`, End at `world/DIM1/region/`

## Phase P: Spectator Cam + Drone Cam + Streaming

- [ ] P1. **Spectator mode** - Click a player in the list, click "Spectate", camera follows them in 3rd person
  - Player position/direction streamed in real-time from AubsCraftTracker plugin
  - Configurable camera offset (distance, height, angle behind player)
  - Smooth interpolation between position updates
  - The viewer renders independently - zero performance impact on the player's game
  - Perfect for streaming: Aubs plays in VR, spectator cam streams to Twitch/YouTube
  - OR: guests visit the site directly and spectate live, no stream needed
  - Each viewer picks their own camera angle, renders locally from position data
  - Only a few bytes/sec of position data transmitted - scales to unlimited viewers
  - Player privacy: spectate requires player opt-in (toggle in profile settings)
  - Camera auto-loads chunks around the followed player as they move
- [ ] P2. **Camera modes** - Free fly, orbit, follow/spectate, cinematic waypoint, top-down
- [ ] P3. **Waypoint recording** - Record camera path, play back in loop (cinematic mode)
- [ ] P4. **OBS Browser Source mode** - Configurable resolution, no UI chrome, overlay options
- [ ] P5. **URL parameters** - `?spectate=HereticSpawn&hideUI=true&resolution=1080p`
- [ ] P6. **WebRTC live streaming** - captureStream() to peer connection via SpawnDev.RTLink
  - One viewer runs spectator cam, others watch as a live video feed without rendering
- [ ] P7. **Stream overlay** - Optional on-screen info: player name, coordinates, server name, player count

## Phase Q: WebXR VR Mode

- [ ] Q1. **SpawnDev.BlazorJS WebXR wrappers** - New addition to the SpawnDev ecosystem
- [ ] Q2. **VR rendering** - WebGPU output to VR display instead of flat canvas
- [ ] Q3. **Hand controller navigation** - Fly mode with hand controllers
- [ ] Q4. **VR interaction** - Point at claims, players, blocks to interact
- [ ] Q5. **God mode admin** - Float above world, see everything, manage by pointing
- [ ] Q6. **Scale model** - Shrink world to tabletop size for close examination
- [ ] Q7. **VR base building** - Place/remove blocks with hand controllers in your claim
- [ ] Q8. **Voice commands in VR** - "Show claims", "fly to spawn", "kick player X"
- [ ] Q9. **Mixed reality (AR)** - Overlay Minecraft map on real desk (Quest 3 passthrough)

## Phase R: Inventory + Advanced Player Tools

- [ ] R1. **AubsCraftInventory Paper plugin** - Web inventory viewing/management API
- [ ] R2. **Inventory viewer** - See your items from the web
- [ ] R3. **Inventory management** - Move items, organize chests (within claim)
- [ ] R4. **Crafting planner** - Plan crafting recipes, see material requirements
- [ ] R5. **Enchantment viewer** - See enchantments on items
- [ ] R6. **Block counter** - Click a block type, see count in current view

## Phase S: Data Analytics + Admin Intelligence

- [ ] S1. **CoreProtect history viewer** - Click any block to see change history, who/when/what
- [ ] S2. **Player activity heatmap** - Where do players spend time? (CoreProtect data)
- [ ] S3. **Claim activity analytics** - Active vs abandoned claims
- [ ] S4. **Block placement statistics** - Most used blocks, building patterns
- [ ] S5. **TPS correlation** - What activities cause TPS drops?
- [ ] S6. **Player retention metrics** - Who's active, who stopped playing, when

---

## Paper Plugins Required

| Plugin | Phase | Purpose | Status |
|--------|-------|---------|--------|
| VRDetect | A | VR player detection | DONE |
| AubsCraftLink | H | Account linking (verification codes) | Planned |
| AubsCraftClaims | I | GriefPrevention claim data API | Planned |
| AubsCraftTracker | J | Real-time player position broadcasting | Planned |
| AubsCraftChat | K | Web-to-game chat bridge | Planned |
| AubsCraftBuild | L | Browser creative mode block placement | Planned |
| AubsCraftAI | N | AI villager NPCs | Planned |
| AubsCraftInventory | R | Web inventory viewing/management | Planned |

All plugins: Java 21, Paper API 1.21.5, communicate with AubsCraft server via HTTP/WebSocket.

---

## Architecture

### Data Flow (target)
```
Server --[binary WS]--> JS WebSocket --[stays in JS]--> IndexedDB cache
                                                              |
                                         .NET <--[on demand]--+
                                           |
                                     ILGPU kernel --> WebGPU --> render
```

### Key Rules
- **Performance is THE feature** - every decision through the lens of "does this make it faster?"
- **ILGPU kernel for ALL mesh generation** - no CPU fallbacks
- **Data stays where it belongs** - no unnecessary .NET/JS round-trips
- **Deploy after EVERY change** - edit, deploy, wait for feedback
- **Client-side processing** - server is a data source, client GPU is the compute engine
- **JS for I/O, .NET for logic, GPU for compute** - each sandbox for its strengths

### SpawnDev Ecosystem Showcase

| Feature | SpawnDev Tech |
|---------|--------------|
| 3D rendering | SpawnDev.ILGPU (WebGPU kernel) |
| JS interop | SpawnDev.BlazorJS (IndexedDB, WebSocket, WebXR, fetch) |
| WebRTC streaming | SpawnDev.RTLink |
| AI integration | Claude API via BlazorJS |
| Offline caching | SpawnDev.BlazorJS IndexedDB wrappers |
| VR support | SpawnDev.BlazorJS WebXR wrappers (new) |
| Crypto/auth | SpawnDev.BlazorJS.Cryptography |

## Phase Z: The North Star - Browser-Based Minecraft Client

Every feature we build moves us closer. This is the horizon we aim for.

- [ ] Z1. **Entity rendering** - Mobs, animals, item entities with models and animations
- [ ] Z2. **Player skin rendering** - Full player models with downloaded skins, animation
- [ ] Z3. **Collision/physics** - Walk ON blocks, gravity, jumping (not just flying)
- [ ] Z4. **Real-time block updates** - Server pushes block changes as they happen
- [ ] Z5. **Block interaction** - Break and place blocks in real-time (not queued)
- [ ] Z6. **Inventory system** - Full hotbar, crafting, chest interaction
- [ ] Z7. **Combat** - Hit detection, damage, health, food
- [ ] Z8. **Redstone visualization** - Powered state rendering, signal propagation
- [ ] Z9. **Particle system** - All MC particles (torch flame, campfire smoke, enchant glitter)
- [ ] Z10. **Biome rendering** - Per-biome grass/leaf/water colors, fog colors
- [ ] Z11. **Chunk-level LOD** - Near: full 3D, mid: simplified, far: heightmap (seamless transitions)
- [ ] Z12. **MC protocol bridge** - Direct MC server protocol instead of file-based world reading
- [ ] Z13. **Mobile touch gameplay** - Play Minecraft from a phone browser, no app needed
- [ ] Z14. **VR gameplay** - Play Minecraft in VR from a browser, no Vivecraft needed
- [ ] Z15. **Cross-play** - Java, Bedrock, Web, and VR players all on the same server

*"Aim as far as possible and you will never be disappointed how far you make it." - Captain TJ*

---

### Key References (local)
- `Research/local-world-cache.md` - IndexedDB cache design, load sequence, offline mode
- `Research/minecraft-block-textures.md` - Atlas layout, per-face blocks, extraction
- `Research/brainstorm-viewer-features.md` - Feature vision, plugin data, quick wins
- `Research/brainstorm-beyond-the-map.md` - Full platform brainstorm (creative mode, AI, VR, chat)
- `Research/server-plugin-data.md` - All 22 plugins, data formats, RCON capabilities
- `Research/water-transparency-implementation.md` - Two-pass rendering spec
