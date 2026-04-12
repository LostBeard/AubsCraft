# Account Linking System - Technical Design

**Date:** 2026-04-12
**Phase:** H (Account Linking)
**Dependencies:** Phase G (Multi-User Auth) must be done first

---

## Overview

Links a Minecraft character (UUID) to an AubsCraft web account. This unlocks all player-facing features: claim viewing, base editing, chat, inventory, profiles.

## Flow

```
1. Player visits AubsCraft website
2. Creates web account (email + password)
3. Website generates a 6-char verification code, shows it on screen
4. Player logs into Minecraft, types: /link ABC123
5. AubsCraftLink plugin receives the command
6. Plugin calls AubsCraft server API: POST /api/link/verify { code: "ABC123", uuid: "...", name: "..." }
7. Server validates code, links UUID to web account
8. Player sees "Account linked!" in-game
9. Website shows "Linked to: HereticSpawn" 
10. All player features unlock
```

## Components

### 1. AubsCraftLink Paper Plugin

```java
// Commands
/link <code>     - Link MC account to web account
/unlink          - Remove the link
/profile         - Show profile URL
/profile public  - Make profile public
/profile private - Make profile private

// Events
PlayerJoinEvent  - Check if player is linked, show welcome message
PlayerQuitEvent  - Update last-seen timestamp
```

**Communication:** HTTP POST to the AubsCraft admin server API.
The plugin needs the admin server URL configured in its config.yml:
```yaml
admin-server-url: "http://192.168.1.142:5080"
```

**Security:**
- Verification codes expire after 5 minutes
- Codes are single-use
- Rate limit: 1 code request per minute per web account
- Rate limit: 3 /link attempts per minute per player
- The API endpoint validates that the code exists and hasn't expired

### 2. Server API Endpoints

```
POST /api/link/generate     - Generate a verification code (authenticated web user)
  Request:  (none - uses auth cookie to identify web account)
  Response: { code: "ABC123", expiresAt: "2026-04-12T16:05:00Z" }

POST /api/link/verify       - Verify a code from the MC plugin
  Request:  { code: "ABC123", uuid: "51284fe7-...", name: "HereticSpawn" }
  Response: { success: true } or { success: false, error: "expired" }
  Note: This endpoint is called by the plugin, not the browser. 
        Auth: plugin API key in header, not cookie auth.

GET  /api/link/status        - Check if current web account is linked
  Response: { linked: true, mcName: "HereticSpawn", mcUuid: "51284fe7-..." }
            or { linked: false }

POST /api/link/unlink        - Unlink accounts (from web or from /unlink command)
  Response: { success: true }
```

### 3. Data Storage

Add to the user account model:
```json
{
  "username": "tj@spawndev.com",
  "passwordHash": "...",
  "salt": "...",
  "level": 4,
  "mcUuid": "51284fe7-1234-5678-9abc-def012345678",
  "mcName": "HereticSpawn",
  "mcPlatform": "java",
  "linkedAt": "2026-04-12T15:00:00Z",
  "profilePublic": true
}
```

Pending codes stored in memory (or a simple JSON file):
```json
{
  "ABC123": {
    "webAccountId": "tj@spawndev.com",
    "createdAt": "2026-04-12T15:00:00Z",
    "expiresAt": "2026-04-12T15:05:00Z"
  }
}
```

### 4. Bedrock/Geyser Player Handling

Bedrock players connect through Geyser with fake UUIDs:
- Format: `00000000-0000-0000-0009-{xbox-id-hash}`
- Player name: prefixed with `.` (e.g., `.Noob607`)
- Floodgate stores the mapping in `plugins/floodgate/players/`

The linking system treats these UUIDs the same as Java UUIDs. The plugin doesn't care about the format - it just passes the UUID from `player.getUniqueId()`.

For display purposes, we mark the platform:
```java
boolean isBedrock = player.getUniqueId().toString().startsWith("00000000-0000-0000-0009");
String platform = isBedrock ? "bedrock" : "java";
// VR detection from VRDetect plugin data
boolean isVR = vrPlayers.containsKey(player.getUniqueId());
if (isVR) platform = "vr";
```

### 5. Security Considerations

- **Plugin API authentication:** The plugin uses an API key (configured in plugin config.yml and server appsettings.json). This prevents random HTTP requests from linking accounts.
- **Code brute-force:** 6 alphanumeric chars = 2.1 billion combinations. With rate limiting (3 attempts/min), brute-force is impractical.
- **Impersonation prevention:** Only the actual in-game player can run /link with their UUID. The server verifies the UUID came from the plugin (authenticated via API key), not from a web request.
- **Unlink protection:** Unlinking requires either the web account password or the in-game /unlink command. An admin can force-unlink from the admin panel.

---

## What Linking Unlocks (by phase)

| Phase | Feature | Requires Linking? |
|-------|---------|:-:|
| I | View own claims highlighted | Yes |
| I | Click to fly to own claims | Yes |
| J | Player position on map | No (admin sees all) |
| K | Chat from web | Yes |
| L | Base editing (creative mode) | Yes |
| M | Public profile page | Yes |
| M | Achievement tracking | Yes |
| N | AI villager personalized responses | Yes |
| P | Spectate (as authenticated viewer) | Yes |
| Q | VR base building | Yes |
| R | Inventory management | Yes |

---

## Implementation Order

1. Multi-user auth (Phase G) - required foundation
2. AubsCraftLink plugin - `/link` command, API communication
3. Server API endpoints - code generation, verification
4. Web UI - "Link Account" page with code display
5. Linked status indicators - MC name badge in header, platform icon
6. Test with all 3 players: HereticSpawn (Java), SpudArt (Java), .Noob607 (Bedrock)
