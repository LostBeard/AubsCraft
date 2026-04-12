# AubsCraft Server Plugin Data Reference

**Server:** Paper 1.21.5 at `M:\opt\minecraft\server\`
**Date researched:** 2026-04-12

---

## Installed Plugins (22)

| Plugin | Jar | Data Dir | Purpose |
|--------|-----|----------|---------|
| BKCommonLib | BKCommonLib.jar | BKCommonLib/ | Library for TrainCarts |
| BlueMap | BlueMap.jar | BlueMap/ | 2D/3D web map (being replaced by our viewer) |
| Chunky | Chunky.jar | Chunky/ | World pre-generation |
| CoreProtect | CoreProtect.jar | CoreProtect/ | Block change logging/rollback |
| EssentialsX | EssentialsX.jar | Essentials/ | Core server management |
| Floodgate | Floodgate-Spigot.jar | floodgate/ | Bedrock player identity bridging |
| Geyser | Geyser-Spigot.jar | Geyser-Spigot/ | Bedrock protocol translation |
| GriefPrevention | GriefPrevention.jar | GriefPreventionData/ | Land claiming (golden shovel) |
| LuckPerms | LuckPerms.jar | LuckPerms/ | Permission management |
| MyPet | MyPet.jar | MyPet/ | Pet companions |
| NotQuests | NotQuests.jar | NotQuests/ | Quest system |
| RealisticSeasons | RealisticSeasons.jar | RealisticSeasons/ | Seasonal changes |
| SimpleVoiceChat | SimpleVoiceChat.jar | voicechat/ | Proximity voice chat |
| TrainCarts | TrainCarts.jar | Train_Carts/ | Rideable trains on rails |
| TC-Coasters | TC-Coasters.jar | TCCoasters/ | TrainCarts roller coaster addon |
| Timber/TreeTimber | TreeTimber.jar | Timber/ | Chop whole trees |
| ViaVersion | ViaVersion.jar | ViaVersion/ | Multi-version client support |
| VillagerGPT | VillagerGPT.jar | VillagerGPT/ | AI-powered villager chat |
| VillagerOptimizer | VillagerOptimizer.jar | VillagerOptimizer/ | Performance for villagers |
| VivecraftExtensions | VivecraftPaperExtensions.jar | VivecraftPaperExtentions/ | VR support |
| VRDetect | VRDetect.jar | VRDetect/ | Custom TJ plugin: VR player detection |
| WorldEdit | WorldEdit.jar | WorldEdit/ | In-game world editing |

---

## GriefPrevention (Claim System)

**Config:** `plugins/GriefPreventionData/config.yml`
- Claim tool: GOLDEN_SHOVEL
- Initial blocks: 100 per player
- Accrual: 100 blocks/hour, max 80,000
- Auto-claim radius: 4 blocks for new players
- Claim expiry: 14 days unused, 60 days inactive (exempt if >10K blocks)
- Claims enabled only in overworld (disabled in nether/end)

**Player data format:** `plugins/GriefPreventionData/PlayerData/<uuid>`
```
<accrued_claim_blocks>
<bonus_claim_blocks>
```
Simple two-line text file. Line 1 = accrued, line 2 = bonus.

**Claim data:** `plugins/GriefPreventionData/ClaimData/` - currently EMPTY
- GP may store claims in memory and serialize on shutdown
- Or claims may be in the world's data folder
- Need to test: restart server and check if files appear
- Alternative: query via RCON commands or write a custom plugin endpoint

**RCON claim commands to test:**
- `/claimslist <player>` - may return claim coordinates
- `/claiminfo` - info about claim at current position
- These may not be RCON-accessible (may require being in-game)

---

## Essentials

**Player data:** `plugins/Essentials/userdata/<uuid>.yml`

Key fields per player:
```yaml
last-account-name: HereticSpawn
ip-address: 192.168.1.2
timestamps:
  logout: 1775973384004  # Unix millis
  login: 1775997722231
logoutlocation:
  world-name: world
  x: 228.51627
  y: 64.0
  z: -243.72546
  yaw: -86.25
  pitch: 6.93
```

**Warps:** `plugins/Essentials/warps/` - YAML files per warp name

**Useful for viewer:**
- Last known player location (even when offline)
- Login/logout timestamps (activity tracking)
- Warp list with coordinates

---

## CoreProtect

**Database:** `plugins/CoreProtect/database.db` (SQLite)

Records every block change, container access, chat message. Tables include:
- `co_block` - block place/break events with coordinates, player, timestamp
- `co_container` - chest/inventory changes
- `co_chat` - chat log
- `co_session` - login/logout sessions

**Useful for viewer:**
- Block change history at any coordinate (who placed/broke what, when)
- Activity heatmaps (where are blocks being changed most?)
- Rollback visualization (show what changed over time)
- Build timeline (animate construction of a base)

---

## LuckPerms

**Database:** `plugins/LuckPerms/luckperms-h2-v2.mv.db` (H2)

Stores player groups, permissions, metadata. Useful for role-based access in the web viewer (admin vs player vs guest).

---

## User Cache

**File:** `server/usercache.json`

```json
[
  {"name":"HereticSpawn","uuid":"51284fe7-...","expiresOn":"2026-05-11 ..."},
  {"name":"SpudArt","uuid":"2f0a5428-...","expiresOn":"2026-05-11 ..."},
  {"name":".Noob607","uuid":"00000000-0000-0000-0009-00000a90b894","expiresOn":"2026-05-12 ..."}
]
```

Bedrock players (via Geyser/Floodgate) have UUIDs starting with `00000000-0000-0000-0009-`.
Username prefix `.` indicates Floodgate Bedrock player.

---

## RCON Capabilities (already implemented in ServerHub.cs)

| Command | Hub Method | Returns |
|---------|------------|---------|
| Player list | GetCurrentStatus() | Online players via monitor |
| Player position | GetPlayerPositions() | x/y/z per online player |
| Time query | GetWorldTimeWeather() | Ticks + formatted time |
| Set time/weather | SetTime/SetWeather | Confirmation |
| Whitelist CRUD | WhitelistAdd/Remove/Get | Player list |
| Ban/kick/pardon | BanPlayer/KickPlayer/Pardon | Confirmation |
| Give item | GiveItem | Confirmation |
| Teleport | TeleportPlayer | Confirmation |
| Save world | SaveWorld | Confirmation |
| Raw command | SendCommand | Raw response |

**Not yet implemented but available via SendCommand:**
- `/claimslist <player>` - need to test if GP responds via RCON
- `/essentials:seen <player>` - last seen info
- `/coreprotect inspect` - block history (may need in-game context)
