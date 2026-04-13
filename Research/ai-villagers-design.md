# AI Villager NPCs - Technical Design

**Date:** 2026-04-12
**Phase:** N (AI Villagers)
**Dependencies:** Account linking (Phase H), AubsCraftAI plugin, Claude API

---

## The Vision

Walk up to a villager in the 3D viewer (or in VR), click on them, and have a conversation. The villager knows the server - who's online, what's been built, where things are. They remember you from last time. They have personality.

---

## Villager Types

### Town Crier (spawn area)
- **Personality:** Dramatic, enthusiastic, loves gossip
- **Knowledge:** Server news, recent events, who's online, who joined/left today
- **Sample dialogue:** "Hear ye! HereticSpawn has been building near the eastern shore! SpudArt was seen mining diamonds in the deep caverns! Three travelers walk our lands today!"
- **Data sources:** Activity log, player positions, online list

### Cartographer (near map room or library build)
- **Personality:** Precise, helpful, directional
- **Knowledge:** World geography, biome locations, notable builds, coordinates
- **Sample dialogue:** "The nearest village lies 340 blocks northeast, at coordinates 450, 72, -120. The dark oak forest begins roughly 200 blocks south of here."
- **Data sources:** World chunk data (biome info), warp list (Essentials), claim locations

### Historian (near a monument or old build)
- **Personality:** Wise, contemplative, speaks in past tense
- **Knowledge:** Block change history, who built what, server timeline
- **Sample dialogue:** "This grand hall was erected by HereticSpawn over three days in March. 4,382 blocks of oak planks and stone bricks were placed. It replaced a modest dirt shelter that stood here since the server's first day."
- **Data sources:** CoreProtect block history, player stats, server age

### Quest Giver (adventure area)
- **Personality:** Mysterious, challenging, rewards-focused
- **Knowledge:** Active quests (NotQuests plugin), achievements, challenges
- **Sample dialogue:** "Brave adventurer! I seek one who can retrieve 64 diamonds from the depths. Complete this task and your name shall be etched in the Hall of Legends!"
- **Data sources:** NotQuests plugin, advancement data, custom quest definitions

### Merchant (market area)
- **Personality:** Shrewd, fair, economy-focused
- **Knowledge:** Player inventories (if linked), item values, trade suggestions
- **Sample dialogue:** "Ah, I see you carry 23 emeralds. The going rate for enchanted diamond gear is 12 emeralds per piece. Shall I check what SpudArt has for trade?"
- **Data sources:** AubsCraftInventory plugin, economy plugin (if installed)

### Lore Master (library or enchanting area)
- **Personality:** Scholarly, detailed, loves explaining game mechanics
- **Knowledge:** Minecraft game mechanics, enchantment info, crafting recipes, mob behavior
- **Sample dialogue:** "Fortune III increases diamond yield by an average of 2.2x. Combined with an Efficiency V pickaxe, you could mine approximately 120 diamonds per hour in the optimal Y-level range of -59 to -64."
- **Data sources:** Claude's built-in Minecraft knowledge + server-specific data

---

## Architecture

```
Browser (viewer)                AubsCraft Server              Claude API
  |                                  |                           |
  | Click villager                   |                           |
  | "Hello, what's new?"            |                           |
  |--[WebSocket]--chat msg---------->|                           |
  |                                  | Build prompt:             |
  |                                  |   System: villager persona|
  |                                  |   Context: server state   |
  |                                  |   History: past convo     |
  |                                  |   User: "what's new?"     |
  |                                  |--[HTTP]--Claude API------>|
  |                                  |                           |
  |                                  |<--[response]--------------|
  |                                  |                           |
  |<--[WebSocket]--response----------|                           |
  | Display in chat bubble           |                           |
  | TTS speaks the response          |                           |
```

### Data pipeline
1. User clicks a villager marker in the 3D viewer
2. Chat panel opens with the villager's name and portrait
3. User types (or speaks via Web Speech API)
4. Message sent to server via WebSocket
5. Server builds a Claude API prompt with:
   - Villager persona (system prompt)
   - Current server context (who's online, time of day, recent events)
   - Conversation history (last 20 messages with this villager)
   - User's message
6. Claude responds (streaming for faster perceived response)
7. Response sent back to viewer
8. Text displayed in chat bubble + spoken via TTS

### Prompt structure
```
System: You are {VillagerName}, a {personality} villager NPC in the AubsCraft 
Minecraft server. You speak in character. You know the following about the server:

Current state:
- Online players: HereticSpawn (VR), SpudArt (Java)
- Server time: Day (6000 ticks)
- Weather: Clear
- Recent events: SpudArt placed 142 blocks in the last hour near (450, 72, -120)

Your knowledge:
{villager-specific context from data sources}

Rules:
- Stay in character as {VillagerName}
- Keep responses under 100 words (natural conversation length)
- Reference real server data when relevant
- Remember previous conversations with this player
- Never break character or acknowledge being an AI
```

---

## Server-Side: AI Service

### Conversation management
```csharp
public class VillagerAIService
{
    // Per-villager, per-player conversation history
    // Key: "{villagerId}:{playerUuid}"
    private readonly Dictionary<string, List<ChatMessage>> _histories = new();
    
    public async Task<string> GetResponse(
        string villagerId, string playerUuid, string userMessage)
    {
        var history = GetOrCreateHistory(villagerId, playerUuid);
        var context = await BuildContext(villagerId);
        var persona = GetPersona(villagerId);
        
        history.Add(new("user", userMessage));
        
        var response = await _claude.Messages.CreateAsync(new()
        {
            Model = "claude-sonnet-4-6",  // fast, affordable for NPCs
            MaxTokens = 200,
            System = $"{persona}\n\nCurrent server state:\n{context}",
            Messages = history.TakeLast(20).ToList(),
        });
        
        var reply = response.Content[0].Text;
        history.Add(new("assistant", reply));
        
        // Persist history (trim to last 50 messages)
        if (history.Count > 50)
            history.RemoveRange(0, history.Count - 50);
        SaveHistory(villagerId, playerUuid, history);
        
        return reply;
    }
}
```

### Context building
```csharp
async Task<string> BuildContext(string villagerId)
{
    var sb = new StringBuilder();
    
    // Online players
    var players = await _rcon.GetPlayersAsync();
    sb.AppendLine($"Online: {string.Join(", ", players.Players)}");
    
    // Time and weather
    var time = await _rcon.GetTimeAsync();
    sb.AppendLine($"Time: {time.TimeFormatted}");
    
    // Villager-specific context
    switch (villagerId)
    {
        case "historian":
            // Recent CoreProtect activity
            var recentChanges = await _coreProtect.GetRecentChanges(100);
            sb.AppendLine($"Recent building: {FormatChanges(recentChanges)}");
            break;
            
        case "cartographer":
            // Notable locations
            var warps = await _essentials.GetWarps();
            sb.AppendLine($"Known locations: {FormatWarps(warps)}");
            break;
            
        // ... etc for each type
    }
    
    return sb.ToString();
}
```

### Cost management
- Use Claude Sonnet (not Opus) for NPC responses - fast and cost-effective
- Cache context data (refresh every 30 seconds, not per request)
- Rate limit: 10 messages per minute per player per villager
- Max conversation length: 50 messages before oldest are pruned
- Estimated cost: ~$0.001 per response (200 tokens out, ~500 tokens in)

---

## AubsCraftAI Paper Plugin

### What it does
- Spawns custom villager NPCs at configured locations
- Villagers are invulnerable, don't move, have custom names
- Right-clicking a villager in-game opens a chat interface (book/sign or custom UI)
- In-game chat with villagers works the same as web chat

### Villager placement
Admin command: `/villager place <type> <name>`
Places a villager NPC at the admin's current location.

Config file: `plugins/AubsCraftAI/villagers.yml`
```yaml
villagers:
  - id: "town_crier"
    name: "Herald"
    type: "LIBRARIAN"
    location: { world: "world", x: 228, y: 65, z: -243 }
  - id: "cartographer"
    name: "Mapkeeper"
    type: "CARTOGRAPHER"
    location: { world: "world", x: 235, y: 65, z: -250 }
```

### In-game interaction
When a player right-clicks the villager NPC:
1. Plugin sends the player name + villager ID to AubsCraft server
2. Server processes through VillagerAIService (same as web path)
3. Response sent back to plugin
4. Plugin displays response as chat message to the player:
   `[Herald] Hear ye! The sun shines upon our fair realm!`

---

## Client-Side: Villager Chat UI

### In the 3D viewer
- Villager markers rendered at their world positions (special icon, not player markers)
- Click a villager -> chat panel opens with villager portrait and name
- Chat panel has text input and optional "Talk" button (voice)
- Responses appear with villager's name and styled text
- Chat bubble floats near the villager in the 3D view

### Voice interaction (Web Speech API)
1. Click "Talk" button (or press V key)
2. Browser captures microphone (SpeechRecognition)
3. Player speaks naturally
4. Speech-to-text converts to text
5. Text sent to Claude API via server
6. Response text received
7. SpeechSynthesis speaks the response in the villager's voice
8. Total latency: ~2-3 seconds

### VR voice interaction
- In VR, walk up to a villager marker
- Press controller trigger to start talking
- Microphone captures through Quest headset
- Same STT -> Claude -> TTS pipeline
- Response plays through Quest speakers
- Spatial audio: villager voice comes from their 3D position

---

## Villager Memory

### Short-term (conversation)
- Last 20 messages in the current conversation
- Passed as chat history to Claude API
- "I remember you asked about diamonds earlier"

### Long-term (persistent)
- Key facts about each player stored between sessions
- Saved to server file: `data/villager-memories/{villagerId}/{playerUuid}.json`
- Injected into the system prompt: "You remember that this player is building a castle near the river"
- Updated by Claude: add a `[MEMORY: player is building a castle]` tag in responses, server extracts and saves

### Shared knowledge
- Some facts are shared across all villagers (server events, major builds)
- Stored in `data/villager-memories/shared.json`
- "The great bridge between continents was completed last week"

---

## Performance Notes

- Claude API calls are async HTTP, don't block rendering
- Responses are streamed (first tokens appear in ~500ms)
- Context data is cached server-side (30-second refresh)
- Voice processing (STT/TTS) runs in browser, not on server
- Villager markers are simple geometry (ILGPU kernel for positioning, same as player markers)
- No impact on the rendering pipeline - it's all UI/network

---

## Implementation Priority

1. **VillagerAIService** on server - Claude API integration, persona management
2. **Basic web chat** - click villager, type, get response
3. **Town Crier** - first villager with live server data
4. **Conversation history** - persistent per-player memory
5. **Voice input** - Web Speech API for spoken interaction
6. **Voice output** - TTS with per-villager voice selection
7. **AubsCraftAI plugin** - in-game villager NPCs
8. **Long-term memory** - facts remembered between sessions
9. **VR voice** - spatial audio, controller trigger to talk
10. **Additional villagers** - Cartographer, Historian, Quest Giver, etc.
