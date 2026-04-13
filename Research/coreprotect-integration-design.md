# CoreProtect Integration - Technical Design

**Date:** 2026-04-12
**Phase:** S (Data Analytics), supports Phase 4 (In-Viewer Admin Tools)
**Data source:** `M:\opt\minecraft\server\plugins\CoreProtect\database.db` (SQLite)

---

## What CoreProtect Tracks

CoreProtect records EVERY block change on the server:
- Block placement (who, what block, where, when)
- Block destruction (who, what was there, where, when)
- Container access (who opened what chest/furnace/etc, when)
- Entity kills (who killed what mob/animal)
- Chat messages (who said what, when)
- Command usage
- Player sessions (login/logout with location)
- Rollback history

---

## Database Schema (SQLite)

Location: `M:\opt\minecraft\server\plugins\CoreProtect\database.db`

Key tables:
```sql
-- Block changes
co_block (
    id INTEGER PRIMARY KEY,
    time INTEGER,        -- Unix timestamp
    user INTEGER,        -- FK to co_user
    wid INTEGER,         -- FK to co_world  
    x INTEGER,
    y INTEGER,
    z INTEGER,
    type INTEGER,        -- block type ID
    data INTEGER,        -- block data/state
    meta BLOB,           -- NBT data (optional)
    action INTEGER,      -- 0=break, 1=place, 2=click, 3=kill
    rolled_back INTEGER  -- 0 or 1
)

-- Users
co_user (
    id INTEGER PRIMARY KEY,
    user TEXT             -- player name or "#entity" for non-players
)

-- Worlds  
co_world (
    id INTEGER PRIMARY KEY,
    world TEXT            -- "world", "world_nether", "world_the_end"
)

-- Container access
co_container (
    id INTEGER PRIMARY KEY,
    time INTEGER,
    user INTEGER,
    wid INTEGER,
    x INTEGER, y INTEGER, z INTEGER,
    type INTEGER,        -- item type
    data INTEGER,
    amount INTEGER,      -- positive = added, negative = removed
    metadata BLOB,
    action INTEGER,
    rolled_back INTEGER
)

-- Chat and commands
co_chat (
    id INTEGER PRIMARY KEY,
    time INTEGER,
    user INTEGER,
    wid INTEGER,
    x INTEGER, y INTEGER, z INTEGER,
    message TEXT
)

-- Sessions
co_session (
    id INTEGER PRIMARY KEY,
    time INTEGER,
    user INTEGER,
    wid INTEGER,
    x INTEGER, y INTEGER, z INTEGER,
    action INTEGER       -- 0=login, 1=logout
)
```

---

## Server-Side: CoreProtect Query Service

### Read-only SQLite access
```csharp
public class CoreProtectService
{
    private readonly string _dbPath;
    
    public CoreProtectService(IConfiguration config)
    {
        var serverPath = config["Minecraft:ServerPath"] ?? "/opt/minecraft/server";
        _dbPath = Path.Combine(serverPath, "plugins", "CoreProtect", "database.db");
    }
    
    // Block history for a specific location
    public async Task<List<BlockChange>> GetBlockHistoryAsync(
        int x, int y, int z, int radius = 0, int limit = 50)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        await conn.OpenAsync();
        
        var sql = @"
            SELECT b.time, u.user, b.x, b.y, b.z, b.type, b.action, b.rolled_back
            FROM co_block b
            JOIN co_user u ON b.user = u.id
            WHERE b.x BETWEEN @x1 AND @x2
              AND b.y BETWEEN @y1 AND @y2
              AND b.z BETWEEN @z1 AND @z2
            ORDER BY b.time DESC
            LIMIT @limit";
        
        // ... execute and return results
    }
    
    // Recent activity by player
    public async Task<List<BlockChange>> GetPlayerActivityAsync(
        string playerName, int hours = 24, int limit = 100)
    {
        // Query co_block JOIN co_user WHERE user = playerName
        // AND time > (now - hours*3600)
    }
    
    // Activity in a region (for grief detection)
    public async Task<List<BlockChange>> GetRegionActivityAsync(
        int x1, int z1, int x2, int z2, int hours = 24)
    {
        // Query co_block for all changes in the XZ region
    }
    
    // Player session history
    public async Task<List<SessionEvent>> GetSessionHistoryAsync(
        string playerName, int days = 30)
    {
        // Query co_session for login/logout events
    }
    
    // Block change statistics
    public async Task<PlayerStats> GetPlayerStatsAsync(string playerName)
    {
        // Aggregate: blocks placed, blocks broken, containers accessed
        // Grouped by time period (today, this week, all time)
    }
}
```

### API endpoints
```
GET /api/coreprotect/block/{x}/{y}/{z}?radius=0&limit=50
GET /api/coreprotect/player/{name}?hours=24&limit=100
GET /api/coreprotect/region/{x1}/{z1}/{x2}/{z2}?hours=24
GET /api/coreprotect/stats/{name}
GET /api/coreprotect/sessions/{name}?days=30
```

### Performance considerations
- SQLite read-only mode (no locking with the MC server's writes)
- WAL mode should already be enabled by CoreProtect
- Index on (x, z, time) is crucial for spatial queries - check if CoreProtect creates it
- Cache frequent queries (player stats, recent activity) server-side
- Large region queries: paginate, don't load everything at once

---

## Client-Side: In-Viewer Block Inspector

### Click any block to inspect
1. User enables "Inspect Mode" (click toolbar button or press I key)
2. Cursor changes to magnifying glass
3. Click a block in the 3D viewer
4. Raycast determines which block was clicked (coordinates)
5. API call: `GET /api/coreprotect/block/{x}/{y}/{z}`
6. History panel slides in showing who placed/broke this block and when

### Block history panel
```
+----------------------------------+
| Block Inspector                  |
| Stone Bricks at (228, 64, -243)  |
|                                  |
| History:                         |
| [14:23] HereticSpawn placed      |
| [14:20] HereticSpawn broke dirt  |
| [12:05] SpudArt placed dirt      |
| [Mar 15] HereticSpawn placed     |
|          grass_block             |
|                                  |
| [Rollback] [Copy coords]        |
+----------------------------------+
```

### Area inspector
1. User selects two corners in the 3D viewer (click + click)
2. Selected region highlighted with a wireframe box
3. API call for the entire region
4. Shows aggregate: who changed what, most active players
5. Rollback option for the entire region

---

## Grief Detection Dashboard

### Automated alerts
Monitor CoreProtect data for suspicious patterns:

```csharp
public class GriefDetectionService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await CheckForGriefPatterns();
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }
    
    async Task CheckForGriefPatterns()
    {
        // Pattern 1: Large-scale destruction near someone else's claim
        // Query: blocks broken in last 5 min, near claims not owned by the breaker
        
        // Pattern 2: Lava/water placement near claims
        // Query: lava_bucket or water_bucket placed near claims not owned by placer
        
        // Pattern 3: Unusual destruction rate
        // Query: player who broke > 100 blocks in 5 minutes
        
        // Pattern 4: Container theft
        // Query: container access inside claims by non-trusted players
    }
}
```

### Alert types
| Alert | Trigger | Severity |
|-------|---------|----------|
| Mass destruction | >100 blocks broken in 5 min near a claim | HIGH |
| Lava/water grief | Fluid placed within 10 blocks of a claim | HIGH |
| Container theft | Non-trusted access inside a claim | MEDIUM |
| Rapid building | >500 blocks placed in 5 min (possible bot) | LOW |
| Unusual login | Player logging in from new location pattern | LOW |

### Admin notification
- Browser notification (if admin has the site open)
- In-viewer alert overlay with "Fly to" button
- Activity log entry with full details
- Optional: Discord webhook for when admin isn't on the site

---

## Time-Lapse Visualization

### CoreProtect data enables world time-lapse
1. Query all block changes in a region over a time period
2. Replay changes in order, animating the 3D viewer
3. Watch a build come together or watch grief happen in fast-forward
4. Playback controls: play, pause, speed (1x, 10x, 100x), scrub timeline

### Implementation
- Query CoreProtect for all changes in a region between two timestamps
- Sort by time
- Group into frames (e.g., 1 frame = 1 minute of real time)
- For each frame, apply block changes to the local chunk data
- Re-mesh and render

### Performance
- This is a replay, not real-time - can pre-compute all frames
- ILGPU kernel generates mesh for each frame
- Vertex buffer swaps per frame (pre-computed meshes)
- Most frames change few blocks - incremental mesh update, not full re-mesh

---

## Data Volume Estimates

For an active server with 3 players:
- ~1000-5000 block changes per player per hour
- ~3000-15000 rows per hour in co_block
- ~100K rows per day
- ~3M rows per month
- SQLite handles this easily (indexed queries return in <10ms)

For the database file:
- ~50-100MB per month of play
- CoreProtect has built-in cleanup (configurable retention, default 30 days)

---

## Implementation Priority

1. **CoreProtectService** - read-only SQLite access, basic queries
2. **Block history API** - endpoint for single-block history
3. **In-viewer inspector** - click block, see history
4. **Player activity query** - what did player X do recently
5. **Grief detection** - automated pattern monitoring
6. **Region inspector** - area selection with aggregate history
7. **Time-lapse** - replay block changes in the viewer
8. **Analytics dashboard** - player stats, building trends, activity heatmaps
