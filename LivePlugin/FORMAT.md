# AubsCraft Live frame (format 1)

One frame is one binary body, little-endian, POSTed by the plugin to `{panel-url}/api/live/ingest/{server-id}` with the header
`X-Live-Secret`. The encoder is `src/com/spawndev/aubscraftlive/Frames.java`; the reader is `AubsCraft.Live.Model/LiveFrameCodec.cs`. They are
tested against each other (`AubsCraft.Admin.Server.Tests/LiveFrameCodecTests.cs`: the C# encoder must write the Java encoder's bytes exactly).

```
"ACL1"  u8 version (1)  i64 fullTime  i64 dayTime  u8 weather (bit0 storm, bit1 thunder)
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
