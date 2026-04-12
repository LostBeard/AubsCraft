# GriefPrevention Claim Visualization - Technical Design

**Date:** 2026-04-12
**Phase:** I (Golden Shovel Claim Viewer)
**Dependencies:** AubsCraftClaims plugin, account linking (Phase H) for "my claims" feature

---

## What Players See

### On the 3D map
- Semi-transparent colored volumes marking claim boundaries
- Each player's claims get a unique color (generated from UUID hash)
- Claims extend from bedrock (Y=-64) to build limit (Y=320) as tall colored columns
- Claim boundaries visible at any zoom level
- Toggle: show all / show mine / hide claims

### On click
- Claim info panel slides in from the right
- Shows: owner name + skin head, claim size (area in blocks), creation date
- Trust list: who can build here
- Last activity: when was the owner last online
- "Fly to claim" button for admins/other players (if claim is public)

### Claim colors
- Generated deterministically from player UUID so they're consistent across sessions
- Saturated, distinct colors - avoid similar colors for adjacent claims
- Owner's own claims highlighted with a brighter border or pulsing effect
- Expired/abandoned claims shown in red (admin view only)

---

## Data Source: GriefPrevention

### What GP stores
- Claim ID (auto-incrementing)
- Owner UUID
- Lesser boundary corner (Location: world, x, y, z)
- Greater boundary corner (Location: world, x, y, z)
- Parent claim ID (for subdivisions)
- Trust lists: builders, containers, accessors, managers

### How to access it

**Option A: AubsCraftClaims plugin wrapping GP API (RECOMMENDED)**

```java
// In AubsCraftClaims plugin
GriefPrevention gp = GriefPrevention.instance;

// Get all claims
DataStore dataStore = gp.dataStore;
// DataStore doesn't expose a getAllClaims() easily, but we can iterate chunks

// Get claims for a specific player
PlayerData playerData = dataStore.getPlayerData(uuid);
// playerData.getClaims() returns their claims

// Get claim at a location
Claim claim = dataStore.getClaimAt(location, false, null);

// Claim properties
claim.getID()                    // long
claim.getOwnerID()               // UUID (null for admin claims)
claim.getLesserBoundaryCorner()   // Location
claim.getGreaterBoundaryCorner()  // Location
claim.getArea()                  // int (square blocks)
claim.parent                     // Claim (null if top-level)
claim.children                   // ArrayList<Claim> (subdivisions)

// Trust lists
ArrayList<String> builders = new ArrayList<>();
ArrayList<String> containers = new ArrayList<>();
ArrayList<String> accessors = new ArrayList<>();
ArrayList<String> managers = new ArrayList<>();
claim.getPermissions(builders, containers, accessors, managers);
```

**Option B: RCON commands**
- `claimslist <player>` - returns text list of claim locations
- Parsing RCON text output is fragile - plugin API is better

**Option C: Direct file/DB access**
- GP stores data in `plugins/GriefPreventionData/` but the internal format isn't documented
- Not recommended - could break on GP updates

### Plugin API endpoint

```
GET /api/claims              - All claims (admin) or own claims (player)
GET /api/claims/{playerId}   - Claims for a specific player (admin)
GET /api/claims/at/{x}/{z}   - Claim at a specific location

Response format:
{
  "claims": [
    {
      "id": 1,
      "owner": { "uuid": "51284fe7-...", "name": "HereticSpawn" },
      "lesser": { "x": 100, "z": -200 },
      "greater": { "x": 150, "z": -150 },
      "area": 2500,
      "children": [...],
      "trust": {
        "builders": ["SpudArt"],
        "containers": [],
        "accessors": ["public"],
        "managers": []
      }
    }
  ]
}
```

### Real-time events (WebSocket from plugin)
```json
{ "event": "claim_created", "claim": {...} }
{ "event": "claim_resized", "claim": {...} }
{ "event": "claim_deleted", "claimId": 1 }
{ "event": "trust_changed", "claimId": 1, "trust": {...} }
```

---

## Rendering Claims in the 3D Viewer

### Geometry
Each claim is a rectangular prism (box) from Y=-64 to Y=320:
- 4 vertical wall faces (transparent)
- Optional top/bottom faces (can skip for performance)
- Rendered in the transparent pass (after opaque, before/after water)

### ILGPU kernel for claim geometry
Claims are simple boxes - a kernel generates the 4-wall vertex data:
- Input: array of claim bounds (x1, z1, x2, z2 per claim)
- Output: vertex buffer with colored transparent quads
- Color per claim from UUID hash
- Alpha: 0.15 for fill, 0.6 for edges/borders

### Vertex format
Same 11-float format as blocks. Color carries the claim color, UV set to -1 (no texture, flat color).

### Rendering order
1. Opaque blocks (existing)
2. Claim boundaries (new transparent pass, depth-write off)
3. Water (existing transparent pass)

Claims render BEFORE water so underwater claims are visible through water.

### Edge highlighting
Claim borders (the 4 vertical edges where walls meet) rendered as slightly thicker/brighter lines. Two approaches:
- Emit thin quads along the edges (1-block wide, brighter alpha)
- Or use a line primitive (if WebGPU supports line topology for this)

### Performance
- Claims are MUCH simpler than terrain - just 4 quads per claim
- With 50 claims on the server, that's 200 quads = 1200 vertices = negligible
- Update only when claims change (WebSocket events), not every frame
- Separate vertex buffer for claims, uploaded once, drawn every frame

---

## UI Components

### Claim list sidebar panel
```
Claims (23 total)
[Show All] [Show Mine] [Hide]

HereticSpawn (3 claims)
  [Fly] Main Base - 2500 blocks
  [Fly] Farm - 400 blocks  
  [Fly] Nether Portal - 100 blocks

SpudArt (2 claims)
  [Fly] House - 600 blocks
  [Fly] Mine Entrance - 200 blocks

.Noob607 (1 claim)
  [Fly] Starter Base - 100 blocks
```

### Claim detail panel (on click)
```
+----------------------------------+
| [Head] HereticSpawn's Claim      |
| Main Base                        |
|                                  |
| Size: 50x50 (2500 blocks)       |
| Location: 100, -200 to 150, -150|
| Created: 2026-03-15              |
| Last Active: 2 hours ago         |
|                                  |
| Trust:                           |
|   Builders: SpudArt              |
|   Containers: (none)             |
|   Access: public                 |
|                                  |
| [Admin: Delete] [Admin: Resize]  |
+----------------------------------+
```

### Map integration
- Claims panel can be toggled from the sidebar or with a keyboard shortcut (G for "Golden Shovel"?)
- Clicking a claim in the list flies the camera there
- Clicking a claim boundary in the 3D view opens the detail panel
- Hovering over a claim shows owner name as a floating label

---

## Claim Colors Algorithm

Generate a visually distinct color per player from their UUID:

```csharp
static (float r, float g, float b) GetClaimColor(string uuid)
{
    // Hash UUID to get a deterministic hue
    int hash = uuid.GetHashCode();
    float hue = (hash & 0x7FFFFFFF) % 360 / 360f;
    
    // Use HSL with high saturation, medium lightness for visibility
    float saturation = 0.7f;
    float lightness = 0.5f;
    
    // HSL to RGB conversion
    return HslToRgb(hue, saturation, lightness);
}
```

For the current 3 players:
- HereticSpawn: unique color A
- SpudArt: unique color B  
- .Noob607: unique color C

Colors should be cached per session and consistent across all views.
