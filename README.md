# AubsCraft Admin

A real-time Minecraft server admin panel built with **Blazor WebAssembly** and **ASP.NET Core**. Connects to your Paper/Spigot server via RCON and provides a full browser-based dashboard for server management - no plugins required on the Minecraft side.

Built by [Todd Tanner (@LostBeard)](https://github.com/LostBeard) for his daughter Aubriella's Minecraft server (mc.spawndev.com).

## Features

### Dashboard
- **Real-time player count and TPS** with live history graph - all pushed via SignalR, no polling
- **Server status** - connected/disconnected indicator, 1m/5m/15m TPS readings
- **Who's playing** - live player list with platform detection (Java, Bedrock, VR)

### Player Management
- **Whitelist** - add/remove with player avatars (mc-heads.net)
- **Online players** - kick, ban, pardon with one click
- **Gamemode control** - switch players between survival, creative, spectator, adventure
- **Teleport** - teleport any player to any other player from the browser
- **Player profiles** - detailed stats per player (play time, deaths, mob kills, blocks placed/broken, distance traveled, advancements)

### World Controls
- **In-game time display** - live clock with tick count, auto-refreshes every 10 seconds
- **Time presets** - sunrise, noon, night, midnight
- **Weather control** - clear, rain, thunderstorm
- **Server broadcast** - send messages to all players
- **World save** - trigger save-all from the browser

### Server Management
- **Activity log** - real-time timeline of player joins, leaves, deaths, advancements, chat, and whitelist rejections with filterable event types
- **Whitelist rejection alerts** - toast notifications when non-whitelisted players attempt to join, with one-click "Whitelist Now" button
- **Live chat** - see in-game chat and respond via the admin panel
- **Server console** - send any RCON command directly
- **Plugin manager** - view installed plugins, enable/disable by renaming .jar files
- **Plugin browser** - search and install plugins from Modrinth
- **Server control** - start, stop, restart the Minecraft service via systemd
- **BlueMap integration** - embedded 3D world map viewer (iframe)

### Technical
- **Dark/Light theme** - defaults to system preference, toggle in the top bar
- **Cookie authentication** - first-run setup creates your admin account, bcrypt hashed
- **Log tailing** - monitors the Minecraft server log file for events RCON doesn't expose (deaths, advancements, chat)
- **Self-contained deployment** - single binary, no .NET runtime required on the server
- **One-click deploy** - included `deploy-aubscraft.bat` script (build, SSH stop, xcopy, SSH start)
- **Paper/EssentialsX compatible** - handles color codes, group prefixes, command overrides

## Requirements

- [.NET 10.0 SDK](https://dotnet.microsoft.com/) (for building)
- Minecraft Paper or Spigot server with RCON enabled
- RCON password configured in `server.properties`

## Quick Start

1. Clone the repository
2. Edit `AubsCraft.Admin.Server/appsettings.json` with your server details:
   ```json
   {
     "Rcon": {
       "Host": "127.0.0.1",
       "Port": 25575,
       "Password": "your_rcon_password"
     },
     "Minecraft": {
       "LogPath": "/opt/minecraft/server/logs/latest.log"
     }
   }
   ```
3. Run the server:
   ```bash
   dotnet run --project AubsCraft.Admin.Server
   ```
4. Open the URL shown in your browser
5. Create your admin account on first launch

## Enabling RCON on Your Minecraft Server

In your `server.properties`:
```properties
enable-rcon=true
rcon.port=25575
rcon.password=your_secure_password
```
Restart the server after changes.

## Deployment

### Quick (manual)

```bash
dotnet publish AubsCraft.Admin.Server -c Release -r linux-x64 --self-contained -o publish
```

Copy the `publish` folder to your server and run the binary. No .NET runtime needed.

### Automated (included script)

The repo includes a full deployment setup for a Linux VM with a mapped network drive:

1. **First time** - copy files to server, then run:
   ```bash
   ssh yourserver "sudo bash /srv/aubscraft/setup-service.sh"
   ```
   This installs the systemd service, enables auto-start, and configures passwordless `systemctl` for deploys.

2. **Every deploy after** - just run:
   ```
   deploy-aubscraft.bat
   ```
   Builds, stops the service via SSH, copies files via mapped drive, starts the service. Takes about 15 seconds.

### systemd service

The included `aubscraft_admin.service` runs the admin panel as a systemd service. The `setup-service.sh` script installs it, enables boot start, and adds sudoers entries so the deploy script can stop/start without a password.

## Project Structure

| Project | Description |
|---------|-------------|
| `AubsCraft.Admin` | Blazor WebAssembly frontend - all the UI pages and components |
| `AubsCraft.Admin.Server` | ASP.NET Core host - serves WASM, bridges RCON via SignalR, reads player stats |
| `SpawnDev.Rcon` | Standalone Source RCON protocol client library (TCP, async, reusable) |
| `VRDetect` | Paper plugin (Java) - detects QuestCraft VR players and stores metadata |

## Configuration Reference

| Key | Default | Description |
|-----|---------|-------------|
| `Rcon:Host` | `127.0.0.1` | Minecraft server IP |
| `Rcon:Port` | `25575` | RCON port |
| `Rcon:Password` | - | RCON password (required) |
| `Minecraft:LogPath` | `latest.log` | Path to server's latest.log |
| `BlueMap:Url` | - | BlueMap web viewer URL for iframe embed |
| `BlueMap:Enabled` | `false` | Enable BlueMap page |
| `ActivityLog:MaxEvents` | `1000` | Max events in memory |
| `ActivityLog:FilePath` | `activity-log.json` | Activity log persistence file |
| `Auth:CredentialsPath` | `admin.json` | Admin credentials file (delete to reset) |

## SpawnDev.Rcon

The RCON client library is a standalone, reusable C# implementation of the [Source RCON Protocol](https://developer.valvesoftware.com/wiki/Source_RCON_Protocol). It works with any server that implements Source RCON - Minecraft, Valve Source servers, ARK, Rust, and others.

Features:
- Async/await TCP client with automatic reconnection
- `MinecraftRconClient` with typed commands (list, whitelist, ban, kick, time, weather, TPS, etc.)
- Paper/EssentialsX/LuckPerms-aware response parsing
- Minecraft color code stripping (`MinecraftText.StripColorCodes`)
- Player list parsing handles group prefixes and multiline responses

## VRDetect Plugin

A lightweight Paper plugin (Java) that detects QuestCraft VR players. When a player joins, VRDetect checks for VR client indicators and stores the result as persistent metadata. The admin panel reads this to show VR badges next to player names.

Build:
```bash
cd VRDetect && bash build.sh
```

Drop the resulting `.jar` into your server's `plugins/` folder.

## Cross-Platform Support

The admin panel detects and displays player platforms:
- **Java** - standard Minecraft Java Edition
- **Bedrock** - players connecting via GeyserMC (detected by Floodgate prefix)
- **VR** - QuestCraft players (detected by VRDetect plugin)

## Tech Stack

- **Frontend** - Blazor WebAssembly (.NET 10), Bootstrap 5, SignalR
- **Backend** - ASP.NET Core (.NET 10), SignalR hub, Source RCON protocol
- **Data** - Minecraft NBT player stats (read directly from `world/stats/`), SQLite for VRDetect, JSON for activity log
- **Deployment** - Self-contained linux-x64, systemd service, SSH + mapped drive deploy

## License

MIT - see [LICENSE](LICENSE)
