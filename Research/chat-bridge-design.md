# Web-to-Game Chat Bridge - Technical Design

**Date:** 2026-04-12
**Phase:** K (Web-to-Game Chat)
**Dependencies:** Account linking (Phase H) for authenticated chat, AubsCraftChat plugin

---

## The Vision

TJ is at work. Aubs is playing Minecraft at home. TJ opens map.spawndev.com on his phone, types a message. Aubs sees it in-game: `[Web] HereticSpawn: how's the build going?`

---

## Architecture

```
Browser (TJ's phone)              AubsCraft Server         MC Server
  |                                    |                       |
  | "how's the build going?"           |                       |
  |--[WebSocket]---chat message------->|                       |
  |                                    |--[WebSocket]--------->|
  |                                    |    AubsCraftChat      |
  |                                    |    broadcasts to game |
  |                                    |                       |
  |                                    |<--[chat event]--------|
  |<--[WebSocket]---incoming chat------|    in-game chat        |
  |                                    |                       |
  | Shows in web chat panel            |                       |
```

### Data flow
- **Web -> Game:** User types in web chat, sent via WebSocket to server, relayed to AubsCraftChat plugin, plugin broadcasts to MC server with [Web] prefix
- **Game -> Web:** Player chats in-game, AubsCraftChat plugin captures the event, sends to AubsCraft server via WebSocket, server broadcasts to all web viewers

---

## AubsCraftChat Paper Plugin

### Events it listens to
```java
@EventHandler
public void onChat(AsyncChatEvent event) {
    // Paper's async chat event (not deprecated PlayerChatEvent)
    String message = PlainTextComponentSerializer.plainText()
        .serialize(event.message());
    String sender = event.getPlayer().getName();
    
    // Send to AubsCraft server
    sendToServer(new ChatMessage("chat", sender, message));
}

@EventHandler
public void onPlayerDeath(PlayerDeathEvent event) {
    String message = PlainTextComponentSerializer.plainText()
        .serialize(event.deathMessage());
    sendToServer(new ChatMessage("death", event.getEntity().getName(), message));
}

@EventHandler
public void onPlayerAdvancement(PlayerAdvancementDoneEvent event) {
    String advancement = event.getAdvancement().getKey().getKey();
    sendToServer(new ChatMessage("advancement", event.getPlayer().getName(), advancement));
}
```

### Commands
```java
// Receive web chat and broadcast in-game
public void onWebChatReceived(String senderName, String message) {
    Component webMessage = Component.text("[Web] ")
        .color(NamedTextColor.AQUA)
        .append(Component.text(senderName + ": ")
            .color(NamedTextColor.WHITE))
        .append(Component.text(message)
            .color(NamedTextColor.GRAY));
    
    Bukkit.getServer().sendMessage(webMessage);
}

// Private message from web
public void onWebPrivateMessage(String senderName, String targetName, String message) {
    Player target = Bukkit.getPlayer(targetName);
    if (target == null) return;
    
    Component pm = Component.text("[Web PM] ")
        .color(NamedTextColor.LIGHT_PURPLE)
        .append(Component.text(senderName + ": ")
            .color(NamedTextColor.WHITE))
        .append(Component.text(message)
            .color(NamedTextColor.GRAY));
    
    target.sendMessage(pm);
}
```

### WebSocket connection
Plugin connects to AubsCraft server on startup:
```
ws://192.168.1.142:5080/api/chat
```

Messages are JSON:
```json
// Game -> Server
{ "type": "chat", "sender": "HereticSpawn", "message": "hello from in-game" }
{ "type": "death", "sender": "SpudArt", "message": "SpudArt fell from a high place" }
{ "type": "advancement", "sender": ".Noob607", "message": "story/mine_stone" }
{ "type": "join", "sender": "HereticSpawn" }
{ "type": "leave", "sender": "SpudArt" }

// Server -> Plugin (from web)
{ "type": "web_chat", "sender": "HereticSpawn", "message": "how's the build going?" }
{ "type": "web_pm", "sender": "HereticSpawn", "target": "SpudArt", "message": "come check this out" }
```

---

## Server-Side: Chat Relay

### WebSocket endpoints

**Plugin connection:**
```
ws://192.168.1.142:5080/api/chat/plugin
Auth: API key header
```

**Browser connections:**
```
ws://map.spawndev.com:44365/api/chat
Auth: cookie (web account)
```

### Server logic
- Receives from plugin -> broadcasts to all browser clients
- Receives from browser -> validates auth -> sends to plugin for in-game broadcast
- Stores chat history in memory (last 200 messages) for new connections
- Rate limit: 1 message per second per web user (prevent spam)

### Message validation
- Max message length: 256 characters (matches MC chat limit)
- Strip control characters
- No HTML/script injection (plain text only)
- Linked account required (must know the MC name to attribute messages)
- Optional: profanity filter (configurable by admin)

---

## Client-Side: Chat Panel

### UI Layout
```
+----------------------------------+
| Chat                      [X]    |
|                                  |
| [10:23] HereticSpawn: hello      |
| [10:24] SpudArt: hey!           |
| [10:24] * SpudArt fell from...   |
| [10:25] [Web] HereticSpawn:     |
|         how's the build?         |
| [10:26] .Noob607: nice!         |
|                                  |
| [Type a message...________] [>] |
+----------------------------------+
```

### Message types and styling
| Type | Color/Style | Icon |
|------|-------------|------|
| Player chat | White text | None |
| Web chat | Aqua [Web] prefix | Globe icon |
| Death | Red italic | Skull |
| Advancement | Gold | Trophy |
| Join | Green | + |
| Leave | Gray | - |
| System | Yellow | Gear |
| Private message | Purple | Lock |

### Chat panel features
- **Resizable/collapsible** - drag to resize, minimize to icon
- **Sound notification** - optional chime on new messages (mutable)
- **Scroll to bottom** - auto-scrolls unless user has scrolled up to read history
- **Click player name** - opens player info panel or fly-to-player
- **Click coordinates** - if a message contains coords (123, 64, -456), click to fly there
- **Emoji** - web users can use emoji, rendered in chat (MC shows as unicode)

### Mobile chat
- Chat input at bottom of screen (above virtual joystick)
- Messages appear as a transparent overlay on the 3D view
- Auto-fade after 5 seconds, show on tap
- Full chat panel slides up from bottom

### VR chat
- Chat appears as a floating panel in world space
- Voice-to-text input via Web Speech API (tap controller button to talk)
- Text-to-speech readout of incoming messages (optional)

---

## Location Sharing

When a user types coordinates in chat, they become clickable:

### Detection
```csharp
// Regex for coordinate patterns
var coordPattern = @"(-?\d+)[,\s]+(-?\d+)[,\s]+(-?\d+)";
// Matches: "228, 64, -243" or "228 64 -243" or "228,64,-243"
```

### In web chat
Coordinates render as a clickable link:
```html
<span class="coord-link" data-x="228" data-y="64" data-z="-243">
  228, 64, -243 [Fly to]
</span>
```

Clicking "Fly to" smoothly moves the camera to that location.

### In-game
The plugin could also make coordinate links clickable in MC chat (using Adventure API click events):
```java
Component coordLink = Component.text("[228, 64, -243]")
    .color(NamedTextColor.AQUA)
    .clickEvent(ClickEvent.runCommand("/tp @s 228 64 -243"))
    .hoverEvent(HoverEvent.showText(Component.text("Click to teleport")));
```

---

## Screenshot Sharing

### Web -> Game
1. User clicks "Screenshot" button (or presses F2)
2. Canvas is captured as PNG (ILGPU/WebGPU readback or canvas.toBlob)
3. PNG uploaded to server (stored in a screenshots folder)
4. Short URL generated: `map.spawndev.com:44365/s/abc123`
5. URL posted in chat: `[Screenshot] HereticSpawn shared a view: map.spawndev.com/s/abc123`
6. In-game, the URL is clickable (opens in browser)

### Storage
- Screenshots stored in OPFS (client-side) and optionally server-side
- Server-side storage for sharing URLs
- Auto-cleanup: screenshots older than 7 days deleted (configurable)
- Max 10 screenshots per user per day (rate limit)

---

## Implementation Priority

1. **AubsCraftChat plugin** - basic chat relay (game <-> server)
2. **Server chat WebSocket** - relay + history
3. **Web chat panel** - basic send/receive with styling
4. **Chat history** - load last 200 messages on connect
5. **Location sharing** - clickable coordinate links
6. **Mobile chat** - overlay on 3D view
7. **Private messages** - /msg from web
8. **Screenshot sharing** - capture and share
9. **VR chat** - voice-to-text input
