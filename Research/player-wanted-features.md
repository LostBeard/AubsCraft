# Player-Wanted Features Research

**Date:** 2026-04-12
**Source:** Analysis of popular Minecraft companion tools, community requests, and competitive advantages

---

## What players actually use companion apps for

### Tier 1: "I'd use this every session" (highest demand)
1. **Live map with player positions** - #1 most-used feature in Dynmap/BlueMap
2. **Claim/base location finder** - "Where is everyone?" is the top social question
3. **Coordinate bookmarks** - "Remember this spot" without writing coords on paper
4. **Chat from phone/tablet** - Talk to friends while AFK or at work/school
5. **Mobile-friendly map** - Check on the server from anywhere

### Tier 2: "I'd use this weekly" (high demand)
6. **Build showcase/sharing** - Show off builds to non-players (parents, friends)
7. **Warp/home list** - Quick navigation without `/home` commands
8. **Death location** - "Where did I die?!" with exact coordinates
9. **Player stats** - Compare play time, achievements, builds
10. **Server status** - Is the server up? How many players? TPS?

### Tier 3: "This would be amazing" (differentiator)
11. **3D base viewer** - See your base from any angle without being in-game
12. **VR exploration** - Walk through the server world in VR
13. **Build planner** - Plan builds before placing blocks
14. **Time-lapse** - Watch the server evolve over days/weeks
15. **AI guide/assistant** - Ask an NPC for help, directions, lore

---

## Features from popular Minecraft tools we should match or beat

### From Dynmap/BlueMap (map viewers)
- [x] 3D world rendering (we have this, better than both)
- [x] Real-time player markers
- [ ] Flat/isometric/3D view toggle
- [ ] Layer system (surface, caves, nether)
- [ ] Marker sets (custom markers by admins)
- [ ] Area markers (region highlighting)
- [ ] Chat integration
- [ ] URL sharing with coordinates

### From Amidst/MineAtlas (seed tools)
- [ ] Structure prediction from world seed
- [ ] Biome mapping from seed
- [ ] Slime chunk calculator
- [ ] Stronghold triangulation

### From Chunk Base (utility)
- [ ] Coordinate converter (Overworld/Nether)
- [ ] Spawn chunk display
- [ ] Light level overlay
- [ ] Biome finder with filters

### From Lunar Client/Badlion (player tools)
- [ ] Waypoint system with sharing
- [ ] Minimap
- [ ] Coordinates HUD
- [ ] FPS display
- [ ] Direction/compass

---

## Unique features NOBODY else has (our competitive edge)

These are features no existing Minecraft companion tool offers:

### 1. GPU-accelerated browser rendering
- Our WebGPU renderer runs at 60 FPS in a browser tab
- BlueMap/Dynmap use static pre-rendered tiles - we render LIVE
- This means: smooth camera flight, real-time updates, VR support

### 2. VR world exploration
- No existing Minecraft web tool supports WebXR
- We could be the FIRST browser-based Minecraft VR viewer
- Works on Quest without any app installation

### 3. Browser creative mode
- No web tool lets you place blocks from the browser
- Combined with VR, this is "Minecraft creative mode in your browser"
- Claim-boundary enforcement makes it safe for survival servers

### 4. AI villager NPCs
- No Minecraft server has voice-interactive AI villagers accessible from a browser
- Claude-powered NPCs with server knowledge and persistent memory
- Voice chat via Web Speech API - zero cost, runs locally

### 5. WebRTC spectator streaming
- No existing tool can stream the 3D view to other browsers via WebRTC
- One player flies the drone cam, everyone watches in real-time
- Uses SpawnDev.RTLink for signaling

### 6. Cross-platform with zero install
- Works on phone, tablet, desktop, VR headset - same URL
- No Minecraft client needed
- No Java needed
- PWA installable for app-like experience

---

## Mobile-Specific Considerations

### Touch controls for the 3D viewer
- **One finger drag**: pan/rotate camera
- **Two finger pinch**: zoom in/out
- **Two finger drag**: tilt camera angle
- **Tap**: select block/claim/player
- **Long press**: context menu (info, teleport, bookmark)
- **Swipe from edge**: open sidebar (player list, chat, etc.)

### Mobile performance
- Reduce draw distance automatically on mobile (detect via `navigator.userAgentData` or screen size)
- Use heightmap-only rendering (no full 3D chunks) on low-end devices
- Texture atlas already small (256x256) - no issue for mobile
- Target 30 FPS on mobile (vs 60 on desktop, 90 on VR)

### PWA features
- Add to home screen prompt
- Offline support (cached map data)
- Push notifications (player joins, chat messages, server events)
- Background sync (update cache while app is backgrounded)

---

## Feature Ideas by Player Type

### For Aubs (10yo, building/exploring)
- **Build showcase with sharing URL** - Show friends "look what I built!"
- **Photo mode with filters** - Take pretty screenshots of builds
- **Achievement tracker** - See progress toward all advancements
- **Pet finder** - Where are my animals? (MyPet plugin data)
- **Treasure map** - Admin-placed hidden items, visible only when you're close on the map

### For new players
- **Server tour** - Pre-recorded flyover of the best builds and locations
- **"Getting started" markers** - Show nearby resources, safe areas, community farms
- **Guest view** - Browse the map without an account (read-only)
- **Join instructions** - How to connect to mc.spawndev.com from Java/Bedrock/VR

### For builders
- **Material calculator** - Select an area, count blocks needed per type
- **Symmetry checker** - Overlay a mirror to check build symmetry
- **Height reference** - Show Y-level markers (useful for builds at specific heights)
- **World download (own claim only)** - Export claim as schematic for backup

### For admins
- **Grief detection** - Highlight blocks placed/removed in unusual patterns (CoreProtect)
- **X-ray view** - See underground structures, caves, spawners (admin only)
- **Player path replay** - CoreProtect movement data visualized as trails on the map
- **Ban/kick from map** - Right-click player marker, select action
- **Server resource overlay** - Loaded chunks, entity counts, TPS by region

### For content creators/streamers
- **Cinematic camera paths** - Record and replay smooth camera movements
- **Clean capture mode** - No UI, configurable resolution, perfect for OBS
- **Overlay system** - Show server info, player count, event countdown on stream
- **Reaction system** - Viewers can place temporary emojis on the map (like Twitch reactions)

---

## Revenue-Adjacent Ideas (if the platform grows)

Not monetization per se, but ways the platform could sustain itself:

1. **Open source the viewer** - Build community, get contributions, build reputation
2. **SaaS for other servers** - "AubsCraft Viewer for YOUR server" - hosted solution
3. **Plugin marketplace** - Community-built Paper plugins that integrate with the viewer
4. **Template worlds** - Pre-built world themes (medieval, modern, fantasy) with viewer integration
5. **Educational license** - Schools use Minecraft for education; a web viewer for class projects
6. **3D print export** - Export any area as an STL for 3D printing (paid service)

---

## Priority Matrix

| Feature | Player Impact | Development Cost | SpawnDev Showcase Value | Priority |
|---------|:---:|:---:|:---:|:---:|
| Live player markers | High | Low | Low | DO FIRST |
| Claim visualization | High | Medium | Medium | DO FIRST |
| Mobile touch controls | High | Medium | Medium | DO SOON |
| URL deep-linking | Medium | Low | Low | DO SOON |
| Chat from web | High | Medium | Medium | DO SOON |
| Build showcase/sharing | High | Medium | High | PLAN |
| VR exploration | Medium | High | VERY HIGH | PLAN |
| AI villagers | Medium | High | VERY HIGH | PLAN |
| Browser creative mode | High | Very High | VERY HIGH | PLAN |
| Time-lapse | Medium | Medium | High | LATER |
| WebRTC streaming | Low | Medium | VERY HIGH | LATER |
