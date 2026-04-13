# AubsCraft: Wild Ideas Brainstorm

**Date:** 2026-04-12
**Philosophy:** Aim as far as possible. Every crazy idea has a kernel of brilliance.

---

## 1. AI Build Assistant

### The concept
Describe what you want to build in plain English. Claude generates a 3D blueprint in the viewer.

"Build me a medieval castle with two towers, a drawbridge, and a moat."

### How it works
- User opens the blueprint editor
- Types or speaks a description
- Claude API generates a structured block-by-block layout
- Blueprint renders instantly in the viewer as a transparent overlay
- User can modify, rotate, scale, then commit to the world

### Why this is possible
- Claude understands Minecraft architecture deeply
- Output format: JSON array of `{x, y, z, blockType}` entries
- ILGPU kernel renders the blueprint as ghost blocks
- User places at their claim location
- Iterative: "make the towers taller", "add windows", "change to dark oak"

### The magic moment
Aubs says "Build me a fairy castle with pink towers and a rainbow bridge" into her VR headset. 10 seconds later, a transparent fairy castle appears in front of her. She walks through it in VR, says "make the towers taller", and they grow. She clicks "Build" and it starts placing blocks in the real server.

---

## 2. Player Journey Trails

### The concept
Visualize where a player has been throughout their ENTIRE server history. Their complete journey rendered as a glowing trail through the world.

### How it works
- CoreProtect stores login/logout locations
- AubsCraftTracker records position history at regular intervals
- Trail rendered as a glowing line through the world (ILGPU line/ribbon geometry)
- Color-coded by time: old trails fade to dim, recent trails glow bright
- Toggle by player: see just Aubs' trail, or everyone's, or just today's

### What you see
- A spider web of glowing lines crisscrossing the world
- Dense clusters where players spend time (bases, farms, mines)
- Long lines showing exploration journeys
- First-day trail: see exactly where a new player explored on day one

### Heatmap mode
- Instead of lines, show a volumetric heatmap
- Bright = frequently visited, dark = rarely visited
- ILGPU kernel processes all position data, generates density field
- Render as semi-transparent colored voxels overlaid on the world
- Admin use: find popular areas, plan infrastructure

---

## 3. Real Weather Integration

### The concept
Connect to a real weather API. Your local weather controls the Minecraft server weather.

### How it works
- Server checks weather API every 30 minutes
- Aubs' location (Ithaca, NY) drives the weather
- Real rain = MC rain. Real snow = MC snow. Real thunderstorm = MC thunder.
- Real temperature mapped to MC time of day (hot = noon, cold = midnight... or seasonal)

### The fun part
- Aubs looks outside: it's snowing
- She logs into Minecraft: it's snowing there too
- She opens the web viewer: she sees the snow particles falling through the 3D world
- She tells her friend: "it's snowing in AubsCraft AND in real life!"

### Data source
- OpenWeatherMap API (free tier: 60 calls/hour)
- Location: Ithaca, NY (configurable per server)
- Mapping: clear->clear, clouds->clouds, rain->rain, snow->snow, thunderstorm->thunder

---

## 4. Nether Portal Network Visualizer

### The concept
Show ALL nether portals as a connected graph. Visualize the network of portals linking locations across the overworld and nether.

### How it works
- Scan world data for nether portal blocks (block ID for obsidian frame + portal blocks)
- Calculate which overworld portals connect to which nether portals (x/8, z/8 mapping)
- Render as a node graph overlaid on the world:
  - Nodes: portal locations (glowing purple markers)
  - Edges: lines connecting linked portals (purple gradient lines)
  - Labels: distance between portals, travel time saved

### Split view
- Left side: Overworld with portal markers
- Right side: Nether with corresponding portals
- Lines connect matching portals across dimensions
- Click a portal on either side to see its partner

### Travel planner
- "How do I get from my base to SpudArt's base fastest?"
- Calculate: direct overworld distance vs nether shortcut
- Show both paths, time estimates (walking speed: 4.3 blocks/sec)
- Optimal portal placement suggestions: "build a portal at (x, z) to cut 5 minutes off the trip"

---

## 5. Treasure Hunt System

### The concept
Admin hides virtual treasures in the world. Players use the web viewer with "hot/cold" proximity hints to find them. AR-style gaming layer on top of Minecraft.

### How it works
- Admin places treasure markers at specific coordinates (via admin panel, not in-game)
- Treasures are invisible in-game - only discoverable through the web viewer
- Web viewer shows a compass/proximity indicator when near a treasure
- "Warmer... warmer... HOT!" as you get closer
- Found it! Player gets a reward (in-game item via AubsCraftBuild plugin, or points/badge)

### Treasure types
| Type | Difficulty | Hint |
|------|-----------|------|
| Surface gem | Easy | Visible in viewer as sparkle effect |
| Underground cache | Medium | Compass only, must use X-ray mode to find |
| Deep artifact | Hard | Only a vague directional hint |
| Legendary relic | Expert | Riddle clues that reference real world locations |

### Events
- Weekly treasure hunt events with new placements
- Leaderboard: who found the most treasures
- Limited-time seasonal treasures (holiday events)
- Player-placed treasures (hide gifts for friends)

---

## 6. Noteblock Music Visualizer

### The concept
Noteblock contraptions on the server play music. The web viewer visualizes the music in 3D - particles, colors, and effects that pulse with the notes.

### How it works
- Detect noteblock activations via plugin event listener
- Map note pitch + instrument to visual effects
- ILGPU particle kernel generates music visualizer geometry
- Each note triggers: colored particle burst at the noteblock location
- Pitch maps to color (low = red, mid = green, high = blue)
- Instrument maps to particle shape (piano = circles, guitar = diamonds, drums = squares)

### Concert mode
- Build a stage area with noteblocks
- Point spectator cam at the stage
- Music plays through Web Audio API (positional, from noteblock locations)
- Visual effects dance with the music
- Stream it to Twitch - a virtual Minecraft concert

---

## 7. Server World Records Board

### The concept
Automatically track and display world records and achievements based on server data.

### Records tracked
| Record | Source | Example |
|--------|--------|---------|
| Tallest structure | CoreProtect / scan | "HereticSpawn built to Y=256 at (x, z)" |
| Deepest mine | CoreProtect | "SpudArt mined to Y=-58" |
| Longest rail line | Block scan for rails | "420 blocks from spawn to east village" |
| Largest claim | GriefPrevention | "HereticSpawn: 10,000 blocks" |
| Most blocks placed (day) | CoreProtect | "SpudArt: 2,847 blocks on April 10" |
| Longest session | Session log | ".Noob607: 8 hours straight" |
| First to reach The End | Advancement data | "HereticSpawn on March 20" |
| Most deaths | Stats | "SpudArt: 47 deaths" |
| Fastest speedrun | Custom tracking | "Reach End in 3 hours from first join" |

### Display
- Physical "Hall of Records" build on the server with signs
- Web viewer: dedicated records page with visualizations
- 3D bar charts rendered with ILGPU (blocks as bars!)
- Animated when records are broken ("NEW RECORD!" fireworks)

---

## 8. Collaborative World Canvas

### The concept
A flat area on the server designated as a pixel art canvas. Web viewers can place colored blocks to create collaborative pixel art - like Reddit's r/Place but in Minecraft.

### How it works
- Admin designates a flat area (e.g., 128x128 blocks)
- Web viewers see a top-down orthographic view of the canvas
- Click to place a colored block (wool/concrete palette)
- Rate limit: 1 block per 10 seconds per user
- The canvas is visible both in-game AND in the web viewer
- Time-lapse replay shows the canvas evolving

### Why it's fun
- Collaborative creation across web and in-game
- Competition: factions try to claim space
- Events: "Draw the server logo together"
- History: the canvas tells the story of the community

---

## 9. Dynamic Map Markers + Annotations

### The concept
Players and admins can place custom markers, notes, and drawings ON the 3D map.

### Marker types
- **Pin** - labeled point marker (waypoint)
- **Area** - highlighted region with description
- **Path** - drawn route between points (walking directions)
- **Note** - floating text at a location
- **Arrow** - directional indicator
- **Circle** - radius around a point (danger zone, farm area)

### Drawing tools
- Freehand draw on the map surface (like Google Maps drawing tools)
- Straight line tool for paths and routes
- Shapes: rectangle, circle, polygon
- Colors and labels customizable

### Sharing
- Personal markers (only you see them)
- Shared markers (visible to all authenticated users)
- Admin markers (event areas, restricted zones, public info)
- URL with markers: `?markers=my-farm-route`

---

## 10. Server Event System

### The concept
Schedule server events with countdown timers, automatic configuration changes, and viewer integration.

### Event types
- **Build competition** - auto-create build plots, timer, voting, winner announcement
- **PvP arena** - designated area, gamemode changes, spectator cam, brackets
- **Treasure hunt** - auto-place treasures, timer, leaderboard
- **Race** - checkpoint course, timer, spectator cam follows racers
- **Boss fight** - spawn custom mob, track damage, team coordination
- **Exploration challenge** - first to find a structure, compass hints

### Event lifecycle
1. Admin creates event via web panel (type, time, rules)
2. Countdown shows on the map viewer AND in-game
3. Server auto-configures: gamemode changes, area protection, TP restrictions
4. Event runs with live tracking on the viewer
5. Results calculated, winners announced (in-game + web + Discord)
6. Event archived with highlights reel (time-lapse, stats)

### Spectator integration
- Event spectator cam auto-follows the action
- Multiple camera angles for multi-player events
- Live scoreboard overlay
- Viewers can bet on outcomes (virtual currency, not real money)

---

## 11. Procedural World Art

### The concept
Use ILGPU to generate art from the Minecraft world itself.

### Ideas
- **Pixel art from terrain** - top-down render using block colors as pixels, export as PNG wall art
- **3D print your base** - export chunk data as STL/GLTF for 3D printing
- **Isometric poster** - render the world in isometric view, poster-quality output
- **Minecraft portrait** - convert a photo to Minecraft block art, place it on the server
- **Topographic map** - classic topo lines generated from heightmap data, printable
- **Block palette poster** - every block type used on the server, arranged beautifully

### 3D printing pipeline
1. Select region in viewer
2. ILGPU kernel generates mesh (already done!)
3. Export mesh as GLTF/STL
4. Scale to physical size (1 block = 1cm?)
5. Send to 3D printer or upload to Shapeways/similar
6. Receive a physical copy of your Minecraft build

---

## 12. Smart Notifications

### Context-aware alerts
Not just "player joined" - intelligent notifications based on what you care about.

| Notification | Trigger | Who cares |
|-------------|---------|-----------|
| "SpudArt is building near your base" | Block placement within 50 blocks of your claim | Claim owner |
| "Your crops are ready to harvest" | Game tick calculation for crop growth time | Farmer players |
| "Creeper explosion near your base!" | TNT/explosion event near a claim | Claim owner |
| "Your friend .Noob607 just logged in" | Friend join event | Friends |
| "New player joined: FirstTimer123" | First-ever join | Admins |
| "TPS dropped below 15" | Performance threshold | Admins |
| "Someone opened your chest" | Container event in your claim by non-trusted | Claim owner |
| "Your build got 5-star rated!" | Rating event | Builders |
| "Treasure hunt starts in 10 minutes" | Scheduled event | Everyone |

### Delivery
- Browser push notification (works even when tab is in background)
- In-viewer toast notification
- Sound alert (configurable per notification type)
- Email digest (daily summary option)
- Discord webhook per notification type

---

*"Every block placed is a statement. Every feature built is a promise. Every crazy idea explored is a step toward something nobody's ever seen before." - The AubsCraft Team*
