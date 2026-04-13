# Viewer Presence in Minecraft - Technical Design

**Date:** 2026-04-12
**Concept:** Web viewers are visible IN-GAME as flying entities (drones, birds, etc.)
**Dependencies:** AubsCraftTracker plugin (camera positions), account linking (for identity)

---

## The Vision

Aubs is building in Minecraft. She looks up and sees a glowing Allay hovering nearby - that's her dad TJ watching from the web viewer on his phone at work. She waves (crouches), the Allay dips in response. She knows she's not building alone.

---

## How It Works

```
Web Viewer (browser)          AubsCraft Server           MC Server
  |                                |                        |
  | Camera position (5 Hz)         |                        |
  |--[WebSocket]--pos update------>|                        |
  |                                |--[WebSocket]---------->|
  |                                |   AubsCraftPresence    |
  |                                |   plugin spawns/moves  |
  |                                |   entity at camera pos |
  |                                |                        |
  |                                |                        | Players see
  |                                |                        | the entity
  |                                |                        | flying around
```

### Data flow
- Web viewer already sends camera position updates (for chunk loading priority)
- Server relays camera positions to the AubsCraftPresence plugin
- Plugin spawns a Minecraft entity at the camera position
- Entity moves smoothly as camera position updates arrive
- Entity is visible to all in-game players

---

## Entity Types (User Selectable)

Players choose their viewer avatar from their web profile settings.

| Entity | Look | Best For | Notes |
|--------|------|----------|-------|
| **Allay** | Tiny, glowing, blue, flying | Default, cute | Aubs' favorite mob |
| **Parrot** | Colorful bird, perches | Bird watchers | Multiple color variants |
| **Bat** | Dark, small, fluttery | Stealth viewing | Naturally flies |
| **Phantom** | Translucent, ghostly | Spooky aesthetic | Large, dramatic |
| **Bee** | Cute, buzzy, small | Fun, lighthearted | Iconic |
| **Vex** | Tiny ghost with sword | Aggressive look | Fits "spectator" theme |
| **Armor Stand** | Custom head texture | Custom drone model | Most flexible |
| **Invisible + Particles** | Just particle effects | Minimal presence | Subtle, non-intrusive |

### Custom drone model (Armor Stand approach)
- Invisible armor stand with a custom player head
- Head texture: a "drone" skin (propeller on top, camera lens on front)
- Upload custom head textures to a skin server
- Rotate the head to match camera yaw
- Add particle effects: small smoke particles from "propellers"

### Resource pack option (future)
- Custom resource pack with a drone 3D model
- Players who accept the resource pack see a detailed drone
- Players without the pack see the fallback entity (Allay/Parrot)

---

## AubsCraftPresence Paper Plugin

### Entity management
```java
public class ViewerEntity {
    UUID viewerAccountId;       // web account
    String viewerName;          // display name
    String mcLinkedName;        // linked MC name (if any)
    EntityType entityType;      // ALLAY, PARROT, BAT, etc.
    Entity entity;              // the spawned MC entity
    Location currentTarget;     // where to move to
    boolean isActive;           // currently viewing?
}

// Spawn viewer entity when web viewer connects
public void onViewerConnect(String viewerName, EntityType type, Location initialPos) {
    Entity entity = world.spawnEntity(initialPos, type);
    entity.setCustomName(Component.text("[Web] " + viewerName)
        .color(NamedTextColor.AQUA));
    entity.setCustomNameVisible(true);
    entity.setInvulnerable(true);
    entity.setGravity(false);
    entity.setSilent(true);
    
    // For Allay: make it glow
    if (entity instanceof Allay allay) {
        allay.setGlowing(true);
    }
    
    // Store for position updates
    viewerEntities.put(viewerName, new ViewerEntity(entity, ...));
}

// Move entity smoothly toward camera position
// Called on a scheduler at 10 Hz
public void updatePositions() {
    for (var viewer : viewerEntities.values()) {
        if (!viewer.isActive) continue;
        
        Location target = viewer.currentTarget;
        Location current = viewer.entity.getLocation();
        
        // Smooth interpolation (don't teleport, move gradually)
        double speed = 2.0; // blocks per tick
        Vector direction = target.toVector().subtract(current.toVector());
        double distance = direction.length();
        
        if (distance > 0.5) {
            direction.normalize().multiply(Math.min(speed, distance));
            Location newLoc = current.add(direction);
            newLoc.setYaw((float) viewer.targetYaw);
            newLoc.setPitch((float) viewer.targetPitch);
            viewer.entity.teleport(newLoc);
        }
    }
}

// Remove entity when viewer disconnects
public void onViewerDisconnect(String viewerName) {
    var viewer = viewerEntities.remove(viewerName);
    if (viewer != null && viewer.entity != null) {
        viewer.entity.remove();
    }
}
```

### Entity behaviors
- **No collision** - entities don't push players or block movement
- **No sound** - entities are silent (setSilent)
- **Invulnerable** - can't be killed by players
- **No AI** - custom movement only, no pathfinding
- **Custom name tag** - shows `[Web] PlayerName` in aqua above the entity
- **Glowing** - optional glow effect so they're visible at distance
- **Despawn on disconnect** - entity removed when viewer closes the browser

### Interaction from in-game
- **Right-click the entity** - shows viewer info (name, platform, how long viewing)
- **Crouch near entity** - the entity "nods" (animation) as acknowledgment
- **Chat mention** - if you type `@WebViewerName`, the message is forwarded to their web chat

---

## Web Viewer Side

### Camera position broadcast
The web viewer already sends camera position for chunk loading. Extend with:
```json
{
    "x": 228.5, "y": 72.0, "z": -243.2,
    "yaw": 45.0, "pitch": -10.0,
    "viewerName": "HereticSpawn",
    "entityType": "allay",
    "visible": true
}
```

### Visibility toggle
Web viewers can toggle their in-game visibility:
- "Show me in-game" checkbox in settings
- When off, no entity is spawned (pure ghost mode)
- Default: visible (opt-out, not opt-in - encourages social interaction)

### Entity type selector
Settings page with entity previews:
```
Your in-game appearance:
[Allay]  [Parrot]  [Bat]  [Bee]  [Phantom]  [Drone]
   ^selected
Preview: [image of selected entity]
```

---

## Social Features

### Wave/Emote system
Web viewers can send simple emotes visible in-game:

| Web Action | In-Game Effect |
|-----------|---------------|
| Click "Wave" button | Entity bobs up and down rapidly |
| Click "Heart" button | Heart particles spawn around entity |
| Click "Celebrate" button | Firework particles around entity |
| Type a message | Message appears as floating text above entity briefly |

### In-game player interactions with viewer entities
| In-Game Action | Web Viewer Effect |
|---------------|-------------------|
| Crouch near entity | Notification: "SpudArt waved at you!" |
| Hit entity (no damage) | Notification: "SpudArt poked you!" |
| Give item to Allay | Notification: "SpudArt gave you a flower!" |
| Chat @viewerName | Message appears in web chat |

### Proximity awareness
When a web viewer's entity is near an in-game player:
- In-game: player sees the entity and name tag
- Web viewer: notification "You're near SpudArt!" with option to open chat
- Distance threshold: 16 blocks (1 chunk)

---

## VR Viewer Presence

VR viewers get a distinct entity type:
- Default: **Phantom** (ethereal, ghostly, large enough to notice)
- Or: custom armor stand with VR headset skin
- Particle effect: small purple portal particles (like enderman)
- The VR viewer's head rotation maps to the entity's head rotation
- Hand controller positions could map to entity arm positions (ambitious but possible)

---

## Performance Considerations

### Server-side (MC)
- Entity count: 1-5 web viewers typical, rarely more than 10
- Position updates: 10 Hz scheduler (every 2 ticks)
- Entity teleport is cheap (no physics, no pathfinding)
- Custom name rendering is client-side (MC handles it)
- Impact: negligible for <20 viewer entities

### Network
- Camera position updates: already being sent for chunk loading
- Added data: entity type + visibility flag (~10 bytes per update)
- No additional bandwidth for the viewer entities themselves (MC server handles rendering to game clients)

### ILGPU involvement
- None needed for this feature - it's purely MC server-side entity management
- The web viewer already sends camera positions via WebSocket
- The plugin just spawns and moves standard MC entities

---

## Implementation Priority

1. **AubsCraftPresence plugin** - spawn/move/remove entities from camera positions
2. **Server relay** - forward web camera positions to the plugin (WebSocket)
3. **Basic entity** - Allay with name tag, smooth movement
4. **Entity type selector** - web settings page
5. **Visibility toggle** - opt-out ghost mode
6. **Emote system** - wave/heart/celebrate
7. **In-game interaction** - right-click for info, crouch to wave
8. **VR presence** - distinct VR entity type
9. **Chat bridge** - @mention from game to web
10. **Resource pack drone** - custom 3D model for the drone entity

---

## Bidirectional Voice + Text Chat

### The concept
Walk up to a web viewer's entity (Allay/drone) in-game and TALK to them. They hear you through their browser. They talk back and you hear them through Minecraft. Like proximity voice chat but between the game client and a web browser.

### Text chat (simpler, do first)
- **Game -> Web:** Player types near the entity, message appears in web viewer's chat panel
  - Proximity-gated: only messages from players within 16 blocks of the entity
  - Or: direct @mention anywhere on the server
- **Web -> Game:** Web viewer types, message appears as floating text above their entity AND in game chat with [Web] prefix
  - Already covered by AubsCraftChat bridge
  - Entity-specific: the entity "speaks" the message (particles + floating text)

### Voice chat (advanced, WebRTC)
- **Web viewer's microphone -> in-game audio:**
  - Web viewer activates mic (Web Speech API or raw MediaStream)
  - Audio stream sent via WebRTC to the server (or directly to nearby players' clients)
  - Problem: vanilla MC has no voice chat. Need a mod or workaround.
  - **Solution A: Simple Voice Chat mod** (popular MC mod with proximity voice)
    - Server installs Simple Voice Chat plugin
    - AubsCraftPresence injects web viewer's audio into the SVC system
    - In-game players with SVC hear the web viewer through proximity
  - **Solution B: Web-only voice (no MC mod needed)**
    - Web viewers can voice chat with OTHER web viewers via WebRTC
    - In-game players see text transcription above the entity (speech-to-text)
    - Not true voice to game, but still useful
  - **Solution C: Discord integration**
    - Server has a Discord voice channel
    - Web viewers join via browser WebRTC to the Discord channel
    - In-game players join the same Discord channel
    - AubsCraftPresence shows who's in voice chat with a speaker icon above their entity

### Recommended approach
Start with **text chat** (Phase K already covers this). Then add **voice between web viewers** via WebRTC (SpawnDev.RTLink). For game-to-web voice, **Discord integration** is the most practical since it doesn't require a Minecraft mod.

### Proximity-based features
- **Distance-based text:** Only see messages from players/viewers within 32 blocks
- **Distance-based voice volume:** Audio fades with distance (WebRTC + Web Audio API gain node)
- **Directional audio in VR:** In VR mode, voice comes from the spatial position of the speaker (Web Audio API PannerNode positioned at the entity/player location)

---

## Game Sounds + Animations in the Viewer

### Spatial game audio
Minecraft sound files are in the client jar at `assets/minecraft/sounds/`. We can extract and play them positionally in the web viewer using the Web Audio API.

**Sound categories:**
| Category | Examples | Trigger |
|----------|---------|---------|
| Ambient | Wind, cave drips, birds, underwater | Based on biome + camera position |
| Block | Place, break, step sounds per material | From CoreProtect live feed or tracker events |
| Mob | Cow moo, pig oink, zombie groan | From entity data near camera |
| Weather | Rain, thunder | From server weather state |
| Music | MC background music (C418) | Jukebox blocks or background ambiance |
| Player | Footsteps, damage, eat, drink | From tracked player actions |

**Web Audio API integration:**
```javascript
// Create positional audio source for a block break sound
const audioCtx = new AudioContext();
const panner = audioCtx.createPanner();
panner.panningModel = 'HRTF';  // 3D spatial model
panner.setPosition(blockX, blockY, blockZ);  // world position

// Load and play the sound
const buffer = await loadSound('sounds/block/stone/break1.ogg');
const source = audioCtx.createBufferSource();
source.buffer = buffer;
source.connect(panner).connect(audioCtx.destination);
source.start();
```

**Listener position:** Camera position = audio listener position. As the camera moves, all spatial sounds update naturally.

**In VR:** WebXR provides native spatial audio integration. Sounds come from the correct direction relative to your head. Walking past a waterfall, you hear it pan from front to side to behind.

### Sound asset management
- Extract sounds from MC client jar: `assets/minecraft/sounds/`
- Organize by category in our `wwwroot/sounds/` folder
- Load on demand (don't preload everything)
- Cache in OPFS alongside world data
- Compress to Opus format for smaller file sizes (Web Audio supports Opus)
- Total: MC has ~2000 sound files, but we only need ~100 common ones initially

### Block animations
**Block break animation:**
- When CoreProtect reports a block break in real-time, show cracking particles
- ILGPU kernel generates break particle vertices (small colored quads flying outward)
- Particles fade and fall with gravity over 0.5 seconds
- Color matches the broken block's texture

**Block place animation:**
- Block appears with a subtle scale-up animation (0.8 -> 1.0 over 0.2s)
- Small "poof" particle effect
- Can be done in the shader: vertex scale uniform that animates from 0.8 to 1.0

**Water animation:**
- UV scrolling in the water shader: offset water UVs by time
- Creates the appearance of flowing water
- Very cheap: just add `uniforms.time` to the UV in `fs_water`
- `let animated_uv = input.tex_uv + vec2<f32>(uniforms.time * 0.02, uniforms.time * 0.01);`

**Leaf sway:**
- Cross-quad plants can sway gently in wind
- Vertex displacement in the shader based on `sin(time + position.x)`
- Only applies to plant vertices (flag = 1 in blockFlags)
- Very subtle, very immersive

**Day/night cycle:**
- Sun/moon position animated with server time
- Sky color lerps between day (blue) and night (dark blue/black)
- Star rendering at night (point sprites or small quads)
- Already planned in Phase E1/E2

### Live event visualization
When the tracker plugin reports player actions:
| Event | Visual | Sound |
|-------|--------|-------|
| Block break | Crack particles at location | Stone/wood break sound |
| Block place | Scale-up animation | Block place sound |
| Player damage | Red flash on player marker | Hurt sound |
| Player death | Skull particle burst | Death sound |
| Explosion | Expanding particle sphere | Explosion sound |
| Lightning | Flash + bolt render | Thunder sound |

### Performance budget for audio/animation
- Web Audio API runs off the main thread (browser handles audio processing)
- ILGPU kernel for particle generation (break/place effects)
- Particle count limit: max 100 active particle systems
- Sound limit: max 16 simultaneous spatial sounds
- Animation uniforms: one `time` float added to the uniform buffer (already have room)

---

## Plugin Summary

| Plugin | Added Responsibility |
|--------|---------------------|
| **AubsCraftPresence** (NEW) | Spawn/move viewer entities, emote handling |
| AubsCraftTracker | Already sends player positions; receives viewer positions |
| AubsCraftChat | Already bridges chat; add @mention forwarding |

Could potentially combine AubsCraftPresence into AubsCraftTracker since they share the position WebSocket connection. The tracker already connects to the server - just add incoming viewer position handling.
