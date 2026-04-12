# Paper Plugin Development Reference

**Date:** 2026-04-12
**Based on:** VRDetect plugin (working, deployed on AubsCraft server)

---

## Project Structure

```
PluginName/
  build.sh                    # Shell build script (no Maven/Gradle needed)
  build/
    PluginName.jar            # Output
  src/
    main/
      java/com/spawndev/pluginname/
        PluginNamePlugin.java # Main class
      resources/
        plugin.yml            # Plugin descriptor
```

## Build (no Maven/Gradle)

VRDetect uses a simple `javac` + `jar` build - no build system needed for small plugins.

```bash
# Compile against Paper API jar
javac -cp "$PAPER_JAR" -d build/classes src/main/java/com/spawndev/pluginname/*.java

# Package with plugin.yml
cp src/main/resources/plugin.yml build/classes/
cd build/classes && jar cf ../PluginName.jar .
```

Paper API jar location on TJ's server:
`M:\opt\minecraft\server\libraries\io\papermc\paper\paper-api\1.21.5-R0.1-SNAPSHOT\`

Alternatively, point at `M:\opt\minecraft\server\paper.jar` (the full server jar includes API).

Java version: **Microsoft OpenJDK 21**

## plugin.yml

```yaml
name: PluginName
version: 1.0.0
main: com.spawndev.pluginname.PluginNamePlugin
api-version: '1.21'
description: What the plugin does
author: LostBeard
website: https://github.com/LostBeard
commands:
  link:
    description: Link your MC account to the website
    usage: /link <code>
    permission: aubscraft.link
permissions:
  aubscraft.link:
    description: Allow account linking
    default: true
```

## Main Plugin Class Pattern

```java
package com.spawndev.pluginname;

import org.bukkit.entity.Player;
import org.bukkit.event.EventHandler;
import org.bukkit.event.Listener;
import org.bukkit.event.player.PlayerJoinEvent;
import org.bukkit.plugin.java.JavaPlugin;

public class PluginNamePlugin extends JavaPlugin implements Listener {

    @Override
    public void onEnable() {
        // Register events
        getServer().getPluginManager().registerEvents(this, this);
        getLogger().info("PluginName enabled");
    }

    @Override
    public void onDisable() {
        getLogger().info("PluginName disabled");
    }

    @EventHandler
    public void onPlayerJoin(PlayerJoinEvent event) {
        Player player = event.getPlayer();
        // handle event
    }
}
```

## Communication with AubsCraft Server

### Option A: File-based (VRDetect pattern)
Plugin writes JSON to `plugins/PluginName/data.json`. AubsCraft server reads the file.
- Simple, no network code
- Slight delay (file polling or FileSystemWatcher)
- Good for: VR detection, static data

### Option B: HTTP endpoint in plugin
Plugin starts a lightweight HTTP server (Spark/Javalin/NanoHTTPD).
- Real-time request/response
- More complex (add HTTP library dependency)
- Good for: claim queries, inventory access, command execution

### Option C: Plugin channel messages
Plugin sends/receives custom messages on named channels.
- Uses Bukkit's plugin messaging API
- Requires a connected player as the message target
- Good for: player-specific data exchange

### Option D: WebSocket from plugin
Plugin opens a WebSocket connection to the AubsCraft admin server.
- True real-time bidirectional
- Plugin connects TO the admin server (not the other way around)
- Good for: player position streaming, chat bridge, live events

**Recommended for AubsCraft:** Option D (WebSocket) for real-time plugins (tracker, chat, build).
Option A (file-based) for simple plugins (VR detection, static data).

## Planned AubsCraft Plugins

| Plugin | Communication | Key APIs Needed |
|--------|--------------|-----------------|
| AubsCraftLink | File + Command | CommandExecutor, PlayerJoinEvent |
| AubsCraftClaims | WebSocket | GriefPrevention API (external dep) |
| AubsCraftTracker | WebSocket | PlayerMoveEvent (throttled), Scheduler |
| AubsCraftChat | WebSocket | AsyncChatEvent (Paper), CommandExecutor |
| AubsCraftBuild | WebSocket | BlockPlaceEvent, World.setBlockData() |
| AubsCraftAI | HTTP (Claude API calls) | CommandExecutor, NPC spawning |
| AubsCraftInventory | WebSocket | PlayerInventory, Container access |

## GriefPrevention API Access

GriefPrevention exposes a Java API. To use it from a plugin:

```java
// In your plugin's onEnable():
if (getServer().getPluginManager().getPlugin("GriefPrevention") != null) {
    // GP is loaded, safe to use its API
    GriefPrevention gp = GriefPrevention.instance;
    
    // Get all claims
    Collection<Claim> claims = gp.dataStore.getClaims(0, Integer.MAX_VALUE);
    
    // Get claims for a player
    PlayerData playerData = gp.dataStore.getPlayerData(playerUUID);
    
    // Get claim at a location
    Claim claim = gp.dataStore.getClaimAt(location, false, null);
    
    // Claim properties:
    // claim.getOwnerID() - UUID
    // claim.getLesserBoundaryCorner() - Location
    // claim.getGreaterBoundaryCorner() - Location
    // claim.getArea() - int (square blocks)
    // claim.getTrustList() - permissions
}
```

Compile with GriefPrevention.jar on classpath. The jar is at:
`M:\opt\minecraft\server\plugins\GriefPrevention.jar`

## Deploy

Copy the built .jar to `M:\opt\minecraft\server\plugins\` and restart the server,
or use RCON: `reload confirm` (hot-reload, but not recommended for production).

## Testing

1. Build the plugin
2. Copy to server plugins/
3. Restart server via RCON or systemctl
4. Check logs: `M:\opt\minecraft\server\logs\latest.log`
5. Test commands in-game or via RCON from the admin panel
