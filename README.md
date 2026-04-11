# AubsCraft Admin

A real-time Minecraft server admin panel built with Blazor WebAssembly and ASP.NET Core. Connects to your Paper/Spigot server via RCON and provides a browser-based dashboard for server management.

Built by [Todd Tanner (@LostBeard)](https://github.com/LostBeard) for his daughter's Minecraft server.

## Features

- **Real-time Dashboard** - Player count, server TPS with history graph, online status - all pushed via SignalR
- **Player Management** - Whitelist add/remove, kick, ban, pardon with player avatars
- **Gamemode Control** - Switch players between survival, creative, spectator, adventure
- **Teleport** - Teleport players to each other from the browser
- **World Controls** - Time of day, weather, server broadcast messages, world save
- **Activity Log** - Real-time timeline of player joins, leaves, deaths, advancements, chat, and whitelist rejections with filterable event types
- **Whitelist Rejection Alerts** - Toast notifications when non-whitelisted players attempt to join, with one-click "Whitelist Now" button
- **Live Chat** - See in-game chat and respond via the admin panel
- **Server Console** - Send any command directly to the server
- **BlueMap Integration** - Embedded 3D world map viewer
- **Dark/Light Theme** - Defaults to system preference, toggle in the top bar
- **Authentication** - Cookie-based admin login, first-run setup creates your account
- **Log Tailing** - Monitors the Minecraft server log file for events RCON doesn't expose

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

For production deployment as a self-contained application (no .NET runtime required on the server):

```bash
dotnet publish AubsCraft.Admin.Server -c Release -r linux-x64 --self-contained -o publish
```

Copy the `publish` folder to your server and run the binary. A systemd service file is recommended for auto-start.

## Project Structure

| Project | Description |
|---------|-------------|
| `SpawnDev.Rcon` | Generic Source RCON protocol client library (TCP, async) |
| `AubsCraft.Admin` | Blazor WebAssembly frontend |
| `AubsCraft.Admin.Server` | ASP.NET Core host - serves WASM, bridges RCON via SignalR |

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

The RCON client library (`SpawnDev.Rcon`) is a standalone, reusable implementation of the [Source RCON Protocol](https://developer.valvesoftware.com/wiki/Source_RCON_Protocol). It works with any server that implements Source RCON - Minecraft, Valve Source servers, and others.

## License

MIT - see [LICENSE](LICENSE)
