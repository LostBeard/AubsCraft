# AubsCraft Live for Fabric

The same live feed as `LivePlugin/` (Paper), for Fabric servers: it samples the world's entities every few ticks on the server thread and POSTs one
binary frame (`LivePlugin/FORMAT.md`) to the AubsCraft panel on the same machine, with a shared secret. `Frames.java` is the Paper plugin's own file
(Gradle compiles `../LivePlugin/src` without the Bukkit class), so the two cannot drift.

Config: `config/aubscraft-live.properties` (written by the panel when it installs the mod: panel-url, server-id, secret, interval-ticks, max-entities).

Build (JDK 21, Gradle 8.12+): `gradle build`; copy `build/libs/aubscraft-live-fabric-1.0.0.jar` to
`AubsCraft.Admin.Server/ServerPlugins/aubscraft-live-fabric.jar`. Mojang mappings (Loom remaps to intermediary); built for Minecraft 1.21.5, Fabric API 0.128.2.
Verified on a real Fabric server by `AubsCraft.Admin.Server.Tests/LivePluginFabricTests.cs`.
