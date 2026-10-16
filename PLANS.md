# AubsCraft Development Plans

Master checklist for the AubsCraft platform - admin panel, 3D world viewer, player tools, and VR.
All features listed are hard-set unless explicitly removed by Captain.
Updated: 2026-04-12

---

## Phase A: Admin Panel (COMPLETE)

- [x] A1. SignalR Hub (real-time server communication)
- [x] A2. Authentication (cookie auth, admin.json, PBKDF2-SHA256)
- [x] A3. Dark Theme (Bootstrap 5.3.3, no flash)
- [x] A4. Log Tailing (FileSystemWatcher + timer, Paper 1.21.5 format)
- [x] A5. Activity Log + Notifications (filterable timeline, toast alerts)
- [x] A6. Chat Panel + Extra Controls (gamemode, teleport, TPS graph)
- [x] A7. BlueMap Embed (iframe)
- [x] A8. GitHub Release Prep
- [x] A9. Plugin Management (read plugin.yml, enable/disable)
- [x] A10. Player Stats + World Stats + Client Detection

## Phase B: 3D World Viewer - Core (COMPLETE)

- [x] B1. WebGPU pipeline + WGSL shader (dual-light, fog, alpha-discard)
- [x] B2. HeightmapMesher (top faces + cliff sides, atlas UVs)
- [x] B3. MinecraftMeshKernel (ILGPU full 3D voxel meshing)
- [x] B4. Texture atlas (80+ textures, 16x16 grid, 256x256)
- [x] B5. FPS camera (WASD + mouse look, pointer lock)
- [x] B6. Frustum culling + adaptive draw distance
- [x] B7. SignalR heightmap streaming + full chunk loading

## Phase C: Viewer Polish - Session 2026-04-12 (COMPLETE)

- [x] C1. Fix +X face position bug (invisible block faces)
- [x] C2. Per-face textures for logs (all 8 types - bark on sides, rings on top/bottom)
- [x] C3. Per-face textures for grass_block (grass_top, grass_side, dirt)
- [x] C4. Per-face textures for podzol + mycelium
- [x] C5. Cross-shaped plant quads in ILGPU kernel (flowers, grass, fern)
- [x] C6. Skip plants in heightmap (clean loading view)
- [x] C7. Pointer lock fix (detect external focus loss, single-click recovery)
- [x] C8. SafeInvokeAsync fix (Dashboard + Activity Log regressions)
- [x] C9. RCON password restore (World Controls working)
- [x] C10. Transparent water - two-pass rendering (opaque + alpha blend)
- [x] C11. Seabed rendering (heightmap includes underwater terrain)
- [x] C12. IndexedDB WorldCacheService (schema, put/get, cache on stream)

## Phase D: Performance + Data Pipeline (IN PROGRESS)

- [x] D1. **JS-side binary WebSocket** - Replace SignalR with JS WebSocket via BlazorJS
  - Binary frames, data stays in JS, straight to IndexedDB
  - .NET only touches data when ILGPU kernel needs it
- [x] D2. **Radial loading from camera** - Chunks render outward from camera position
- [ ] D3. **Incremental updates** - Server sends only changed chunks (timestamp comparison)
- [x] D4. **OPFS region-file cache** - Replaced IndexedDB with OPFS (118 MB/s write, 310 MB/s read)
  - Region-batched files matching Minecraft's 32x32 chunk pattern
  - Benchmark proved: OPFS region is 107x faster writes than IndexedDB
- [ ] D5. **Offline mode** - Viewer works entirely from OPFS cache when server is down
- [ ] D6. **Fix vanishing chunks** - Never clear rendered data on server error
- [ ] D7. **Water grid alignment** - Heightmap water Y matches kernel water Y everywhere
- [ ] D8. **Vertex buffer management** - Dynamic sizing, logging when limits hit
- [x] D9. **Heightmap GPU kernel** - Replace CPU HeightmapMesher with ILGPU kernel
- [x] D10. **Time-of-day lighting** - Sun direction + ambient + fog from RCON time via SignalR push
- [ ] D11. **Persistent render worker** - Worker stays running across page navigation (singleton service)
  - Standby mode when not on viewer page, resume instantly when returning
  - Eliminates worker startup delay on page revisit
- [ ] D12. **In-viewer UI** - Console commands, player list, chat overlay inside the 3D viewer
  - Check Lost/LostSpawns and SpawnScene for existing UI components

## Phase V: Viewer on SpawnDev.VoxelEngine + SpawnDev.GameUI (TJ 2026-10-14: option 2)

Why: the viewer meshes with its own one-thread-per-block kernels (66 floats per face, no merging), reads every
mesh back to .NET (CopyToHostAsync + CPU section split + re-upload) and draws its own HUD as HTML. VoxelEngine
has greedy meshing, GPU-resident PackedQuad buffers, vertex pooling, culling and LOD; GameUI has the HUD.
Rule 2: every gap is fixed IN the library (VoxelEngine / GameUI), not worked around here.

- [x] V0. Baseline captured BEFORE any change: `AubsCraft.Viewer.Tests` (published AOT build, own Chrome, real world) -> screenshots/viewer/baseline-pre-voxelengine/ (7 shots + shots.json). Far field is NOT settled in 240 s (world streams over SMB), so compare the near field; the gate takes AUBS_VIEWER_TAG to name the next run
  - [x] V0b. Shoreline (08) and cave (09) shots added, found from the streamed chunk bytes so every build shoots the same camera; classic vs voxel in screenshots/viewer/cmp-classic and cmp-voxel: near-identical (the only difference is the far horizon, smoother seabed in the voxel one)
- [x] V1. GameUI runs in a worker: `GameUIService.Init(device, queue, format, w, h)` + `PushedInputProvider` (GameUI 1.1.0-local.1, pushed 18f98d6, 12 tests red-checked); proven in a real worker by V2
- [x] V2. GPU HUD in the viewer (stats panel, compass, crosshair while locked, hint, F3 debug overlay) drawn by GameUI in the render worker; the HTML stats line is now visually hidden (kept for screen readers and the gate). Gate: `ViewerHudTests` (same-scene `?hud=0` control; red-checked by running them with the HUD hidden: all 3 fail)
  - Needed SpawnJS 3.0.4-local.1: the record-constructor fix (RenderStats) was only on SpawnJS's v3 branch, never in a release - now on main (df5d5f7)
  - `/map?hud=0` hides the HUD (a clean frame for streaming and capture: D2.11/P4/P5)
  - [x] V2b. GameUI chat box (Enter opens it, fading feed otherwise, wrapped lines, sends via the page), player list, notices (join/leave/death/advancement), heading-up minimap (M zooms). HTML chat hidden on /map. Gate ViewerHudFeedTests 4/4 (`/map?hudfeed=demo`); GameUI 1.1.0-local.3 (chat feed options + the same-frame Enter bug)
- [x] V3. VoxelEngine textured pass: `BlockRenderPipeline` + `BlockTextureArray.FromAtlas` + `BlockMaterial` (VoxelEngine 0.2.0-local.1, 0117c6e): texture array with repeat (merged quads tile), per-face layers, tint, alpha cutout, opaque/plant/water passes, fog + lighting from caller-supplied uniforms, section placement by firstInstance. 18 WebGPU pixel tests, red-checked with 12 mutations
- [x] V4. VoxelEngine `ColumnMesher` (0.2.0-local.1, 7e3c28b): a whole column (24 sections) meshed on the GPU in a handful of dispatches, class-aware (`BlockClass`: opaque / leaves+glass / water / plants), exact across column borders, one tiny counter readback, column straight from a JS Uint8Array (`CreateColumnBufferFromJS`). 12 tests vs a brute-force CPU reference on WebGPU + CPU lanes; 16 single mutations: 15 killed, 1 equivalent
  - [x] V4b. Server: GlobalBlockRegistry (persisted, save failures never fail a request), /api/world/blocks, /api/world/chunk-ids, WS ?format=ids; client: VoxelBlockTables, JS ingest into ColumnMesher/HeightmapMesher
- [x] V5 (f8710fc, deployed; gate: 7 voxel shots pass, near field = V0 baseline, 84-147 FPS, screenshots/viewer/voxel2; far field missing until V6; no cave culling): `VoxelWorldRenderer` (columns resident, ColumnMesher, BlockRenderPipeline, depth32float reversed-Z, frustum per section, water back to front) behind `/map?renderer=voxel`; `DayLighting` shared with the old renderer; `GET /api/world/chunk-ids`. Not done: cave/visibility culling (frustum + distance only), far tier (V6), lighting parity check vs baseline. Original V5 text: Section pool: VertexPool + IndirectDrawBuffer + CullingPipeline (frustum + cave graph) replace MapRenderService slots
- [x] V6. Far tier: VoxelEngine `HeightmapMesher` (0.2.0-local.3, a0f48f2; 10 tests WebGPU+CPU, 21 mutations all killed) + server `/api/world/ws?format=ids` + VoxelWorldRenderer far chunks (9747ccd, deployed). Gate screenshots/viewer/voxel3: 7 shots, far land + fog at the horizon, 43-84 FPS (baseline 62-90). Open: pool far-chunk buffers (one bind per chunk per pass is the CPU cost), cave/visibility culling, mid-range LOD not needed (columns are greedy-merged)
- [x] V7. Cut over (10-14): MinecraftMeshKernel/HeightmapMeshKernel/MinecraftLODKernel/CaveFillKernel/ChunkMesher/old HeightmapMesher/SectionVisibility/OrbitCamera/VoxelEngineService/ChunkStreamService and the heightmap OPFS cache deleted; MapRenderService is the 250-line frame loop; classic switch gone. Gate on the cut-over build: HUD 3/3, HUD feed 4/4, 9 shots, 141-210 FPS (the old viewer: 43-90). Known limits: no cave/visibility culling (frustum + distance only), no OPFS cache (offline mode is D5)

- [x] V8 (10-15): fewer calls on weak CPUs. VoxelEngine `QuadArena` (0.2.0-rc.2-local.2: columns AND far chunks share a few 16 MiB buffers; real binds counted by `BlockRenderPipeline.QuadBufferBinds`) and `IngestGovernor` (meshes wait for a per-frame turn by frame time; neighbour re-meshes coalesced and last). TJ on an Intel UHD 620: 837 draws/837 binds, 37 fps, 17.6 of 19.1 ms recording. Dev machine, same world, 100 s after open (screenshots/viewer/load-before vs load-after2): 2.8K vs 4.6-5.2K columns+far loaded, ~125 vs ~190 fps, record 6.5 vs 4-5 ms, binds 1541 vs 45 (of ~1790 draws). TJ's Intel 620 paste after V8a: 3621 draws at 14 us each (51.9 ms recording), 7365 columns+far loaded, 17382 meshes: most of it beyond the fog. V8b (10-15): fog-distance cull (30 chunks, frame cleared to the fog colour) + far meshes only within a window around the camera (`FarWindow`). Dev machine, same world, 100 s after open (load-far2): 7.3K columns+far loaded, 4.5K meshes (was 9.6K), ~240 fps, record 3.1 ms; all 9 shots now settle in 41-54 s (they hit the 150 s cap before); 0.00% changed pixels vs the previous accepted shots in 5 compared views; night sky checked (`?time=ticks` link, ViewerSkyTests). Gate 24/24. V8c: TJ's 620 after V8b: 42 fps, record 13.4 ms (3.9x less), 1652 draws, 4751 meshes, 0 waiting; now GPU-bound (780K quads), so the draw distance adapts (57/48 fps band, 12..30 chunks) and the fog follows it (dd=14 shows 4x the fog-coloured area of dd=30; full-distance shots 0.00% changed). Gate 25/25. V8d: TJ's 620 settled at 52 fps, 625 draws, but record 9.7 ms = 15 us/draw (it was 8 at 1652 draws): a fixed ~4-5 ms per frame, the walk over all 7,300 columns+far chunks (hash lookups + totals). Candidate list + running totals (gather 0.0-0.1 ms on the dev box; same 1541 draws; 9 of 9 shots 0.00% changed; gate 26/26). Not measured on the Intel 620 itself. Next lever if still slow: render bundles or batching draws in JS (draws are ~2.5 us each here, ~14 us on the 620)

## Phase W: World fidelity - real textures, animation, live activity (started 2026-10-15, TJ: "we need better texture accuracy, animations, and live activity like seeing animals and players going about their play")

MEASURED BASELINE (240 block names seen in the dev world sample, `block-registry.json`): 103 have a texture, 135 (56%) are flat colours; 74 are not full
cubes (stairs, slabs, fences, doors, trapdoors, beds, panes, torches, lanterns, rails, ladders, chests, campfires, candles...) and are drawn AS CUBES; tint is
one constant colour (no biomes); nothing animates; no entities at all. Today's atlas is hand-curated (89 PNGs, 80 used, mapped by name in
`TextureAtlas.cs`). The client jars have what is needed: 26.3 has 1,289 blockstates, 2,884 block models, 1,405 block textures, 105 animated (`.mcmeta`),
869 entity textures and the grass/foliage colormaps. The server has NO live entity feed (players' positions only, through RCON `data get entity`).

### W-T: Texture accuracy (asset-driven, client-side: nothing to install)
- [x] WT1. **Asset pipeline**: a tool (`AubsCraft.Assets`: C# library + CLI, tested) reads the client jar: blockstates + models (parent chain, texture variables, rotations,
  elements) and writes the texture array (all ~1,400 block textures = ~1.5 MB) + `blocks.json` (state -> class, per-face layer, tint kind, animation, model). Replaces the
  hand-made atlas and `TextureAtlas.GetFaceLayers`. Gate: every block name in the world resolves; the report lists what does not.
- [x] WT2. **Cube-model blocks all correct**: per-face layers for the 135 flat names that are cubes (ores, wool, copper, glass, furnace, barrel...), cutout/transparent
  classes from the model's render type, `tintindex` from the model.
- [ ] WT3. **Block states in the registry**: the server's global id is the NAME only; stairs/slabs/doors/fences/panes need their PROPERTIES (facing, half, shape,
  open, connections, waterlogged). Registry becomes rendered-state ids (12-bit = 4096 states: fits a normal world; measured per world), chunk-ids wire unchanged.
- [ ] WT4. **Non-cube shapes**: VoxelEngine `ModelMesher` (element boxes with per-face UV/cullface/rotation into the same quad stream), stairs/slabs first, then
  fences, walls, panes, doors, trapdoors, torches, ladders, rails, beds, chests, lanterns, candles, flower pots. Greedy merge stays for cubes.
- [x] WT5. (DONE 10-16, gated; deploy pending) **Biome tint**: the server reads the biome of each 4 x 4 block cell of a column (top section with blocks, layer 1) and sends it in an `X-Biomes` header and 16 bytes after the far-tier heightmap (`HeightmapWire`); the AssetTool computes every biome's grass/foliage/water colour from the colour maps + the biome table (`BiomeData`, 65 vanilla biomes, `mc-biomes.json`); VoxelEngine `BiomeTintMap` (three wrapping 256x256 maps, a texel per 4x4 blocks, linear filtered so borders blend) is read per PIXEL by blocks tagged TintGrass/TintFoliage/TintWater. Unknown (modded) biomes are plains. LEFT: the swamp grass noise (always 6A7039), frozen-ocean temperature modifier, tint where a column's top section is not the surface (a floating island over a different biome).
  (a per-column tint map in a storage buffer, indexed by the column slot). Water colour per biome (swamp, warm ocean...).
- [ ] WT6. Resource packs (stretch): the same pipeline on a pack's assets; 32x/64x textures need per-layer size (texture array of one size: resample or mip packs).

### W-A: Animation
- [x] WA1. **Animated block textures** (water, lava, fire, soul fire, nether portal, magma, sea lantern, prismarine, kelp, seagrass, command blocks...): frames as layers,
  `BlockMaterial` gains (frame count, frame time, interpolate); `BlockFrameUniforms` gains `time`; the shader picks the layer. VoxelEngine change + tests.
- [~] WA2. Water/lava surface: flowing direction, small wave; (DONE 10-16: light-emitting blocks - lava, glowstone, sea lanterns, shroomlights, froglights, torches, lanterns, fire, lit lamps/campfires, portals... - are drawn at full brightness at night: VoxelEngine `BlockMaterial.FlagEmissive` (local.12), `EmissiveBlocks`; gate `ViewerNightTests`. REAL LIGHT DONE 10-16: the server reads each column's sky + block light (`/api/world/chunk-light`, 98,304 bytes, packed on the wire; Paper's Starlight writes it with isLightOn 0 and leaves out uniform sections), VoxelEngine `LightVolume` holds the columns of a 17 x 17 window round the camera (local.14), the block shader lights each face by the cell in front of it (the game's brightness curve, warm block light, a dark floor of 0.3 where no sky reaches), `LightField` fetches the columns; a Lighting On/Off button on the Map page (remembered; `?light=0|1`); gates `ViewerLightTests`, PMT `Light_*`, `LightTests`.)
- [~] WA3. Sky: sun/moon/stars/clouds (E2), weather particles and tint (E3). (SKY DONE + deployed 10-16, gated on the scene world: VoxelEngine `SkyPipeline` draws one full-screen triangle FIRST in the world pass (gradient horizon(fog colour)->zenith, glow round the sun, the game's sun and moon squares with the moon in its phase from the server day number, stars turning with the sun, the game's cloud map at y 192.33 drifting 0.36 blocks/tick from the server clock, grey and everywhere in rain); `DayLighting.SkyAt`, `SkyLayer`, `SkyAtlasBuilder` (mc-sky.json/rgba); not drawn underwater or in AR. Gate `ViewerSkyLookTests` (noon gradient, midnight stars, dusk sun), `SkyLayerTests`, `SkyAtlasTests`. RAIN AND SNOW DONE 10-16 (fixed 10-16 after TJ saw it stand still: it moved with the SERVER clock, and the Paper plugin sent getFullTime() = DAY time, which stops with the day cycle; now the plugin sends getGameTime(), the rain and clouds move by the viewer's own clock, and LiveWorldState tolerates a standing clock): VoxelEngine `WeatherPipeline` (a camera-facing quad per column of a 24x24 window from the ground to 12 blocks above the camera, the game's rain/snow textures sliding down), the server sends each column's MOTION_BLOCKING heightmap (`X-RainHeights`), `WeatherMap` picks rain/snow/none by biome temperature; gate `ViewerWeatherTests`. LEFT: cloud height/colour per dimension, sun/moon lighting the clouds, rain splashes/ripples, snow settling, thunder flashes, rain fog.)

### W-L: Live activity (players and animals going about their play)
- [x] WL1. (DONE 10-16: plugin source+jar, frame format, panel ingest/fan-out/installer/WS, viewer client; the plugin and the Fabric mod now also send items, falling blocks, paintings, frames, armor, regional variants, swing/use flags; TJ presses Install/Update + restarts to get them; a real Paper run is the scene builder) **Feed**: a small Paper plugin `AubsCraft Live` (Java 21, built with `javac` against paper-api; jar installed through the panel's plugin manager) pushes, ~5-10 Hz,
  every entity in the loaded chunks near players: uuid, type, position, yaw/pitch/head yaw, velocity, pose, baby/variant/colour, held item, custom name; for players name,
  uuid and skin URL. Loopback WebSocket with a shared secret -> panel -> browsers (binary deltas, ~16 bytes an entity). NEEDS TJ'S GO to install on the live servers.
  Without it, the stepping stones: players via the existing RCON positions (1 Hz), animals from the `entities/` region files (static, stale between saves).
- [x] WL2. (DONE 10-16: EntityRenderPipeline, entity atlas, every living creature of 1.21.5 modelled (the game's own models for ~100), boats, minecarts, armor stands, arrows, armor on players) **Entity renderer** (VoxelEngine `EntityRenderPipeline`: instanced model parts, per-part UV into the entity textures): player (biped + skin + second layer), zombie,
  skeleton, creeper, cow, pig, sheep, chicken, villager, horse, wolf, cat, fox, rabbit, bat, spider, enderman, slime, fish, then a textured-box fallback with a name label.
- [x] WL3. (DONE 10-16: interpolation, walk cycle, head look, wing flap, sneak/ride/swim/glide/sleep/dead stances, swing, bow, eating, held items; LEFT: hurt flash) **Motion**: interpolate 5-10 Hz snapshots to the display rate (render ~150 ms behind), walk cycle from speed, head look, sneak/sleep/swim poses, hurt flash.
- [x] WL4. (DONE: skins via the panel proxy, name tags, minimap markers, follow camera, player list actions; LEFT: chat bubbles) Player skins (server-side fetch + cache of the profile texture URL), name tags, chat bubbles, minimap markers (players, animals; toggles), follow-camera on a player.
- [~] WL5. Item entities (DONE 10-16: dropped items drawn as the game does, `ItemLayer` + `ItemAtlasBuilder` (mc-items.json, 1394 items: flat sprites + block cubes from the item models), the plugin and the Fabric mod send item entities with the item name as variant (`include-items` now true, `max-items` 500; TJ presses Update/Install on the Plugins page to rewrite the config + jar); gate `ViewerItemsTests`; LEFT: stack counts, enchant glint, tinted potions/eggs), boats/rafts/chest boats, minecarts, armor stands (DONE 10-16: the game exact models from tools/modeldump (filter now keeps Boat/Raft/Minecart/ArmorStand), `EntityModel.ModelYawDegrees`, gate `BoatsMinecartsAndStands_AreDrawn`; armor stands have no arms/armour/pose, minecarts no cargo block, paddles at rest), falling blocks + primed TNT (DONE 10-16: whole-block cubes from the item atlas; plugin/mod send the block as variant), paintings + item frames (DONE 10-16: `ItemAtlasBuilder` packs the 51 paintings at their own sizes and the two frames, `ItemLayer.AddHanging`; the plugin/mod send the painting name and the frame's item; gate `PaintingsAndItemFrames_HangOnTheWall_FacingOut`; LEFT: frame item rotation, maps in frames, glow frame light), arrows + thrown things (DONE 10-16: the game's ArrowModel flown along its yaw/pitch, snowballs/eggs/pearls/fireballs/potions/rockets as camera-facing item sprites; LEFT: tridents, wither skulls, llama spit, shulker bullets, potion colours, tipped arrows), end crystals. PLAYER ACTIONS (DONE 10-16: `EntityPoser.Stance` - sneaking, riding (sitting), swimming, gliding, sleeping along the bed under them, dead on their side; the right arm raised to hold an item and the held item drawn in the hand with the game's own display transforms (`ItemLayer.AddHeld`); players are drawn at 15/16 as the game does; gate `ViewerStanceTests`; SWINGING / USING (DONE 10-16: two new frame flags, 16384 swinging from PlayerAnimationEvent / `swinging`, 32768 using from `isHandRaised` / `isUsingItem`; the game's own swing, a drawn bow, eating); ARMOR DONE 10-16 (a player's variant says `helm|chest|legs|boots` as materials, leather as leather#rrggbb; `ArmorLook` draws the game's armor boxes (inflate 1.0 / 0.5) in the equipment textures; no frame format change; gate Players_WearTheirArmor); LEFT: blocking with a shield, elytra, armor on zombies/skeletons, trims, enchant glint, left-handed players, the other hand). (PARTICLES DONE 10-16: `ParticleRenderPipeline` in VoxelEngine, analytic from the clock; campfire/soul campfire/furnace/fire/soul fire/torch/redstone torch/candle show smoke + flames, the server names the emitting blocks in `X-Emitters`. LEFT: rain/snow (needs weather), lava pops, explosions/hits/splashes from the live feed, end rods, bubbles, wall torch position by facing, signal smoke over hay.)

### Reported by TJ 10-15 (live viewer), not yet handled
- [ ] Rails draw wrong: they should be a thin layer on top of the block they lie on (shape meshing, WT4).
- [ ] A top water block that also holds a plant (seagrass, kelp, waterlogged) does not draw its water (the class is the plant's, the water is lost).
- [ ] Fish (cod, salmon, tropical, pufferfish, squid) are coloured boxes: need their models + a swim rig.
- [x] Smoke / particles (campfire, furnace, fire, torches) render (WL5, 10-16).
- [ ] Intel 620: creatures cost 14.4 ms a frame with 531 drawn (4,794 parts): frustum culling + head-and-body-only beyond 40 blocks added; re-measure.

### TJ 10-15 (later): the viewer must be a complete spectator client
- [x] WU. (DONE + deployed 10-15; eye probe bug fixed: odd-index ushort copy) **Underwater looks underwater**: camera in water -> blue fog, short visibility, tint; the water surface seen from below (double-sided water pass). Verify with the scene tool.
- [ ] WP. **Player list actions + follow camera** (TJ 10-15: clicking a name only captured the mouse). A click on a name opens a menu (Go to / Follow / See through their eyes / Stop following); the click no longer takes the pointer (`GetHudClickRectsAsync`: the list's rectangle, the whole view while a menu is open). Follow = chase camera (mouse orbits) or the player's eyes; moving yourself, or the player leaving, ends it. `FollowCamera` + `LiveWorldState.TryFindPlayer/SampleOne` unit tested; gate `ThePlayerList_ClickOpensAMenu_...`. Open: wheel zoom while following, a follow toggle from the minimap/name tag, other dimensions.
- [~] WM. (10-16: ~95 creatures modelled; the models now come EXACT from the client jar via tools/modeldump (`mc-models.json`, `GameModels`, `EntityModels.FromGame.cs`): bat, phantom, goat, panda, camel, sniffer, armadillo, hoglin/zoglin, strider, ravager, warden, creaking, frog, tadpole, guardian/elder, shulker, vex, allay, breeze, wither, dragon, giant, axolotl, fish whole (cod, salmon, pufferfish, tropical A/B), and the parrot/dolphin/silverfish/bee/cow corrected from it. LEFT: variants from the plugin (panda gene, axolotl colour, frog type, shulker colour, strider cold, hoglin->zoglin conversion), mobs the dump has no class for (wolf, cat, horse, villager, zombie/skeleton family, enderman: HumanoidModel builders), baby heads, animations beyond walk/head (wing flap, hop, tentacles), pose changes (sit, sleep, swim), wither armour, dragon animation.)
- [x] WT4. (DONE 10-15, deployed: VoxelEngine ShapeTable/ShapeKernel, assets keep models+rules, ShapeResolver per state, 428 shaped blocks; far tier still draws them as cubes; element rotation (torch tilt) ignored; beds/chests/signs/banners are entity-rendered in vanilla and stay cubes) Shape meshing: stairs, slabs, doors, fences, gates, panes, walls, trapdoors (open), ladders, torches, buttons, beds, chests, lanterns, candles, flower pots ... needs block states beyond today's picture states (facing/half/shape/open/connections) in the registry.
- [x] WB. (10-16, gated on the scene world; deploy pending) **Block entities drawn as the game draws them**: chests (single/double, trapped, ender), beds (all dyes, both parts, 4 facings), shulker boxes (all dyes, 6 facings), standing/wall/hanging signs, standing/wall banners (base colour), skulls (skeleton, wither skeleton, zombie, creeper, player). Until now each was a cube of its particle texture (a sign was a planks block). The server reads them from the chunk's RAW palette while parsing (`BlockEntityKinds.Classify`: name + facing/type/part/rotation/attached), sends them in an `X-BlockEntities` header (7 bytes each, 500 a column); `BlockEntityLayer` poses the ones near the camera with `BlockEntityModels` (the game's own geometry from `tools/modeldump` under the moves each renderer makes, read from the renderers' bytecode with javap) and `EntityLayer` draws them with the creatures; `VoxelBlockTables` gives the blocks class None. SIGN TEXT DONE 10-16 (front only: the server reads block_entities front_text -> `X-SignText`, `SignTextAtlas` draws each distinct writing into a slot band of the entity atlas with canvas text, `PoseSignText` puts a no-depth quad on the board; dye darkened to 0.4, glow outlined; gate `ViewerSignTextTests`, `RealWorldSignTextTests`; LEFT: back text, a game-like pixel font, hanging signs unseen in a gate). BANNER PATTERNS DONE 10-16 (`X-BannerPatterns`, 43 pattern masks, `PoseBannerLayers`; gate be-banners shots). LEFT: shield-style banners on items, open lids (chest/shulker animation state), bell, enchanting table book, decorated pot, conduit, lectern, piglin/dragon heads, cauldron contents, item frames/paintings/armor stands, the double chest of trapped chests joined across a type change.
- [~] WV. **VR and AR** in the viewer (10-16: BUILT + gated with a mock WebXR device; NOT yet tried on a real headset - TJ's Quest). WebXR cannot run in a worker, so a session draws the world on the PAGE thread: `Xr/XrViewerHost` makes a second `RenderWorkerService` (`ManualFrames`, streams columns around the head, no loop) and draws each eye with WebGPU (`ViewSetup.FromXr`, `XrRenderEye`) into a bridge OffscreenCanvas, copied to the headset's WebGL layer by `WebGlXrBlit`; the worker's own loop rests (`SetPausedAsync`) and resumes at the head's place (`SetCameraAsync`). Left stick flies along the head, right stick snap-turns / rises, a grip is 6x, B/Y leaves. AR: `immersive-ar`, clear alpha 0 (VoxelEngine `BeginRenderPass(clearAlpha)`, 0.2.0-rc.5-local.7), premultiplied bridge canvas; distant fog is still opaque fog-coloured. Gate: `ViewerXrTests` (mock `navigator.xr`: two eyes, stick, exit; sky transparent in AR), `XrTests` (pure math). LEFT: HUD/menus in VR (GameUI world-space), hand-controller pointing, real-headset frame rate (the bridge canvas follows each eye's viewport size), the desktop canvas holds its last frame while the headset is on, AR floor/anchor placement, the eyes straddling a chunk border rebuild the candidate list twice a frame.
- [ ] Fish models + swim rig, particles (smoke, fire, rain).

Order: WT1 + WA1 first (biggest visible change, client only), then WL1 stepping stones (RCON players) + WL2, then WT3/WT4/WT5.

## Phase D2: Input System + UI Layout

- [ ] D2.1. **Unified InputState** - Shared state that all input modes write to, camera/game reads from
- [ ] D2.2. **Gamepad support** - Gamepad API polling, standard mapping, dead zones
  - Left stick: move, Right stick: look, Triggers: speed/interact
  - D-pad: camera mode switching, Face buttons: interact/menu/edit
- [ ] D2.3. **Auto input detection** - WebXR -> VR, Gamepad -> controller, Touch -> mobile, default -> PC
- [ ] D2.4. **Mobile touch controls** - Virtual joystick (left thumb), gestures (pinch/drag/tap)
- [ ] D2.5. **Collapsible sidebar** - Tab key or hamburger, fullscreen mode hides entirely
- [ ] D2.6. **Keyboard shortcuts** - Tab (players), M (minimap), C (coords), T (chat), F2 (screenshot), F5 (camera mode)
- [ ] D2.7. **Gamepad UI cursor** - Virtual cursor via right stick when not in pointer lock
- [ ] D2.8. **Adaptive button prompts** - Show keyboard/gamepad/gesture icons based on active input
- [ ] D2.9. **HUD elements** - Compass, coordinates, minimap corner, FPS counter
- [ ] D2.10. **Fullscreen mode** - Two levels: full-page (hide admin UI) + browser fullscreen (hide browser chrome)
  - Minimal floating HUD overlay (FPS, coords, compass, exit button)
  - Auto-hide HUD after 3s inactivity, show on mouse move
  - F11 cycles: normal -> full-page -> browser fullscreen -> normal
  - F key reserved for game actions (interact/fly toggle/etc.)
  - Escape: releases pointer lock first, then exits fullscreen (matches Minecraft)
- [ ] D2.11. **Streaming/OBS mode** - URL param `?stream=true` for clean capture
  - No HUD, configurable resolution, optional overlay elements
  - OBS Browser Source compatible
- [ ] D2.12. **PWA mobile** - Full-screen, orientation lock, touch-action:none on canvas

## Phase E: Viewer Visual Features

- [ ] E1. **Time-of-day lighting** - Sun direction + ambient from RCON time query, animated cycle
- [ ] E2. **Day/night skybox** - Sky color changes with time, stars at night
- [ ] E3. **Weather rendering** - Rain/snow particles, sky color changes
- [ ] E4. **Entity rendering** - Animals, villagers from server entity data
- [ ] E5. **Per-face textures: remaining blocks** - Pumpkin, melon, TNT, furnace, crafting table, hay, bone
- [ ] E6. **Biome-accurate tinting** - Read biome data, apply correct grass/leaf/water colors per biome
- [ ] E7. **Block lighting/ambient occlusion** - Darken corners and crevices for depth

## Phase F: Map Navigation + HUD

- [ ] F1. **Compass HUD** - N/S/E/W directions on screen
- [ ] F2. **Coordinates display** - Camera X/Y/Z like the game's F3 screen
- [ ] F3. **Minimap** - Small top-down view in corner showing position on full map
- [ ] F4. **Biome labels** - Hover over terrain to see biome name
- [ ] F5. **URL deep-linking** - `?x=228&z=-243&zoom=5` opens directly to a location
- [ ] F6. **Top-down screenshot mode** - Orthographic camera, export shareable PNG
- [ ] F7. **Spawn marker** - Always-visible marker at world spawn
- [ ] F8. **Death markers** - Skull icons where players died (from log parsing)
- [ ] F9. **Bed markers** - Player respawn points (from Essentials data)
- [ ] F10. **Nether portal markers** - Highlight active portals linking dimensions

## Phase G: Multi-User Auth + Admin Levels

- [ ] G1. Multi-user accounts (replace single admin.json with user list)
- [ ] G2. Admin levels/roles (owner, admin, moderator, viewer)
- [ ] G3. Admin management page (create users, set levels from website)
- [ ] G4. Role-based endpoint authorization (dangerous ops require higher level)
- [ ] G5. SignalR hub method authorization by role

## Phase H: Account Linking (MC Character + Web Account)

- [ ] H1. **AubsCraftLink Paper plugin** - `/link <code>` command, validates via server API
- [ ] H2. **Web verification flow** - Create account, get code, link in-game
- [ ] H3. **Bedrock/Geyser support** - Handle Floodgate UUIDs for Bedrock players
- [ ] H4. **Microsoft OAuth option** - Xbox Gamertag to MC UUID mapping
- [ ] H5. **Profile commands** - `/profile`, `/profile public`, `/profile private`, `/unlink`

## Phase I: Golden Shovel Claim Viewer

- [ ] I1. **AubsCraftClaims Paper plugin** - Wraps GriefPrevention API, exposes claims as JSON
- [ ] I2. **Real-time claim events** - Plugin pushes create/resize/delete via WebSocket
- [ ] I3. **3D claim rendering** - Semi-transparent colored volumes for claim boundaries
- [ ] I4. **Click-to-fly** - Click any claim to fly the camera there instantly
- [ ] I5. **Claim info panel** - Owner, size, trust list, creation date, last activity
- [ ] I6. **Neighborhood view** - See who's near your base
- [ ] I7. **Admin claim tools** - Expired/abandoned highlights, one-click removal, overlap detection
- [ ] I8. **Claim analytics** - Size rankings, activity heatmap

## Phase J: Player System on the Map

- [ ] J1. **AubsCraftTracker Paper plugin** - Real-time player position broadcasting
- [ ] J2. **Player list overlay** - Sidebar with all online players, click to fly to them
- [ ] J3. **Player skin rendering** - Skin head icons or simple avatars at positions
- [ ] J4. **Position interpolation** - Smooth movement between RCON position updates
- [ ] J5. **Direction indicator** - Show which way each player is facing
- [ ] J6. **Activity indicator** - Idle, mining, building, fighting, AFK status
- [ ] J7. **Essentials warp markers** - Read warp YAML, show labeled markers on map
- [ ] J8. **Player last-logout markers** - Last position from Essentials userdata

## Phase K: Web-to-Game Chat

- [ ] K1. **AubsCraftChat Paper plugin** - Web-to-game chat bridge via WebSocket
- [ ] K2. **[Web] prefix** - Messages from website show with [Web] tag in-game
- [ ] K3. **Private messages** - `/msg <player>` from web
- [ ] K4. **Location sharing** - "I'm at x=228, z=-243" with clickable camera-fly link
- [ ] K5. **Screenshot sharing** - Capture viewer screenshot, share as clickable thumbnail in chat

## Phase L: Browser Creative Mode (Base Editing)

- [ ] L1. **AubsCraftBuild Paper plugin** - Receives block change commands, validates permissions, applies
- [ ] L2. **Edit Mode UI** - Block palette, place/remove tools, undo/redo
- [ ] L3. **Raycast block targeting** - Click to place on target face, right-click to remove
- [ ] L4. **Ghost block preview** - Transparent/outlined blocks before committing
- [ ] L5. **Claim boundary enforcement** - Server-side validation, players can only edit own claims
- [ ] L6. **Admin override** - Admins can edit anywhere, changes apply immediately
- [ ] L7. **Material cost tracking** - Deduct from player inventory in survival mode
- [ ] L8. **CoreProtect logging** - All web-placed blocks logged for rollback
- [ ] L9. **Submission queue** - Changes queue and apply when player is online (or immediately for admin)
- [ ] L10. **Rate limiting** - Max N blocks per submission for safety

## Phase M: Public Player Profiles

- [ ] M1. **Profile pages** - `map.spawndev.com:44365/player/<name>`
- [ ] M2. **Stats display** - Play time, blocks placed/broken, deaths, achievements
- [ ] M3. **Base gallery** - Player's builds with screenshots
- [ ] M4. **Build showcase** - Featured builds with flyover camera paths
- [ ] M5. **Privacy controls** - Public/private toggle, opt-in only
- [ ] M6. **Achievement tracking** - Read advancement data, display progress

## Phase N: AI Villagers

- [ ] N1. **AubsCraftAI Paper plugin** - Spawns intelligent NPC villagers at designated locations
- [ ] N2. **Text chat interface** - Click villager in viewer, type messages, get AI responses
- [ ] N3. **Voice chat** - Browser microphone, speech-to-text, AI responds, text-to-speech
- [ ] N4. **Villager personalities** - Town Crier, Cartographer, Historian, Quest Giver, Lore Master
- [ ] N5. **World knowledge** - Villagers know server state (who's online, recent events, builds)
- [ ] N6. **CoreProtect integration** - Historian villager can tell you who built what and when
- [ ] N7. **NotQuests integration** - Quest Giver villager ties into quest plugin
- [ ] N8. **Persistent memory** - Per-villager conversation history across sessions
- [ ] N9. **Claude API backend** - AI responses powered by Claude via SpawnDev claude-api

## Phase O: Multi-Dimension Maps

- [ ] O1. **Dimension switcher UI** - Tabs for Overworld, Nether, End
- [ ] O2. **Nether rendering** - Red/orange fog, lava rivers, ceiling, nether block palette
- [ ] O3. **End rendering** - Black/purple void, floating islands, end stone, chorus plants
- [ ] O4. **Portal markers** - Nether portal locations linking dimensions
- [ ] O5. **Same RegionReader** - Nether at `world/DIM-1/region/`, End at `world/DIM1/region/`

## Phase P: Spectator Cam + Drone Cam + Streaming

- [ ] P1. **Spectator mode** - Click a player in the list, click "Spectate", camera follows them in 3rd person
  - Player position/direction streamed in real-time from AubsCraftTracker plugin
  - Configurable camera offset (distance, height, angle behind player)
  - Smooth interpolation between position updates
  - The viewer renders independently - zero performance impact on the player's game
  - Perfect for streaming: Aubs plays in VR, spectator cam streams to Twitch/YouTube
  - OR: guests visit the site directly and spectate live, no stream needed
  - Each viewer picks their own camera angle, renders locally from position data
  - Only a few bytes/sec of position data transmitted - scales to unlimited viewers
  - Player privacy: spectate requires player opt-in (toggle in profile settings)
  - Camera auto-loads chunks around the followed player as they move
- [ ] P2. **Camera modes** - Free fly, orbit, follow/spectate, cinematic waypoint, top-down
- [ ] P3. **Waypoint recording** - Record camera path, play back in loop (cinematic mode)
- [ ] P4. **OBS Browser Source mode** - Configurable resolution, no UI chrome, overlay options
- [ ] P5. **URL parameters** - `?spectate=HereticSpawn&hideUI=true&resolution=1080p`
- [ ] P6. **WebRTC live streaming** - captureStream() to peer connection via SpawnDev.RTLink
  - One viewer runs spectator cam, others watch as a live video feed without rendering
- [ ] P7. **Stream overlay** - Optional on-screen info: player name, coordinates, server name, player count

## Phase Q: WebXR VR Mode

- [ ] Q1. **SpawnDev.BlazorJS WebXR wrappers** - New addition to the SpawnDev ecosystem
- [ ] Q2. **VR rendering** - WebGPU output to VR display instead of flat canvas
- [ ] Q3. **Hand controller navigation** - Fly mode with hand controllers
- [ ] Q4. **VR interaction** - Point at claims, players, blocks to interact
- [ ] Q5. **God mode admin** - Float above world, see everything, manage by pointing
- [ ] Q6. **Scale model** - Shrink world to tabletop size for close examination
- [ ] Q7. **VR base building** - Place/remove blocks with hand controllers in your claim
- [ ] Q8. **Voice commands in VR** - "Show claims", "fly to spawn", "kick player X"
- [ ] Q9. **Mixed reality (AR)** - Overlay Minecraft map on real desk (Quest 3 passthrough)

## Phase R: Inventory + Advanced Player Tools

- [ ] R1. **AubsCraftInventory Paper plugin** - Web inventory viewing/management API
- [ ] R2. **Inventory viewer** - See your items from the web
- [ ] R3. **Inventory management** - Move items, organize chests (within claim)
- [ ] R4. **Crafting planner** - Plan crafting recipes, see material requirements
- [ ] R5. **Enchantment viewer** - See enchantments on items
- [ ] R6. **Block counter** - Click a block type, see count in current view

## Phase S: Data Analytics + Admin Intelligence

- [ ] S1. **CoreProtect history viewer** - Click any block to see change history, who/when/what
- [ ] S2. **Player activity heatmap** - Where do players spend time? (CoreProtect data)
- [ ] S3. **Claim activity analytics** - Active vs abandoned claims
- [ ] S4. **Block placement statistics** - Most used blocks, building patterns
- [ ] S5. **TPS correlation** - What activities cause TPS drops?
- [ ] S6. **Player retention metrics** - Who's active, who stopped playing, when

---

## Paper Plugins Required

| Plugin | Phase | Purpose | Status |
|--------|-------|---------|--------|
| VRDetect | A | VR player detection | DONE |
| AubsCraftLink | H | Account linking (verification codes) | Planned |
| AubsCraftClaims | I | GriefPrevention claim data API | Planned |
| AubsCraftTracker | J | Real-time player position broadcasting | Planned |
| AubsCraftChat | K | Web-to-game chat bridge | Planned |
| AubsCraftBuild | L | Browser creative mode block placement | Planned |
| AubsCraftAI | N | AI villager NPCs | Planned |
| AubsCraftInventory | R | Web inventory viewing/management | Planned |

All plugins: Java 21, Paper API 1.21.5, communicate with AubsCraft server via HTTP/WebSocket.

---

## Architecture

### Data Flow (target)
```
Server --[binary WS]--> JS WebSocket --[stays in JS]--> IndexedDB cache
                                                              |
                                         .NET <--[on demand]--+
                                           |
                                     ILGPU kernel --> WebGPU --> render
```

### Key Rules
- **Performance is THE feature** - every decision through the lens of "does this make it faster?"
- **ILGPU kernel for ALL mesh generation** - no CPU fallbacks
- **Data stays where it belongs** - no unnecessary .NET/JS round-trips
- **Deploy after EVERY change** - edit, deploy, wait for feedback
- **Client-side processing** - server is a data source, client GPU is the compute engine
- **JS for I/O, .NET for logic, GPU for compute** - each sandbox for its strengths

### SpawnDev Ecosystem Showcase

| Feature | SpawnDev Tech |
|---------|--------------|
| 3D rendering | SpawnDev.ILGPU (WebGPU kernel) |
| JS interop | SpawnDev.BlazorJS (IndexedDB, WebSocket, WebXR, fetch) |
| WebRTC streaming | SpawnDev.RTLink |
| AI integration | Claude API via BlazorJS |
| Offline caching | SpawnDev.BlazorJS IndexedDB wrappers |
| VR support | SpawnDev.BlazorJS WebXR wrappers (new) |
| Crypto/auth | SpawnDev.BlazorJS.Cryptography |

## Phase Z: The North Star - Browser-Based Minecraft Client

Every feature we build moves us closer. This is the horizon we aim for.

- [ ] Z1. **Entity rendering** - Mobs, animals, item entities with models and animations
- [ ] Z2. **Player skin rendering** - Full player models with downloaded skins, animation
- [ ] Z3. **Collision/physics** - Walk ON blocks, gravity, jumping (not just flying)
- [ ] Z4. **Real-time block updates** - Server pushes block changes as they happen
- [ ] Z5. **Block interaction** - Break and place blocks in real-time (not queued)
- [ ] Z6. **Inventory system** - Full hotbar, crafting, chest interaction
- [ ] Z7. **Combat** - Hit detection, damage, health, food
- [ ] Z8. **Redstone visualization** - Powered state rendering, signal propagation
- [ ] Z9. **Particle system** - All MC particles (torch flame, campfire smoke, enchant glitter)
- [ ] Z10. **Biome rendering** - Per-biome grass/leaf/water colors, fog colors
- [ ] Z11. **Chunk-level LOD** - Near: full 3D, mid: simplified, far: heightmap (seamless transitions)
- [ ] Z12. **MC protocol bridge** - Direct MC server protocol instead of file-based world reading
- [ ] Z13. **Mobile touch gameplay** - Play Minecraft from a phone browser, no app needed
- [ ] Z14. **VR gameplay** - Play Minecraft in VR from a browser, no Vivecraft needed
- [ ] Z15. **Cross-play** - Java, Bedrock, Web, and VR players all on the same server

*"Aim as far as possible and you will never be disappointed how far you make it." - Captain TJ*

---

### Key References (local)
- `Research/local-world-cache.md` - IndexedDB cache design, load sequence, offline mode
- `Research/minecraft-block-textures.md` - Atlas layout, per-face blocks, extraction
- `Research/brainstorm-viewer-features.md` - Feature vision, plugin data, quick wins
- `Research/brainstorm-beyond-the-map.md` - Full platform brainstorm (creative mode, AI, VR, chat)
- `Research/server-plugin-data.md` - All 22 plugins, data formats, RCON capabilities
- `Research/water-transparency-implementation.md` - Two-pass rendering spec
