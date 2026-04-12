# Administrator Tools - Comprehensive Plan

**Date:** 2026-04-12
**Status:** Planning

---

## Admin Roles

| Level | Role | Capabilities |
|-------|------|-------------|
| 0 | Guest | View map (if public), spectate (if player allows) |
| 1 | Player | View map, own claims, bookmarks, chat, profile |
| 2 | Moderator | Player tools + kick, mute, teleport, view claims, CoreProtect lookups |
| 3 | Admin | Moderator tools + ban, whitelist, server restart, plugin manage, world edit anywhere |
| 4 | Owner (TJ) | Everything, user management, server config, full console |

---

## 1. Admin Dashboard (EXISTING - enhance)

### Current
- [x] Server status (online/offline, TPS, player count)
- [x] Player list with join/leave tracking
- [x] TPS graph
- [x] Activity log

### Planned enhancements
- [ ] **Server resource panel** - RAM usage, loaded chunks, entity count, disk usage
- [ ] **TPS alerts** - notification when TPS drops below threshold (configurable: 15, 10, 5)
- [ ] **Player session tracking** - current session duration, total play time today
- [ ] **Auto-restart scheduling** - set daily restart time, restart on crash detection
- [ ] **Backup status** - last backup time, backup size, trigger manual backup
- [ ] **Uptime graph** - server uptime over past 24h/7d/30d

---

## 2. Player Management

### Player list (enhanced)
- [ ] **Sortable columns** - name, play time, last seen, deaths, blocks placed
- [ ] **Search/filter** - by name, by online status, by platform (Java/Bedrock/VR)
- [ ] **Platform badges** - Java, Bedrock (Geyser), VR (Vivecraft) icons
- [ ] **Quick actions per player** - dropdown with kick, ban, mute, teleport, spectate, message

### Player detail view
- [ ] **Profile card** - name, UUID, platform, skin head, first join, last seen, total play time
- [ ] **Location** - current coordinates, dimension, biome (live updated)
- [ ] **Click "Go To"** - flies the 3D viewer camera to their position
- [ ] **Stats** - blocks placed/broken, mobs killed, deaths, distance traveled (from server stats)
- [ ] **Advancements** - progress through Minecraft achievements
- [ ] **Inventory viewer** - see what they're carrying (AubsCraftInventory plugin, admin only)
- [ ] **Claims list** - all GriefPrevention claims owned by this player
- [ ] **Activity timeline** - recent actions from CoreProtect (block changes, container access)
- [ ] **Ban/kick/mute history** - past moderation actions on this player
- [ ] **Notes** - admin notes field (persisted server-side, only visible to mods+)

### Moderation actions
- [ ] **Kick** - with reason (shown to player), logged
- [ ] **Ban** - with reason, duration (permanent or timed), logged
- [ ] **Mute** - prevent chat, with duration
- [ ] **Warn** - send warning message, logged (3 warnings = auto-mute?)
- [ ] **Teleport** - TP player to coordinates or to another player
- [ ] **Teleport to player** - fly admin camera or TP admin avatar to player
- [ ] **Freeze** - prevent movement (via plugin, for investigation)
- [ ] **Spectate** - silent observation mode
- [ ] **Inventory inspect** - view player inventory without them knowing
- [ ] **Gamemode change** - switch player between survival/creative/spectator/adventure

### Whitelist management
- [ ] **Whitelist toggle** - enable/disable server whitelist
- [ ] **Add/remove players** - by name or UUID
- [ ] **Pending requests** - players who tried to join while not whitelisted (from activity log)
- [ ] **Quick whitelist from activity log** - "Whitelist Now" button on rejected join attempts

---

## 3. World Management

### World controls (EXISTING - enhance)
- [x] Time of day (set to morning/noon/night/midnight)
- [x] Weather control (clear/rain/thunder)
- [x] Server broadcast

### Planned enhancements
- [ ] **Difficulty** - change server difficulty (peaceful/easy/normal/hard)
- [ ] **Gamerule editor** - toggle common gamerules (keepInventory, mobGriefing, doFireTick, etc.)
- [ ] **World border** - view and adjust world border size/center
- [ ] **Seed display** - show world seed (admin only)
- [ ] **Spawn management** - view/set world spawn point, set player spawn points

---

## 4. In-Viewer Admin Tools (3D Map Integration)

These are admin tools that work DIRECTLY in the 3D world viewer. Point, click, manage.

### Block inspection (click any block)
- [ ] **Block info** - type, coordinates, light level, biome
- [ ] **CoreProtect history** - who placed/broke this block and when (click to see timeline)
- [ ] **Rollback** - undo block changes in a radius (CoreProtect rollback command)
- [ ] **Copy coordinates** - one-click copy "X Y Z" to clipboard

### Claim management (click any claim boundary)
- [ ] **Claim info panel** - owner, size, trust list, creation date
- [ ] **Trust management** - add/remove trusted players from admin view
- [ ] **Resize claim** - drag claim boundaries in the viewer
- [ ] **Delete claim** - remove claim (with confirmation)
- [ ] **Transfer claim** - change claim owner
- [ ] **Claim audit** - last activity, is it abandoned?

### Area selection tools
- [ ] **Selection box** - click two corners to define a 3D region
- [ ] **Area stats** - block count by type, entity count, chunk load status
- [ ] **Area rollback** - CoreProtect rollback for entire selected region
- [ ] **Area protect** - create admin claim over selected area
- [ ] **Area export** - export selection as schematic file (for backup)

### Player interaction on map
- [ ] **Right-click player marker** - context menu: kick, ban, TP to, TP here, message, spectate
- [ ] **Draw path** - visualize a player's movement trail (CoreProtect data)
- [ ] **Proximity alert** - highlight when two players are close (useful during conflicts)

---

## 5. Grief Detection + Prevention

### Automated detection
- [ ] **Unusual block destruction patterns** - large number of blocks broken in short time near another player's claim
- [ ] **TNT usage alerts** - TNT placed or detonated near claims
- [ ] **Lava/water placement alerts** - fluid placed near claims
- [ ] **Fire alerts** - fire spread near structures
- [ ] **Theft detection** - container (chest) access inside claims by non-trusted players

### Investigation tools
- [ ] **Block history search** - search CoreProtect: "who broke blocks near X,Y,Z in the last 24h?"
- [ ] **Player activity replay** - visualize a player's actions over a time range on the map
- [ ] **Comparison view** - show the area "before" vs "after" a time range (CoreProtect data)
- [ ] **Evidence snapshot** - capture current state + history for a region, save as a report

### Response tools
- [ ] **Quick rollback** - one-click rollback of a player's changes in an area
- [ ] **Quick ban + rollback** - ban the griefer AND rollback their damage in one action
- [ ] **Restore from backup** - restore a region from server backup (if available)

---

## 6. Plugin Management (EXISTING - enhance)

### Current
- [x] List installed plugins with version
- [x] Enable/disable plugins

### Planned enhancements
- [ ] **Plugin config editor** - edit plugin config.yml from web UI (with syntax highlighting)
- [ ] **Plugin update checker** - compare installed version vs latest release (Hangar/SpigotMC API)
- [ ] **Plugin logs** - filter server log by plugin name
- [ ] **Plugin dependencies** - show which plugins depend on which
- [ ] **Safe reload** - reload specific plugin without full server restart (if plugin supports it)

---

## 7. Console + RCON (EXISTING - enhance)

### Current
- [x] Server log viewer (live tailing)
- [x] RCON command input

### Planned enhancements
- [ ] **Command autocomplete** - suggest commands as admin types (from known command list)
- [ ] **Command history** - up/down arrow through previous commands
- [ ] **Saved commands** - bookmark frequently used commands
- [ ] **Scheduled commands** - run a command at a specific time or interval
- [ ] **Command macros** - define multi-command macros ("morning routine": set time day, weather clear, broadcast "good morning")
- [ ] **Log filtering** - filter by level (INFO/WARN/ERROR), by plugin, by player name
- [ ] **Log search** - full-text search through log history
- [ ] **Log export** - download log as text file for a date range

---

## 8. Server Configuration (Owner only)

- [ ] **server.properties editor** - edit with field descriptions, validation, restart prompt
- [ ] **Bukkit/Spigot/Paper config editor** - YAML editors for all config files
- [ ] **MOTD editor** - visual MOTD editor with color code preview
- [ ] **Icon manager** - upload/change server icon (64x64 PNG)
- [ ] **Whitelist import/export** - bulk add from a list, export current whitelist
- [ ] **Operator management** - add/remove ops with permission levels
- [ ] **Scheduled restarts** - configure automatic restart schedule
- [ ] **Auto-backup configuration** - backup frequency, retention, storage location

---

## 9. Notifications + Alerts System

- [ ] **Browser notifications** - push notifications for important events (player join, TPS drop, crash)
- [ ] **Email alerts** - configurable email for critical events (server down, crash, suspicious activity)
- [ ] **Alert rules** - configurable: "notify me when TPS < 10 for > 30 seconds"
- [ ] **Alert history** - log of all alerts fired, with timestamps and resolution status
- [ ] **Discord webhook** - send alerts to a Discord channel
- [ ] **Sound alerts** - browser audio notification for specific events

---

## 10. Analytics Dashboard (Owner/Admin)

- [ ] **Player retention** - new players vs returning, churn rate
- [ ] **Peak hours** - when is the server busiest? (heatmap by hour/day)
- [ ] **Popular areas** - where do players spend the most time? (CoreProtect data + positions)
- [ ] **Building activity** - blocks placed over time, trending up or down
- [ ] **Resource usage trends** - TPS, RAM, entity count over time (graphs)
- [ ] **Session length distribution** - how long do players typically play?
- [ ] **New player experience** - how far do new players get? Where do they quit?

---

## 11. Mobile Admin Quick Actions

When accessing from phone/tablet, provide a simplified admin interface:

- [ ] **Quick status** - server up/down, player count, TPS (one glance)
- [ ] **Quick actions** - kick, ban, whitelist (big touch targets)
- [ ] **Quick broadcast** - send message to all players
- [ ] **Quick restart** - restart server with confirmation
- [ ] **Alert feed** - scroll through recent alerts/events
- [ ] **Player map** - simplified top-down map with player positions (lighter weight than full 3D)

---

## 12. Audit Trail

Every admin action is logged:

- [ ] **Who did what, when** - every kick, ban, TP, gamemode change, config edit
- [ ] **Searchable** - by admin name, action type, target player, date range
- [ ] **Exportable** - CSV/JSON export for record keeping
- [ ] **Immutable** - audit log cannot be edited or deleted (append-only)
- [ ] **Visible to owner** - only Level 4 (Owner) can see the full audit trail

---

## Implementation Priority

### Do First (high impact, builds on existing)
1. Player management quick actions (kick/ban from player list)
2. In-viewer block inspection (click block, see CoreProtect history)
3. Claim visualization on the 3D map (Phase I)
4. Admin levels/roles (Phase G)
5. Notifications for critical events (TPS drop, crash)

### Do Next (enhances admin workflow)
6. Area selection tools in the viewer
7. Grief detection alerts
8. Console autocomplete + command history
9. Analytics dashboard basics (player count trends, TPS history)
10. Mobile quick actions

### Do Later (nice-to-have, builds on earlier work)
11. Plugin config editors
12. Scheduled commands/restarts
13. Full audit trail
14. Discord webhook integration
15. Server properties editor

---

## Paper Plugins Needed for Admin Tools

| Plugin | Features It Enables |
|--------|-------------------|
| AubsCraftTracker | Player positions, activity status, proximity alerts |
| AubsCraftClaims | Claim visualization, management, resize from web |
| AubsCraftInventory | Inventory viewing, item management |
| AubsCraftLink | Account linking (required for player-specific features) |

CoreProtect already provides block history - we just need to query its SQLite DB from the admin server.

---

## UI Integration

### Admin tools in the sidebar (current pages + new)
```
Dashboard        (enhanced with resource panel, alerts)
Players          (enhanced with quick actions, search, platform badges)
World            (enhanced with gamerules, world border)
Claims      NEW  (claim list, map integration, management)
Moderation  NEW  (ban list, kick history, grief alerts)
Stats            (enhanced with analytics graphs)
Plugins          (enhanced with config editor)
Map              (3D viewer with admin overlay tools)
Activity Log     (enhanced with search, export)
Console          (enhanced with autocomplete, macros)
Settings    NEW  (server config, notification rules, admin accounts)
```

### In-viewer admin toolbar
When in the 3D map, admins get an extra toolbar:
```
[Inspect] [Select Area] [Rollback] [Claims] [Players] [Teleport]
```
- Inspect: click any block for info + history
- Select Area: click two corners for region tools
- Rollback: CoreProtect rollback mode
- Claims: toggle claim boundaries visible
- Players: toggle player markers + trails
- Teleport: click anywhere to TP (admin avatar or camera)
