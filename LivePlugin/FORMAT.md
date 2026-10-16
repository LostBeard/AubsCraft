# AubsCraft Live frame (format 1)

One frame is one binary body, little-endian, POSTed by the plugin to `{panel-url}/api/live/ingest/{server-id}` with the header
`X-Live-Secret`. The encoder is `src/com/spawndev/aubscraftlive/Frames.java`; the reader is `AubsCraft.Live.Model/LiveFrameCodec.cs`. They are
tested against each other (`AubsCraft.Admin.Server.Tests/LiveFrameCodecTests.cs`: the C# encoder must write the Java encoder's bytes exactly).

```
"ACL1"  u8 version (1)  i64 gameTime (the world game time: it ticks even when the day cycle is off)  i64 dayTime  u8 weather (bit0 storm, bit1 thunder)
u16 typeCount, then typeCount x str8 type key        ("cow", "player", "mymod:thing": the minecraft namespace is dropped)
u8 worldCount, then per world:
    str8 name
    u16 entityCount, then per entity (29 bytes):
        i32 id            the server's entity id, stable while the entity is loaded
        u16 typeIndex     into the type table
        f32 x, f32 y, f32 z
        i8 yaw, i8 pitch, i8 bodyYaw        256 steps a turn; yaw is the head's
        u16 flags         1 on ground, 2 sneaking, 4 sprinting, 8 swimming, 16 gliding, 32 sleeping, 64 baby, 128 burning,
                          256 glowing, 512 invisible, 1024 has name, 2048 player, 4096 riding, 8192 dead
        i16 vx, i16 vy, i16 vz              1/8000 block per tick
    u16 nameCount,    then per name:    i32 id, str8 name
    u16 variantCount, then per variant: i32 id, str8 variant      (sheep colour, cat type, rabbit/fox type, wolf variant, villager profession)
    u16 playerCount,  then per player:  i32 id, i64 uuidMost, i64 uuidLeast, str8 name, str16 skinUrl, u8 slim, u8 gameMode,
                                        str8 heldItem, u8 health (0-255 of 20 hearts)
str8  = u8 length + that many UTF-8 bytes (at most 255, cut at a whole character)
str16 = u16 length + that many UTF-8 bytes
```

A frame holds every entity in the loaded chunks of every world (dropped items only when `include-items` is on, at most `max-items`, and each names its item in the variant), up to `max-entities`
(default 3000). At 5 frames a second that is about 150 KB/s on loopback for a busy world.

The reader refuses anything that is not exactly this: wrong magic or version, a count that cannot fit the bytes left, a type index past the
table, bytes after the last world.

## Building and testing

```
LivePlugin/build.sh "<jdk 21 bin>" <server's server.jar>      # -> AubsCraft.Admin.Server/ServerPlugins/aubscraft-live.jar
LivePlugin/make-fixture.sh "<jdk 21 bin>"                      # -> AubsCraft.Admin.Server.Tests/TestData/live-frame-v1.bin
```

`build.sh` compiles against the Paper API inside the server's own `server.jar` bundle (`META-INF/libraries`), so the plugin always matches
the version it will run on.

## Chunk updates (`POST /api/live/chunk/{serverId}?cx=&cz=`)

The panel reads the world from region files, which the server writes minutes after a change. `ChunkWatcher` notices which chunks of the MAIN world changed (block events, commands that edit blocks without one, and the panel's own edits) and, once a chunk has been quiet for 8 ticks (or has changed for 40), posts it. Same gate as the frames: loopback, `X-Live-Secret`. At most 2 chunks are read per 4 ticks, within 12 ms. The panel keeps up to 256 of them in front of the files, serves them from `/api/world/chunk-ids` and `/api/world/chunk-light`, and sends the viewers `ACHG` (below). All numbers little-endian.

```
u8   version (1)
u8   border mask: which neighbours the change touches (1 -X, 2 +X, 4 -Z, 8 +Z)
u16  top: layers sent, counted from the world's bottom (y -64); everything above is air under open sky
u16  palette count; each entry: u16 length, UTF-8 block data string ("minecraft:oak_stairs[facing=north,half=bottom,...]")
u16  blocks[256 * top]: palette indices, x + z*16 + y*256
u8   light[256 * top]: sky light << 4 | block light, same order
u16  rain[256]: where rain stops, per column (x + z*16): the top of the highest MOTION_BLOCKING block, counted up from the world bottom (so the world y is the value minus 64)
u16  sign count; each: u8 x, i16 y, u8 z, 4 x string (the front's lines, plain text), string dye ("black"), u8 glowing
u16  banner count; each: u8 x, i16 y, u8 z, u8 layers, (string pattern ("stripe_top"), string dye)...
```

The viewers' socket (`/api/live/ws`) carries these frames as well as the creature frames; a message starting `ACHG` is: `u16 count`, then for each chunk `i32 cx, i32 cz, u8 border mask`. A viewer that has the column loaded fetches it again, swaps its mesh when the new one is ready, and meshes the bordering neighbours again for the sides in the mask. A column that is not loaded is fetched fresh when it is wanted.

## Edits (the panel's admins change the world)

The plugin makes a long poll, `GET /api/live/commands/{serverId}` (same gate as the frames: loopback + `X-Live-Secret`); the panel answers when it has commands (or 204 after ~25 s). The answer is `u16 count`, then for each command `i32 length` and its bytes. Each command's result goes back as JSON to `POST /api/live/commands/{serverId}/result`: `{"id", "ok", "edit", "placed", "skipped", "applied", "error"}`. Commands are binary, little-endian (`Edits.java` reads them, `EditCodec` writes them): `u8 kind` (1 edit, 2 undo), `string id`; an edit goes on with `string editor, string label, u16 palette count + strings (block data), u16 op count` and the ops: `1 set` (i32 x y z, u16 block), `2 fill` (i32 x1 y1 z1 x2 y2 z2, u16 block, i32 only-where (-1 none), u8 hollow), `3 blocks` (i32 x y z anchor, i32 byte length, records of i16 dx, i16 dy, i16 dz, u16 block); an undo goes on with `string edit` and SWAPS the world with the edit's journal (so undoing twice redoes). Strings are u16 length + UTF-8.

The plugin applies an edit on the main thread a slice a tick (`edit-budget-ms`, default 8), remembers what it overwrote (block data and tile entities: signs, chests) in a journal of up to 3,000,000 blocks, and marks the chunks it changed for the chunk updates above. Edits of 512 blocks or fewer update their neighbours (fences connect); bigger ones do not.

## Voice (web viewers speak and hear through Simple Voice Chat)

Needs the Simple Voice Chat plugin (a soft dependency; without it the bridge stays off). The plugin keeps a WebSocket open to the panel, `/api/live/voice/plugin/{serverId}` (same gate as the frames: loopback + `X-Live-Secret`), and the messages are binary frames, little-endian, a type byte first (`VoiceFrames.cs` is the reference):

- `1 Mic`, panel to plugin: `u16 speaker id, f32 x, f32 y, f32 z, u8 flags (1 whispering), Opus` (48 kHz mono, one 20 ms packet). The plugin gives each web speaker its own locational channel at that position and sends the packet through it, so players near that spot hear the viewer in the game.
- `3 Listeners`, panel to plugin: `u8` how many browsers listen (the plugin forwards players' voices only while it is above 0; it is sent on connect too).
- `4 Left`, panel to plugin: `u16 speaker id` of a browser that has gone; its channel is closed.
- `5 PlayerVoice`, plugin to panel: `u8 name length, name, f32 x, f32 y, f32 z, u8 flags, Opus` (a player speaking, from the microphone event). The panel relays it to every browser (as type 2) and the browser plays it spatialised from that position.

A browser's own socket (`/api/live/voice/ws?server=`, logged-in users) carries `1 Mic` (`f32 x, y, z, u8 flags, Opus`) up and `2 Voice` (`u8 name length, name, f32 x, y, z, flags, Opus`) down. Packets are capped (4096 bytes, 60 a second a speaker).
