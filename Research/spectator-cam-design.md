# Spectator Cam System - Technical Design

**Date:** 2026-04-12
**Phase:** P (Spectator Cam + Drone Cam + Streaming)
**Dependencies:** AubsCraftTracker plugin (Phase J), account linking optional for guest spectating

---

## The Vision

Aubs plays Minecraft in VR. Her friend opens map.spawndev.com on their phone, taps Aubs' name, taps "Spectate." They watch her build in real-time 3D, choosing their own camera angle. No Twitch, no stream delay, no install. Just a URL.

---

## Architecture

```
MC Server                    AubsCraft Server               Browser Viewer
  |                              |                              |
  | Player positions (100ms)     |                              |
  |--[AubsCraftTracker]--------->| WebSocket broadcast          |
  |                              |--[position data]------------>|
  |                              |                              |
  |                              |   Camera follows player      |
  |                              |   Chunks load around player  |
  |                              |   Viewer renders scene       |
```

### Data flow
- **AubsCraftTracker** plugin sends player positions to AubsCraft server every 100-200ms
- Server broadcasts positions to all connected viewers via WebSocket
- Each viewer that's spectating a player updates their camera to follow
- Chunks load dynamically around the followed player (same as free-fly, just auto-positioned)
- Each viewer renders independently - no shared rendering, no bandwidth scaling issues

### Bandwidth per spectator
- Position update: ~50 bytes (x, y, z, yaw, pitch, dimension) x 5-10/sec = ~500 bytes/sec
- That's it. The world data is already cached locally (OPFS). Only position data streams.
- 100 spectators = 50KB/sec total. Negligible.

---

## AubsCraftTracker Paper Plugin

### What it sends
```java
public class PlayerPositionData {
    UUID uuid;
    String name;
    double x, y, z;
    float yaw, pitch;
    String world;        // "world", "world_nether", "world_the_end"
    String gamemode;     // "SURVIVAL", "CREATIVE", "SPECTATOR", "ADVENTURE"
    boolean isVR;        // from VRDetect data
    boolean isSneaking;
    boolean isSprinting;
    boolean isFlying;
    double health;       // 0-20
    int foodLevel;       // 0-20
}
```

### Update frequency
- Position: every 100ms (10 updates/sec) when player is moving
- Stationary: every 1000ms (1 update/sec) when player hasn't moved
- Movement detection: compare current pos to last sent pos, threshold 0.1 blocks

### Communication
WebSocket connection from plugin to AubsCraft admin server:
```
ws://192.168.1.142:5080/api/tracker
```

Plugin connects on enable, reconnects on disconnect. Sends JSON messages:
```json
{
  "type": "positions",
  "players": [
    { "uuid": "...", "name": "HereticSpawn", "x": 228.5, "y": 64.0, "z": -243.2, 
      "yaw": 45.0, "pitch": -10.0, "world": "world", "gamemode": "SURVIVAL",
      "isVR": false, "health": 20.0, "food": 18 }
  ]
}
```

Also sends events:
```json
{ "type": "join", "player": { "uuid": "...", "name": "HereticSpawn" } }
{ "type": "leave", "player": { "uuid": "...", "name": "HereticSpawn" } }
{ "type": "death", "player": { "uuid": "...", "name": "HereticSpawn" }, "message": "fell from a high place" }
{ "type": "dimension", "player": { "uuid": "...", "name": "HereticSpawn" }, "world": "world_nether" }
```

---

## Server-Side: Position Broadcast

### WebSocket endpoint
```
ws://map.spawndev.com:44365/api/positions
```

Server receives positions from the tracker plugin and broadcasts to all connected viewer clients. Lightweight relay - no processing, just forward.

### Client subscription
Viewer connects to `/api/positions` WebSocket on page load. Receives all player positions. The viewer filters locally to show only relevant players (spectated player, nearby players, etc.).

### Privacy controls
Server-side filter: if a player has set `spectateEnabled: false` in their profile, their position data is NOT broadcast to non-admin viewers. Admins always see all positions.

```
GET /api/players/spectate-settings
{ "HereticSpawn": { "spectatable": true }, "SpudArt": { "spectatable": false } }
```

---

## Client-Side: Spectator Camera

### Entering spectate mode
1. Player list shows online players with platform badge (Java/Bedrock/VR)
2. Each player has a "Spectate" button (grayed out if spectating is disabled)
3. Click "Spectate" -> camera smoothly flies to player position over 1-2 seconds
4. Camera locks to follow mode

### Camera follow modes

**Third Person (default)**
```
Camera position = player position + offset
  offset = (-sin(yaw) * distance, height, -cos(yaw) * distance)
  distance: 8 blocks behind (configurable 4-20)
  height: 4 blocks above (configurable 2-10)
```
Camera looks at player position. Smoothly interpolates both position and look-at.

**Overhead**
```
Camera position = player position + (0, 30, 0)
Camera looks straight down at player
Good for: watching base building from above
```

**Orbit**
```
Camera orbits around player at configurable radius
  angle increments slowly (0.5 deg/frame)
  height: 5 blocks above player
Good for: cinematic showcase, AFK streaming
```

**First Person (admin only)**
```
Camera position = player position + (0, 1.6, 0)  // eye height
Camera direction = player yaw/pitch
Sees exactly what the player sees
Privacy concern: admin-only feature
```

**Free Detach**
```
While spectating, press Space to detach camera
Camera freezes at current position, player continues moving
Useful for: watching a player approach a location
Press Escape to re-attach or click "Spectate" again
```

### Smooth interpolation
Position updates arrive at 5-10 Hz. The camera must interpolate smoothly at 60 Hz.

```csharp
// ILGPU kernel for position interpolation? Or just CPU lerp for 1 position
// For a single player follow, CPU lerp is appropriate
// For rendering 20+ player markers with smooth movement, ILGPU

Vector3 currentPos;  // where the camera currently is
Vector3 targetPos;   // where the player's latest position puts the camera

void UpdateSpectatorCamera(float dt)
{
    // Exponential smoothing - responsive but smooth
    float smoothing = 1f - MathF.Pow(0.001f, dt);  // ~0.93 per frame at 60fps
    currentPos = Vector3.Lerp(currentPos, targetPos, smoothing);
    
    // Same for look direction
    currentYaw = LerpAngle(currentYaw, targetYaw, smoothing);
    currentPitch = Lerp(currentPitch, targetPitch, smoothing);
}
```

### Chunk loading around spectated player
When spectating, the camera position follows the player. The existing `LoadFullChunksNearbyAsync` already loads 3D chunks around the camera. So as the spectated player moves, new chunks automatically load ahead of them.

For fast-moving players (flying, using elytra), increase the look-ahead distance:
```csharp
// If player speed > 20 blocks/sec, load chunks further ahead
float speed = Vector3.Distance(lastPos, currentPos) / dt;
int lookAhead = speed > 20f ? 5 : 3;  // chunks
```

---

## Player Markers (all online players)

Even when not spectating, show all online players on the map.

### Current: cyan beacon pillars
Simple colored columns at player positions. Works but basic.

### Upgrade: Player skin heads
- Download player skin from Mojang API: `https://sessionserver.mojang.com/session/minecraft/profile/{uuid}`
- Extract the 8x8 face texture from the 64x64 skin image
- Render as a small textured quad billboard at player position
- Billboard always faces the camera (standard billboard technique)
- Name label below the head

### ILGPU approach for player markers
If 20+ players are online, compute all marker positions on GPU:
- Input: player positions array
- Compute: billboard vertices (4 verts per player, facing camera)
- Output: vertex buffer for all markers
- Render in one draw call

### Player marker interactions
- Hover: show name + health bar + platform badge
- Click: open player panel (stats, claims, spectate button)
- Right-click (admin): kick, ban, TP, message context menu

---

## Streaming Integration

### For OBS/Twitch
```
URL: map.spawndev.com:44365/map?spectate=HereticSpawn&stream=true
```
- Auto-enters spectate mode on the specified player
- Stream mode: no UI chrome, clean canvas
- Optional overlays via URL params: `&overlay=name,coords,health`

### For direct browser viewers
```
URL: map.spawndev.com:44365/map?spectate=HereticSpawn
```
- Opens normally with UI, auto-spectates
- Viewer can change camera mode, detach, switch to free-fly
- Shareable link: "Watch me play!"

### For WebRTC peer sharing
One viewer runs spectator cam -> captureStream -> WebRTC -> other viewers watch
- The "broadcaster" bears the rendering cost
- "Watchers" get a video stream, no rendering needed
- Uses SpawnDev.RTLink for signaling
- Good for: sharing to many viewers without each needing to render

---

## Privacy Design

### Player controls (via /profile command or web settings)
```
Spectate: [Everyone] [Friends Only] [Nobody]
Position visible: [Everyone] [Friends Only] [Nobody]
```

### Default: Nobody (opt-in)
New players are NOT spectatable by default. They must opt in.

### Friend list
Players can add friends via the web UI. Only friends can spectate if set to "Friends Only."

### Admin override
Admins (Level 3+) can always see positions and spectate any player. This is needed for moderation (investigating grief reports, watching suspicious players).

### Invisible mode
Admins can go "invisible" - their position is not broadcast. They can spectate without appearing in the player list. Useful for covert moderation.

---

## Implementation Priority

1. **AubsCraftTracker plugin** - position broadcasting (required for everything)
2. **Player markers on map** - show all players as simple markers first
3. **Basic spectate** - third-person follow with smooth interpolation
4. **Camera modes** - orbit, overhead, detach
5. **URL spectate** - `?spectate=PlayerName` for sharing
6. **Player skin heads** - download and render skin faces
7. **Stream mode** - clean canvas, OBS-friendly
8. **Privacy controls** - opt-in, friend lists
9. **WebRTC sharing** - peer-to-peer video of spectator view
