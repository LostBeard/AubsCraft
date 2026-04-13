# AubsCraft: Next-Level Feature Brainstorm

**Date:** 2026-04-12
**Contributors:** TJ (Captain), Tuvok
**Status:** Dreaming big, aiming for the horizon

---

## 1. Underground Explorer / X-Ray Mode

### The concept
Toggle a mode that strips away the surface and shows the underground world. Caves, ravines, mineshafts, dungeons, ore veins - all visible. Not a cheat tool - a discovery and planning tool.

### How it works
- Slider: "Depth" control strips layers from the top down
- At depth 0: normal surface view
- At depth 10: top 10 blocks removed, you see shallow caves
- At depth 50: you see deep caves, ravines, mineshafts
- At depth 128: you see only the deep dark and bedrock
- Alternative: "Slice" mode - show only a single Y-level (like a CT scan of the world)

### Ore highlighting (admin only)
- Toggle ore overlay: diamonds glow blue, iron glows silver, gold glows yellow
- Each ore type has a particle effect or glow at its world position
- ILGPU kernel scans block data for ore IDs, generates glow markers
- Useful for: admin checking if someone is X-ray mining (suspiciously straight tunnels to ores)

### Cave rendering
- Caves are hollow spaces underground - our full 3D kernel already renders cave walls
- The trick is REMOVING the surface blocks to reveal the caves below
- Approach: in the ILGPU kernel, add a `maxY` parameter. Blocks above `maxY` are treated as air.
- The depth slider adjusts `maxY` from 320 down to -64.

### Dungeon/structure finder
- Scan chunk data for structure-specific blocks: mob spawners, chests, end portal frames
- Highlight their positions on the map with icons
- Click to fly to any discovered structure
- Admin tool: verify world generation, find unlooted structures for events

### Performance
- Same kernel, just with a Y-cutoff parameter. Zero extra cost.
- Ore highlighting: ILGPU kernel generates glow marker positions, minimal vertex data
- The underground typically has LESS geometry than the surface (mostly solid stone)

---

## 2. Build Blueprint System

### The concept
Design a structure in the viewer using a grid-based blueprint tool. See it overlaid on the world as a transparent ghost. Share blueprints with other players. Import/export as Minecraft schematics.

### Blueprint editor
- Enter Blueprint Mode (separate from Edit Mode)
- 3D grid appears at the selected location
- Place "planned blocks" on the grid - they render as transparent, wireframe, or holographic
- Rotate, mirror, copy-paste sections
- Blueprint exists only in the viewer - doesn't affect the real world
- Save/load blueprints to OPFS cache

### Blueprint overlay
- Overlay the blueprint on the real world to see how it fits
- Real blocks show solid, blueprint blocks show as transparent outlines
- Color coding: green = matches a real block already placed, red = needs to be placed, gray = planned
- This is exactly how real-world architects use AR overlays

### Blueprint sharing
- Export as `.schematic` or `.litematic` (Minecraft schematic formats)
- Share URL: `map.spawndev.com/blueprint/abc123`
- Other players can view, fork, and modify shared blueprints
- Community blueprint library

### Material calculator
- Blueprint automatically calculates required materials
- "This build needs: 450 oak planks, 200 stone bricks, 64 glass panes"
- Cross-reference with player inventory (if linked) to show what they still need to gather

### VR blueprint editing
- In VR, the blueprint grid is in front of you like a 3D modeling workspace
- Place blocks with hand controllers - intuitive, spatial
- Scale the blueprint up/down to work at different levels of detail
- Walk inside the blueprint to check interior layout

---

## 3. Seasons + Weather Visualization

### Data source: RealisticSeasons plugin
The server has RealisticSeasons installed which changes:
- Tree leaf colors (green -> orange/red -> bare -> green)
- Snow coverage based on season
- Crop growth rates
- Day length

### Seasonal rendering
| Season | Changes |
|--------|---------|
| Spring | Bright green grass, flowers blooming, rain common |
| Summer | Full green, long days, clear skies |
| Autumn | Orange/red/yellow leaves, shorter days, fog |
| Winter | Snow on ground, bare trees, frost particles, short days |

### Implementation
- Query RealisticSeasons via RCON or plugin API for current season
- Swap leaf texture tint colors based on season (vertex color modification in kernel)
- Add snow layer on top of blocks in winter (extra face at Y+1 with snow texture)
- Adjust sky color and fog based on season
- Particle effects: falling leaves in autumn, snowflakes in winter

### Weather in the viewer
- Current weather from RCON: clear, rain, thunder
- Rain: falling particle streaks (ILGPU particle kernel)
- Snow: gentle falling white particles
- Thunder: screen flash + lightning bolt render + thunder sound
- Fog: increase fog density during rain/snow

---

## 4. World Timeline / Time Machine

### The concept
Scrub a timeline slider to see the world at any point in history. Watch builds appear, terrain change, civilizations grow. Powered by CoreProtect's complete block change history.

### How it works
1. Select a region (or the entire visible area)
2. Timeline slider appears: server creation date to present
3. Drag the slider to any date/time
4. The viewer shows the world AS IT WAS at that moment
5. Play button: animate the timeline forward (time-lapse)

### Technical approach
- Start with current world state
- To go BACKWARD: undo block changes from CoreProtect (reverse order)
- To go FORWARD: re-apply block changes
- ILGPU kernel re-meshes the modified chunk data at each keyframe
- Pre-compute keyframes at intervals (every hour/day) for fast scrubbing
- Between keyframes: interpolate individual block changes

### Use cases
- **Admin:** "When did this griefing happen?" Scrub to find the moment.
- **Builder:** "Watch my castle being built" - time-lapse of a build project
- **History:** "What did spawn look like on day one?" - nostalgia trip
- **Evidence:** Before/after comparison for grief reports

### Performance
- CoreProtect queries are indexed by time and location
- Only process block changes in the visible region
- Incremental mesh updates: only re-mesh chunks that changed between keyframes
- ILGPU kernel handles the re-meshing (no CPU fallback)

---

## 5. Competitive / Social Features

### Build Competition System
- Admin creates a competition: defines the build area, theme, deadline
- Players build within their assigned plots
- Web viewers can tour all entries
- Voting system: web visitors rate builds 1-5 stars
- Leaderboard shows top builds in real-time
- Winner announced on the map with fireworks + announcement
- Archive: past competitions preserved as viewable exhibits

### Achievement Showcase Wall
- Physical location on the server (or virtual in the viewer) showing all player achievements
- Each player gets a "gallery" page in the viewer
- Advancement progress bars
- Rarest achievements highlighted
- Server-wide stats: "First player to reach the End", "Most diamonds mined"

### Build Rating System
- Any player can submit a build for rating
- Submission includes: location, name, description, screenshot
- Other players rate 1-5 stars from the viewer
- Top-rated builds featured on the homepage
- "Build of the week" spotlight

### Friend System
- Add friends from the web viewer
- See friends' online status, last position, recent activity
- Share bookmarks, blueprints, screenshots with friends
- Friend-only spectate permission
- "Friends nearby" notification when friends are close to your base

---

## 6. Server Dashboard for the World (Public Page)

### The concept
A public-facing page (no login required) that shows server status, recent activity, and a live preview of the world. Like a server listing page but way better.

### What visitors see
```
+--------------------------------------------------+
| AubsCraft - mc.spawndev.com                       |
|                                                    |
| [Live 3D Preview - rotates slowly around spawn]   |
|                                                    |
| 3 players online | 24/7 uptime | Paper 1.21.5     |
|                                                    |
| Recent activity:                                   |
| - HereticSpawn completed "The End?" advancement    |
| - SpudArt built a new bridge at (450, -120)       |
| - .Noob607 joined for the first time!              |
|                                                    |
| Top builds:            Server stats:               |
| 1. Castle (4.8 stars)  Blocks placed: 1.2M        |
| 2. Bridge (4.5 stars)  Play time: 480 hours        |
| 3. Farm (4.2 stars)    Unique players: 3           |
|                                                    |
| [Join Server] [View Full Map] [Discord]            |
+--------------------------------------------------+
```

### The live preview
- Auto-orbiting camera around spawn (or a featured location)
- Renders in real-time using the same WebGPU viewer
- Low quality settings for fast loading (heightmap only, reduced draw distance)
- Shows player markers moving around
- Click to enter the full viewer (prompts login)

### SEO + discoverability
- Server description, tags, Minecraft version in meta tags
- OG image: auto-generated screenshot of spawn
- Schema.org markup for the server listing
- Shareable link for server recruitment

---

## 7. Plugin Marketplace Concept (Far Future)

### The idea
Other Minecraft server owners want what AubsCraft has. Package it.

### What we'd offer
- **AubsCraft Viewer** - the WebGPU 3D world viewer as a standalone product
- **AubsCraft Admin** - the admin panel as a standalone product
- **AubsCraft Suite** - viewer + admin + all plugins
- Self-hosted or cloud-hosted options
- Configuration wizard: connect your server, pick features, deploy

### What this means for SpawnDev
- Proves SpawnDev.ILGPU + SpawnDev.BlazorJS in a real product
- Revenue stream from hosting/support
- Open-source core with premium features
- Community contributions improve the platform for everyone

### Technical requirements
- Configurable server connection (not hardcoded to AubsCraft)
- Plugin system for extending the viewer
- Theming/branding options
- Multi-server support (view multiple servers from one install)
- Docker deployment for easy self-hosting

---

## 8. Accessibility Features

### For all players
- **Colorblind modes** - adjust claim colors, ore highlights, player markers for different types of color vision
- **High contrast mode** - stronger outlines, brighter markers
- **Text scaling** - UI text size adjustment
- **Screen reader support** - ARIA labels on all interactive elements
- **Keyboard navigation** - full keyboard access to all menus (Tab + Enter)
- **Reduced motion** - disable animations, particle effects for motion-sensitive users

### For VR
- **One-handed mode** - all actions accessible with one controller
- **Seated mode** - optimized for wheelchair users or seated play
- **Voice-only navigation** - "fly to spawn", "show claims", "open chat" via speech commands
- **Large text** - VR text scaled up for readability
- **Comfort presets** - one-click comfort settings (snap turn, vignette, reduced speed)

---

## 9. Educational Applications

### Minecraft + Education
Many schools use Minecraft Education Edition. AubsCraft's viewer could support educational servers:

- **Geography lessons** - Build real-world landmarks, view them in the 3D viewer, take virtual tours
- **Architecture projects** - Students design buildings in creative mode, present them via the viewer
- **History recreations** - Build historical sites, the AI Historian villager teaches about them
- **Math/geometry** - Coordinate system teaching, area/volume calculations via the block counter
- **Environmental science** - Biome exploration, ecosystem building, seasonal changes

### Teacher tools
- Assignment system: "Build a representation of the water cycle"
- Grading rubric tied to build inspection (block count, materials used, area)
- Class gallery: all student builds viewable in the viewer
- Parent viewing: parents can see what their kids built (spectator mode)

---

## 10. Data Export / Integration

### Export formats
- **Screenshot** - Canvas capture as PNG at any resolution
- **Video** - Record camera paths as MP4 (MediaRecorder API on canvas stream)
- **3D model** - Export visible chunks as GLTF/GLB for 3D printing or other tools
- **Schematic** - Export regions as Minecraft schematic files
- **World download** - Export own claim as a world file (for local play)

### API for external tools
- REST API for server status, player data, world data
- WebSocket API for real-time events
- Embeddable widget: `<iframe src="map.spawndev.com/embed?x=228&z=-243">` for websites/forums
- Discord bot: `/server status`, `/player info HereticSpawn`, `/screenshot x y z`

---

*"The best way to predict the future is to invent it." - Alan Kay*

*"The best way to invent the future is to let a 10-year-old tell you what she wants and then build it." - Captain TJ, probably*
