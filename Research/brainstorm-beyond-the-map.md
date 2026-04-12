# AubsCraft: Beyond the Map - Full Platform Brainstorm

**Date:** 2026-04-12
**Contributors:** TJ (Captain), Tuvok
**Status:** Active brainstorm - everything here is a possibility, not a commitment

---

## The Vision

AubsCraft isn't a map viewer. It's a **browser-based Minecraft companion platform** that gives players capabilities they can't get in-game, admins tools they've never had, and spectators a window into a living world. All powered by SpawnDev.BlazorJS + SpawnDev.ILGPU running on WebGPU.

The Minecraft client shows you the game. AubsCraft shows you the *world*.

---

## 1. Golden Shovel Claim Viewer

### What players get
- 3D visualization of ALL claimed zones across the server
- Color-coded by owner (each player gets a unique color)
- Click any claim to fly the camera there instantly
- See claim boundaries as semi-transparent colored volumes (not just outlines)
- Claim info panel: owner, size, trust list, creation date, last activity
- "Neighborhood view" - see who's near your base

### What admins get
- Expired/abandoned claims highlighted in red
- Claim overlap detection
- One-click claim removal from the web UI
- Claim size analytics (who has the most land?)
- Claim activity heatmap (which claims are active vs abandoned?)

### Implementation
- **Paper plugin: AubsCraftClaims** - wraps GriefPrevention API, exposes claims as JSON via HTTP endpoint or WebSocket
- Plugin listens for claim create/resize/delete events and pushes updates in real-time
- Client renders claim boundaries as semi-transparent colored boxes in the 3D viewer
- GriefPrevention stores claims with corner coordinates - plugin reads these directly

### Data available
- GriefPrevention API: `getClaimsInChunk()`, `getClaim(id)`, claim corners, owner UUID, trust lists
- Player data at `plugins/GriefPreventionData/PlayerData/` - accrued + bonus claim blocks per player
- Can query claim info via RCON: `claimslist <player>`

---

## 2. Account Linking (MC Character + Web Account)

### The flow
1. Player creates a web account (email/password or OAuth)
2. They receive a 6-character verification code on the website
3. In Minecraft, they type `/link <code>`
4. **AubsCraftLink** Paper plugin validates the code via our server API
5. MC UUID is now linked to the web account
6. All player-specific features unlock

### What linking enables
- Inventory management from browser
- Personal claim visualization
- Base editing (creative mode for web)
- Public profile page
- Achievement display
- Chat from web to in-game
- Personal bookmarks and waypoints on the map

### Bedrock/Geyser support
- Bedrock players through Geyser get fake UUIDs (prefix `00000000-0000-0000-0009-`)
- Floodgate maps Xbox IDs to these UUIDs
- The linking plugin needs to handle both Java UUIDs and Geyser-prefixed UUIDs
- Could also support Xbox OAuth for Bedrock players (Microsoft account -> Xbox Gamertag -> Floodgate UUID)

### Paper plugin: AubsCraftLink
```
/link <code>     - Link your MC account to the website
/unlink          - Remove the link
/profile         - View your public profile URL
/profile public  - Make your profile public
/profile private - Make your profile private
```

---

## 3. Browser Creative Mode (Base Editing)

### The concept
Players can place and remove blocks in the 3D viewer, within their own claim boundaries. Changes queue up and apply to the actual server world.

### How it works
- Player is authenticated and linked (see #2)
- They enter "Edit Mode" in the viewer
- Their Golden Shovel claim boundaries are highlighted
- Block palette appears (same blocks available in their inventory or in creative)
- Click to place, right-click to remove
- Changes are shown immediately in the viewer as "ghost blocks" (transparent/outlined)
- A "Submit Build" button queues the changes
- **AubsCraftBuild** Paper plugin applies the queued changes when the player is online (or immediately for admins)
- Admin override: admins can edit anywhere, changes apply immediately

### Safety
- Changes only within own claim (server-side validation in the plugin)
- Rate limiting (max N blocks per submission)
- Undo/redo in the viewer before submitting
- Admin approval mode (optional - admin reviews submissions before they apply)
- Material cost tracking (deducts from player's inventory if survival mode)
- CoreProtect integration - all web-placed blocks logged for rollback

### Tech
- Block placement UI: raycast from camera through the 3D scene, highlight target face
- Ghost block rendering: separate vertex buffer with alpha, distinct from committed blocks
- WebSocket to server: send block change commands
- Plugin receives commands, validates permissions, applies changes, logs to CoreProtect

---

## 4. In-Game Chat from Website

### How it works
- Linked players can send chat messages from the web viewer
- Messages appear in-game with a [Web] prefix: `[Web] HereticSpawn: nice build!`
- In-game messages appear in the web viewer's chat panel (already have this via log tailing)
- Real-time via WebSocket

### Paper plugin: AubsCraftChat
- Listens for chat events, forwards to WebSocket
- Receives web messages via WebSocket, broadcasts to server with [Web] tag
- Supports private messages: `/msg <player>` from web
- Could support emoji rendering (web sends emoji, plugin converts to MC text)

### Beyond text chat
- **Proximity chat indicator**: when two players are near each other on the map, show a speech bubble
- **Location sharing**: "I'm at x=228, z=-243, come check this out!" with a clickable link that flies the camera there
- **Screenshot sharing**: player takes a screenshot in the viewer, shares it in chat as a clickable thumbnail

---

## 5. AI Villagers with Voice Chat

### The concept
Villagers in the game world become intelligent NPCs that players can talk to - via text or voice - from the website viewer.

### How it works
- **AubsCraftAI** Paper plugin spawns special villagers at designated locations
- Each villager has a personality, memory, and knowledge of the server world
- Players click on a villager in the web viewer to start a conversation
- Text chat: type messages, get responses (powered by Claude API)
- Voice chat: browser captures microphone, speech-to-text, AI responds, text-to-speech plays back
- The villager's chat history persists across sessions

### Villager types
- **Town Crier**: knows server news, recent events, who's online, what happened today
- **Cartographer**: knows the map, can give directions, describe biomes, suggest build locations
- **Historian**: knows block change history (via CoreProtect), can tell you who built what and when
- **Merchant**: manages a virtual economy (if the server has one)
- **Quest Giver**: ties into NotQuests plugin to offer and track quests
- **Lore Master**: tells stories about the server's history, significant builds, player achievements

### Tech stack
- Speech-to-text: Web Speech API (browser-native, free)
- AI responses: Claude API via SpawnDev's claude-api skill
- Text-to-speech: Web Speech API or a higher quality TTS service
- Villager rendering: special markers in the 3D viewer at villager positions
- Memory: per-villager conversation history stored in IndexedDB + server-side

### SpawnDev angle
- This is a PERFECT showcase for Claude API + Blazor WASM
- Voice chat in a browser-based 3D world talking to AI NPCs
- Nobody else is doing this in Blazor

---

## 6. Multi-Dimension Maps

### The Overworld, Nether, and End
- Dimension switcher in the UI (tabs or dropdown)
- Each dimension has its own chunk data, heightmap, and rendering
- Nether: lava rivers, glowstone ceiling, soul sand valleys, bastion remnants
- End: void background, end stone islands, chorus plants, end cities
- Dimension portals shown as markers linking the two maps

### Server data
- Overworld: `world/region/`
- Nether: `world/DIM-1/region/`
- End: `world/DIM1/region/`
- Same MCA/Anvil format, same RegionReader works for all three

### Nether-specific rendering
- Red/orange fog instead of blue sky
- Lava as the "water" equivalent (opaque, glowing)
- Ceiling rendering (Nether has a bedrock ceiling)
- Different block palette (netherrack, basalt, blackstone, crimson/warped stems)

### End-specific rendering
- Black/purple void background
- No heightmap - floating islands require full 3D for all chunks
- End crystal markers at the central fountain
- Dragon fight arena visualization

---

## 7. Player System on the Map

### Player list overlay
- Sidebar or floating panel showing all online players
- Each player: name, skin head icon, coordinates, current dimension
- Click a player to fly the camera to their position
- Admin: see player health, hunger, gamemode, inventory summary

### Player rendering in 3D
- Current: cyan beacon pillars at player positions
- Future: actual player skin heads or simple avatar models at positions
- Smooth position interpolation (updates every few seconds via RCON, interpolate between)
- Direction indicator (which way the player is facing)
- Activity indicator (idle, mining, building, fighting, AFK)

### Player profiles (public, opt-in)
- URL: `map.spawndev.com:44365/player/HereticSpawn`
- Stats: play time, blocks placed/broken, deaths, achievements
- Gallery: player's bases with screenshots
- Build showcase: featured builds with flyover camera paths
- Linked MC account badge

---

## 8. WebXR VR Administration

### The concept
Put on a VR headset, open the AubsCraft viewer, and fly through your Minecraft world in VR. Manage the server by pointing at things and using hand controllers.

### How it works
- WebXR API (supported in Chrome, Edge, Quest browser)
- SpawnDev.BlazorJS wraps the WebXR API (or we create wrappers)
- The existing WebGPU renderer outputs to a VR display instead of a flat canvas
- Hand controllers for camera movement (fly mode) and interaction (click on claims, players, etc.)
- Head tracking for look direction

### VR-specific features
- **God mode admin**: float above the world, see everything at once, reach down and interact
- **Scale model**: shrink the world to tabletop size, examine builds up close
- **Teleport**: point at a location, click to teleport the camera there
- **Voice commands**: "show claims", "fly to spawn", "kick player X" (voice + AI)
- **Mixed reality**: overlay the Minecraft map on your real desk (AR mode on Quest 3)

### SpawnDev tech
- SpawnDev.BlazorJS already wraps many browser APIs
- WebXR wrappers would be a new addition to the SpawnDev.BlazorJS ecosystem
- Combined with ILGPU WebGPU rendering, this could be the first Blazor WASM VR app
- This is a HUGE differentiator for the SpawnDev ecosystem

### Hardware
- Meta Quest 2/3 (standalone, built-in browser supports WebXR)
- PCVR headsets via SteamVR + Chrome
- Vivecraft players already on the server - they'd love this

---

## 9. Custom Paper Plugins Needed

| Plugin | Purpose | Complexity |
|--------|---------|-----------|
| **AubsCraftLink** | Account linking (verification codes) | Small |
| **AubsCraftClaims** | GriefPrevention claim data API | Small |
| **AubsCraftChat** | Web-to-game chat bridge | Small |
| **AubsCraftBuild** | Browser creative mode block placement | Medium |
| **AubsCraftAI** | AI villager NPCs | Medium |
| **AubsCraftTracker** | Real-time player position broadcasting | Small |
| **AubsCraftInventory** | Web inventory viewing/management | Medium |
| **VRDetect** | VR player detection | DONE |

All plugins communicate with the AubsCraft server via HTTP API or WebSocket. The admin server acts as the bridge between plugins and the web client.

### Plugin development
- Paper API 1.21.5
- Java 21 (Microsoft OpenJDK on the server)
- Compile against: `M:\opt\minecraft\server\libraries\io\papermc\paper\paper-api\1.21.5-R0.1-SNAPSHOT\`
- Deploy to: `M:\opt\minecraft\server\plugins\`

---

## 10. Quick Win Ideas (Low effort, high impact)

1. **Compass HUD**: show N/S/E/W directions on the viewer
2. **Coordinates display**: show current camera X/Y/Z like the game's F3 screen
3. **Biome labels**: hover over terrain to see biome name
4. **Day/night cycle**: animate the lighting based on server time (already have RCON time)
5. **Minimap corner**: small top-down view in corner showing your position on the full map
6. **Spawn marker**: always-visible marker at world spawn point
7. **Death markers**: show where players died (from log parsing, skull icon)
8. **Bed markers**: show player bed locations (home/respawn points from Essentials)
9. **Nether portal markers**: highlight active portals linking dimensions
10. **Block counter**: click on a type of block, see how many visible in the current view

---

## 11. Monetization Ideas (if ever needed)

Not a priority, but worth thinking about:
- **Premium profiles**: enhanced public profiles with more stats, custom themes
- **Server hosting**: if AubsCraft becomes a platform, host other servers' maps
- **API access**: let other developers build on the AubsCraft viewer (plugin ecosystem)
- **Merchandise**: generate 3D prints of player bases from the map data (export to STL)
- None of this is needed now. The project is about proving SpawnDev's capabilities and giving Aubs the best server ever.

---

## The SpawnDev Showcase Angle

Every feature in AubsCraft demonstrates a SpawnDev library:

| Feature | SpawnDev Tech |
|---------|--------------|
| 3D rendering | SpawnDev.ILGPU (WebGPU kernel) |
| JS interop | SpawnDev.BlazorJS (IndexedDB, WebSocket, WebXR) |
| WebRTC streaming | SpawnDev.RTLink |
| AI integration | Claude API via BlazorJS |
| Offline caching | SpawnDev.BlazorJS OPFS/IndexedDB wrappers |
| VR support | SpawnDev.BlazorJS WebXR wrappers (future) |
| Crypto/auth | SpawnDev.BlazorJS.Cryptography |

AubsCraft is the reference application that proves the entire SpawnDev ecosystem works together. Every feature we build here becomes a case study for the NLnet grant application, dev.to articles, and conference talks.

And Aubs gets the coolest Minecraft server any kid has ever had.

---

---

## 12. Spectator Cam - The Streaming Killer Feature

### The concept
Aubs is playing Minecraft in VR via Vivecraft on her Quest. She wants to stream it. Instead of recording the VR headset view (low quality, wrong aspect ratio, performance hit), she opens our AubsCraft viewer in a browser tab, clicks her name in the player list, clicks "Spectate" - and our WebGPU renderer follows her around in third person as she plays.

OBS captures the browser tab. Twitch/YouTube gets a beautiful, smooth, high-quality third-person view of her gameplay - rendered entirely independently from her VR session.

### Why this is special
- **Zero performance impact on the player** - the spectator view is a separate browser rendering from position data, not a screen capture
- **Better than screen recording** - we control the camera angle, resolution, quality, effects
- **Works for ANY player** - Java, Bedrock, VR, all using the same spectator system
- **Multiple spectators** - multiple people can spectate different players simultaneously
- **WebRTC sharing** - one person runs the spectator cam, others watch as a live video stream without needing to render anything

### The flow
1. Player list shows all online players with "Spectate" button
2. Click Spectate -> camera smoothly flies to the player's position
3. Camera locks to third-person follow mode (configurable offset)
4. AubsCraftTracker plugin pushes position/direction updates every 100-200ms
5. Camera smoothly interpolates between updates (no jitter)
6. Chunks auto-load around the followed player as they move through the world
7. URL can be bookmarked: `?spectate=HereticSpawn` to auto-spectate on page load

### Camera offset options
- **Classic third-person**: Behind and above (like Minecraft F5)
- **Overhead**: Bird's eye following from above
- **Side view**: Watching from the side (good for build streams)
- **Cinematic**: Slow orbit around the player
- **Custom**: User sets distance, height, angle

### Stream-quality features
- Resolution lock (720p/1080p/1440p) regardless of browser window size
- FPS lock (30/60) for consistent stream quality
- Motion blur effect (shader post-process) for cinematic look
- Depth of field (blur distant/near objects, focus on player)
- Name tags above other visible players
- Event popups ("HereticSpawn got an achievement!")

### Multi-stream setup
Imagine a Minecraft event on AubsCraft:
- Camera 1: Following player A (build competition)
- Camera 2: Following player B (another builder)
- Camera 3: Free-flying drone cam with cinematic path
- Camera 4: Top-down overview of the build area
- All four running in separate browser tabs, all captured by OBS as different scenes
- One person controls all four cameras, switches between them for the stream

This is **professional esports broadcast quality** from a Minecraft server using a browser. Nobody else has this.

---

*"The needs of the many outweigh the needs of the few. But sometimes, the needs of the one - a 10-year-old who loves Minecraft - drive the innovation that serves them all." - Tuvok, probably*
