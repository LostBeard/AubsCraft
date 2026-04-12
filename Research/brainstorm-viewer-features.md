# AubsCraft 3D World Viewer - Feature Brainstorm

**Date:** 2026-04-12
**Contributors:** TJ (Captain), Geordi

This is the living brainstorm for where the 3D viewer is going. Not just an admin tool - a player-facing feature that adds real value to the AubsCraft experience.

---

## Vision

The viewer becomes a first-class companion to the Minecraft server - used by admins for management, by players for gameplay advantage, and by streamers for unique content. Accessible from any browser, no Minecraft client required.

---

## 1. Drone Cam / Twitch Streaming Mode

**The idea:** Free-flying camera through the live world that can be streamed to Twitch. Players or admins fly around the server world in real-time from a browser tab.

**What makes this cool:**
- Streamers can show off builds without being in-game
- "Flyover tours" of the server for recruitment/hype
- Spectator cam for events (build competitions, PvP arenas)
- AFK stream that runs 24/7 showing the server world with live player markers
- No Minecraft client needed - just a browser + OBS

**Implementation ideas:**
- Cinematic camera mode: smooth bezier path flight, configurable speed
- Auto-orbit: pick a point, camera orbits it slowly (great for build showcases)
- Follow-player mode: camera tracks a player's position with offset (needs live position updates via SignalR)
- Waypoint recording: record a camera path, play it back in a loop
- OBS Browser Source friendly: render canvas at specific resolution (1920x1080), no UI chrome in streaming mode, optional overlay with server name/player count
- Keyboard shortcuts: P = play/pause path, F = follow player, O = orbit, Space = free fly
- URL parameters for stream setup: `?mode=orbit&target=228,64,-243&speed=0.5&resolution=1080p&hideUI=true`

**Live data overlays (optional, toggleable):**
- Player count + names
- Server TPS
- Time of day / weather
- Current coordinates
- Mini-map in corner

---

## 2. Player Browser Tool - Gameplay Aid

**The idea:** Players log in with their MC account, see the world from a bird's eye view, plan builds, find resources, navigate.

**What makes this cool:**
- Plan your base layout before building in-game
- Find biomes, structures, resource deposits from the map
- Navigate to places without wasting in-game time
- Check on your base from your phone while at work/school
- Share coordinates and screenshots of locations with other players

**Features:**
- Search by biome type, structure type, or block type
- Coordinate bookmarks (personal, per-player)
- Distance/direction indicator from current player position
- Screenshot tool: capture current view as PNG, shareable link
- Mobile-friendly touch controls (pinch zoom, swipe pan)

---

## 3. Base Viewer / Builder (Offline Base Access)

**The idea:** Players can view their claimed area in full 3D even when offline, and potentially queue modifications.

**View mode (immediate):**
- Full 3D render of chunks within player's claim boundaries
- Last-known state (loaded from saved world data)
- Inventory of blocks used in the build (material list)
- Share link: "Check out my base" with a URL that opens to their claim
- Before/after comparison (CoreProtect history integration?)

**Edit mode (future, requires careful design):**
- Place/remove blocks in a "planning layer" overlaid on the real world
- Planning layer is transparent/ghosted, doesn't modify the actual world
- Export plan as schematic (WorldEdit compatible)
- Material shopping list generated from the plan
- Eventually: queue changes to be applied in-game (requires trust system, admin approval)

**Account linking required** - see section 6 below.

---

## 4. GriefPrevention Claim Visualization

**The idea:** All player claims visible on the map as colored overlaid regions, toggleable, with a list for quick navigation.

**Current server data:**
- Plugin: GriefPrevention (golden shovel tool)
- Player data at: `M:\opt\minecraft\server\plugins\GriefPreventionData\PlayerData\`
- Format: UUID files containing claim block counts (accrued + bonus)
- Claim boundaries: stored by GP internally - need to either read its data format or query via RCON/plugin API
- 3 known players: HereticSpawn (TJ), SpudArt, .Noob607 (Bedrock via Geyser)

**Reading claim data - options:**
1. **RCON commands:** `claimlist <player>` returns claim coordinates. Parse the response.
2. **GP plugin API:** Write a small Paper plugin that exposes claims as JSON via an HTTP endpoint or RCON response.
3. **Direct file read:** GP may store claims in a database or flat files - need to investigate GP's internal storage format more deeply. The ClaimData folder was empty, which might mean claims are stored in the world's plugin data.
4. **CoreProtect integration:** CoreProtect tracks ALL block changes in `database.db` (SQLite). Could reconstruct claim boundaries from gold shovel interactions.

**Visualization:**
- Semi-transparent colored rectangles on the map at claim boundaries
- Color-coded by owner (each player gets a distinct color)
- Click a claim: shows owner name, size (blocks), creation date, trust list
- Toggle: show all claims / show only my claims / hide claims
- Claim list sidebar: all claims sorted by owner, size, or distance from camera
- Click-to-teleport: click a claim in the list, camera flies to it
- Admin view: show expired/abandoned claims in red
- Overlap detection: highlight overlapping or adjacent claims

**GriefPrevention data details to research:**
- Does GP expose claim bounds via RCON? Test: `gp:claimlist` or similar
- Does GP store data in world/data/ or its own database?
- What's the format of sub-claims (subdivisions within a claim)?

---

## 5. Player Features List

### For ALL players:
- View the full server map in 3D
- See their own claims highlighted
- Bookmark locations
- Measure distances between points
- Find biomes/structures
- Screenshot and share views
- Mobile browser support

### For linked/authenticated players:
- See their base in full 3D
- View their claim details (size, trust list, remaining blocks)
- See other players' claims (public info only)
- Set home/waypoint markers visible on map
- Coordinate sharing with other players
- Personal camera paths (saved per account)

### For admins:
- Everything above, plus:
- All current admin panel features (whitelist, ban, kick, etc.)
- Inspect any claim (owner, trust list, expiry)
- Teleport to any location (via RCON)
- CoreProtect block history lookup by clicking a location
- Manage expired/abandoned claims
- Server performance overlay (TPS, entity count, loaded chunks)
- Player activity heatmap (where do players spend time?)

---

## 6. Account Linking (MC Character + Site Account)

**Required for:** offline base viewing, personal bookmarks, claim ownership, edit mode.

**Options:**

### Option A: In-game verification code
1. Player visits the web viewer, creates an account (email + password or OAuth)
2. They get a unique 6-digit code
3. In Minecraft, they type `/link <code>`
4. Custom Paper plugin receives the command, verifies the code via the admin server API
5. UUID is now linked to the web account
Done. Simple, proven pattern (Discord bots use this).

### Option B: Microsoft OAuth
1. Player signs in with their Microsoft account on the web viewer
2. We verify their Xbox/MC identity via the Xbox Live API
3. Map Xbox Gamertag to MC UUID
More seamless but requires Microsoft OAuth integration and may not work for Bedrock/Geyser players.

### Option C: Geyser/Floodgate awareness
- Bedrock players come through Geyser with fake UUIDs (prefix `00000000-0000-0000-0009-`)
- The `.Noob607` player is a Bedrock player (Geyser-prefixed UUID)
- Need to handle both Java and Bedrock identity linking
- Floodgate already maps Bedrock Xbox IDs - could read its data for linking

**Recommendation:** Option A (verification code) is simplest and works for both Java and Bedrock. Build a small `AubsCraftLink` Paper plugin.

---

## 7. Drone Cam - Technical Details

### Camera Modes

| Mode | Controls | Use Case |
|------|----------|----------|
| Free Fly | WASD + mouse | Manual exploration |
| Orbit | Auto-rotates around a point | Build showcases |
| Follow | Tracks a player position | Spectating |
| Cinematic | Plays back recorded waypoint path | Streaming |
| Top-Down | Fixed overhead view, pan/zoom | Classic map view |

### Streaming Optimizations
- Render at fixed resolution (720p/1080p/1440p selectable)
- Disable UI elements in stream mode
- Stable FPS target (lock to 30 or 60)
- Optional: encode to WebRTC stream directly from canvas (then others can watch WITHOUT running the renderer themselves)
- Server-side rendering option (future): render on the server, stream as video (would require GPU on server)

### WebRTC Live Streaming (advanced)
- Capture canvas with `captureStream()`
- Create WebRTC peer connection
- Stream the renderer output directly to viewers
- One person runs the drone cam, everyone else gets a live video feed
- SpawnDev.RTLink could handle the WebRTC signaling!
- This means: player flies the drone, their friends watch on Twitch OR directly via our viewer

---

## 8. Data Sources Available on Server

| Plugin | Data | Location | Format | Useful For |
|--------|------|----------|--------|------------|
| GriefPrevention | Claims, player blocks | `plugins/GriefPreventionData/` | Flat files + internal | Claim visualization |
| CoreProtect | Block change history | `plugins/CoreProtect/database.db` | SQLite | Rollback viewer, activity heatmaps |
| Essentials | Player homes, warps, last location | `plugins/Essentials/userdata/` | YAML per UUID | Warp list, player locations |
| LuckPerms | Permissions, groups | `plugins/LuckPerms/*.db` | H2 database | Role-based web viewer access |
| MyPet | Player pets | `plugins/MyPet/pets.db` | SQLite | Pet rendering/markers |
| NotQuests | Quest data | `plugins/NotQuests/database_sqlite.db` | SQLite | Quest markers on map |
| RealisticSeasons | Season state | `plugins/RealisticSeasons/` | Config | Season-accurate rendering (tree colors, snow) |
| VRDetect | VR player detection | `plugins/VRDetect/` | Custom | VR player indicators on map |
| TrainCarts | Rail routes | `plugins/Train_Carts/` | Config | Rail line visualization |
| Geyser/Floodgate | Bedrock player mapping | `plugins/Geyser-Spigot/` + `floodgate/` | Config | Cross-platform account linking |
| usercache.json | UUID to username | `server/usercache.json` | JSON | Name resolution |
| RCON | Live commands | Port-based | Text protocol | Real-time player pos, time, weather |

### Known Players (as of 2026-04-12)
- **HereticSpawn** (51284fe7) - TJ, Java edition
- **SpudArt** (2f0a5428) - Java edition
- **.Noob607** (00000000-...-0a90b894) - Bedrock via Geyser/Floodgate

---

## 9. Quick Wins (could build soon)

1. **Essentials warp markers** - Read warp YAML files, show as labeled markers on map
2. **Claim overlay via RCON** - Test if `claimslist` or `claiminfo` RCON commands return parseable data
3. **Player last-logout markers** - Essentials userdata has `logoutlocation` with exact coords
4. **Time-of-day lighting** - Already have RCON time query, adjust sun direction + ambient color
5. **Top-down screenshot mode** - Orthographic camera, perfect for sharing map images
6. **URL deep-linking** - `?x=228&z=-243&zoom=5` opens directly to a location

---

## 10. Long-term Vision

AubsCraft's web viewer becomes a showcase for what SpawnDev.ILGPU + SpawnDev.BlazorJS can do:
- GPU-accelerated 3D rendering in a browser via Blazor WASM
- Zero-copy WebGPU pipeline
- Real-time data streaming via SignalR
- WebRTC for peer-to-peer video sharing
- PWA installable on phones and desktops

This isn't just a Minecraft tool. It's a proof-of-concept for the entire SpawnDev ecosystem. Every feature we build here proves Blazor WASM is a first-class application platform.

And Aubs gets the coolest Minecraft server any kid has ever had.
