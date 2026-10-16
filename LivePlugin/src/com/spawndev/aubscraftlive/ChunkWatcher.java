package com.spawndev.aubscraftlive;

import org.bukkit.Chunk;
import org.bukkit.ChunkSnapshot;
import org.bukkit.HeightMap;
import org.bukkit.World;
import org.bukkit.block.Banner;
import org.bukkit.block.BlockState;
import org.bukkit.block.Sign;
import org.bukkit.block.banner.Pattern;
import org.bukkit.block.sign.Side;
import org.bukkit.block.data.BlockData;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.Listener;
import org.bukkit.event.block.*;
import org.bukkit.event.entity.EntityChangeBlockEvent;
import org.bukkit.event.entity.EntityExplodeEvent;
import org.bukkit.event.player.PlayerBucketEmptyEvent;
import org.bukkit.event.player.PlayerBucketFillEvent;
import org.bukkit.event.world.StructureGrowEvent;
import org.bukkit.plugin.java.JavaPlugin;

import java.io.ByteArrayOutputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.logging.Level;

/**
 * Keeps the panel's picture of the world current. The panel reads the world from its region FILES, which the server writes minutes after a change; this class notices which chunks of the
 * main world have changed (the block events, and whatever {@link #markDirty} is told of: the panel's own edits), and a moment after a chunk has settled posts its blocks, light, sign texts
 * and banner patterns to the panel (<c>POST /api/live/chunk/{serverId}</c>, format in FORMAT.md), which serves that instead of the file until the file catches up. Only the main world is
 * watched (the viewer shows one). Reads happen on the main thread under a small time budget; the encoding and the POST are off it.
 */
final class ChunkWatcher implements Listener {
    /** Chunks looked at per run, and the time they may take on the main thread together (nanoseconds). */
    private static final int MAX_PER_RUN = 3;
    private static final long BUDGET_NANOS = 12_000_000L;
    /** A chunk is sent once it has been quiet this many ticks, or has been changing this many (so a long build still shows up). */
    private static final int QUIET_TICKS = 8, MAX_WAIT_TICKS = 40;
    private static final int MAX_IN_FLIGHT = 4;
    private static final int MAX_DIRTY = 4096;
    private static final int MAX_SIGNS = 64, MAX_BANNERS = 64;

    private static final class Dirty {
        int borderMask;
        long first, last;
    }

    private final JavaPlugin plugin;
    private final HttpClient http;
    private final URI endpoint;
    private final String secret;
    private final Map<Long, Dirty> dirty = new LinkedHashMap<>();
    private final AtomicInteger inFlight = new AtomicInteger();
    private long tick;
    private long sent, failed, skipped, encoded, encodeNanos;
    private long lastReport;

    ChunkWatcher(JavaPlugin plugin, HttpClient http, URI endpoint, String secret) {
        this.plugin = plugin;
        this.http = http;
        this.endpoint = endpoint;
        this.secret = secret;
    }

    void start() {
        plugin.getServer().getPluginManager().registerEvents(this, plugin);
        plugin.getServer().getScheduler().runTaskTimer(plugin, this::run, 40L, 4L);
    }

    private World mainWorld() {
        List<World> worlds = plugin.getServer().getWorlds();
        return worlds.isEmpty() ? null : worlds.get(0);
    }

    /** A block changed (x, z in blocks): its chunk is to be sent again, and a block on the chunk's edge also changes what the neighbour's faces hide. */
    void markDirty(World world, int x, int z) {
        if (world == null || world != mainWorld()) return;
        int cx = x >> 4, cz = z >> 4, lx = x & 15, lz = z & 15;
        int mask = (lx == 0 ? 1 : 0) | (lx == 15 ? 2 : 0) | (lz == 0 ? 4 : 0) | (lz == 15 ? 8 : 0);
        long key = pack(cx, cz);
        Dirty d = dirty.get(key);
        if (d == null) {
            if (dirty.size() >= MAX_DIRTY) return;
            d = new Dirty();
            d.first = tick;
            dirty.put(key, d);
        }
        d.last = tick;
        d.borderMask |= mask;
    }

    /** The changes so far are all there is to wait for (an edit has finished): the chunks waiting are sent at the next run, not after they have been quiet. */
    void settleAll() {
        for (Dirty d : dirty.values()) d.first = tick - MAX_WAIT_TICKS;
    }

    private static long pack(int cx, int cz) { return ((long) cx << 32) | (cz & 0xFFFFFFFFL); }
    private static int unpackX(long key) { return (int) (key >> 32); }
    private static int unpackZ(long key) { return (int) key; }

    // ---- what changes blocks ----

    private void mark(org.bukkit.block.Block b) { markDirty(b.getWorld(), b.getX(), b.getZ()); }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void onPlace(BlockPlaceEvent e) { mark(e.getBlock()); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void onBreak(BlockBreakEvent e) { mark(e.getBlock()); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void onBurn(BlockBurnEvent e) { mark(e.getBlock()); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void onFade(BlockFadeEvent e) { mark(e.getBlock()); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void onForm(BlockFormEvent e) { mark(e.getBlock()); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void onSpread(BlockSpreadEvent e) { mark(e.getBlock()); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void onGrow(BlockGrowEvent e) { mark(e.getBlock()); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void onFlow(BlockFromToEvent e) { mark(e.getToBlock()); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void onLeaves(LeavesDecayEvent e) { mark(e.getBlock()); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void onSign(SignChangeEvent e) { mark(e.getBlock()); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void onBucketEmpty(PlayerBucketEmptyEvent e) { mark(e.getBlock()); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void onBucketFill(PlayerBucketFillEvent e) { mark(e.getBlock()); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void onEntityChange(EntityChangeBlockEvent e) { mark(e.getBlock()); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void onBlockExplode(BlockExplodeEvent e) { for (var b : e.blockList()) mark(b); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void onEntityExplode(EntityExplodeEvent e) { for (var b : e.blockList()) mark(b); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void onStructure(StructureGrowEvent e) { for (var s : e.getBlocks()) markDirty(s.getWorld(), s.getX(), s.getZ()); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void onPistonExtend(BlockPistonExtendEvent e) { mark(e.getBlock()); for (var b : e.getBlocks()) { mark(b); mark(b.getRelative(e.getDirection())); } }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void onPistonRetract(BlockPistonRetractEvent e) { mark(e.getBlock()); for (var b : e.getBlocks()) { mark(b); mark(b.getRelative(e.getDirection())); } }


    // ---- what changes blocks without an event: commands (the game's own setblock / fill / clone, and editing plugins such as WorldEdit) ----

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onConsoleCommand(org.bukkit.event.server.ServerCommandEvent e) { onCommand(e.getCommand(), e.getSender()); }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onRconCommand(org.bukkit.event.server.RemoteServerCommandEvent e) { onCommand(e.getCommand(), e.getSender()); }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onPlayerCommand(org.bukkit.event.player.PlayerCommandPreprocessEvent e) { onCommand(e.getMessage(), e.getPlayer()); }

    /** Chunks within this many chunks of a player are looked at after a command whose area is not known. */
    private static final int SWEEP_RADIUS = 3;

    void onCommand(String raw, org.bukkit.command.CommandSender sender) {
        if (raw == null) return;
        World world = mainWorld();
        if (world == null) return;
        String cmd = raw.startsWith("/") ? raw.substring(1) : raw;
        // "execute ... run <command>": what runs is the part after the last " run "
        int run = cmd.lastIndexOf(" run ");
        boolean executed = run >= 0;
        if (executed) cmd = cmd.substring(run + 5);
        String[] t = cmd.trim().split("\s+");
        if (t.length == 0) return;
        String name = t[0].toLowerCase();
        if (name.startsWith("minecraft:")) name = name.substring(10);
        org.bukkit.Location origin = sender instanceof org.bukkit.entity.Entity en ? en.getLocation() : (sender instanceof org.bukkit.command.BlockCommandSender b ? b.getBlock().getLocation() : world.getSpawnLocation());
        try {
            if (!executed && name.equals("setblock") && t.length >= 4) {
                double[] p = coords(t, 1, origin);
                if (p != null) { markDirty(world, (int) Math.floor(p[0]), (int) Math.floor(p[2])); return; }
            } else if (!executed && name.equals("fill") && t.length >= 7) {
                double[] a = coords(t, 1, origin), b = coords(t, 4, origin);
                if (a != null && b != null) { markBox(world, a, b); return; }
            } else if (!executed && name.equals("clone") && t.length >= 10) {
                double[] a = coords(t, 1, origin), b = coords(t, 4, origin), d = coords(t, 7, origin);
                if (a != null && b != null && d != null) {
                    double[] to = { d[0] + Math.abs(b[0] - a[0]), d[1], d[2] + Math.abs(b[2] - a[2]) };
                    markBox(world, d, to);
                    return;
                }
            }
        } catch (RuntimeException ignored) {
            // an odd command line: fall through to the sweep
        }
        // Commands that edit blocks without saying where in a way we read (relative to something else, WorldEdit's, place, ...): look around the players
        boolean editing = executed || name.equals("setblock") || name.equals("fill") || name.equals("clone") || name.equals("place") || name.equals("fillbiome") || name.equals("function")
                || name.equals("data") || name.startsWith("/") || name.startsWith("worldedit:") || name.equals("we") || name.equals("worldedit") || name.equals("fastasyncworldedit") || name.equals("fawe");
        if (editing) sweepSoon(world);
    }

    // Three numbers from t[at..]: absolute, or "~n" from the origin; null for anything else (a "^" local coordinate, a selector)
    private static double[] coords(String[] t, int at, org.bukkit.Location origin) {
        double[] r = new double[3];
        double[] o = { origin.getX(), origin.getY(), origin.getZ() };
        for (int i = 0; i < 3; i++) {
            String s = t[at + i];
            if (s.startsWith("^")) return null;
            if (s.startsWith("~")) r[i] = o[i] + (s.length() > 1 ? Double.parseDouble(s.substring(1)) : 0);
            else r[i] = Double.parseDouble(s);
        }
        return r;
    }

    private void markBox(World world, double[] a, double[] b) {
        int x0 = (int) Math.floor(Math.min(a[0], b[0])) >> 4, x1 = (int) Math.floor(Math.max(a[0], b[0])) >> 4;
        int z0 = (int) Math.floor(Math.min(a[2], b[2])) >> 4, z1 = (int) Math.floor(Math.max(a[2], b[2])) >> 4;
        if ((long) (x1 - x0 + 1) * (z1 - z0 + 1) > 256) { sweepSoon(world); return; }
        for (int cx = x0; cx <= x1; cx++)
            for (int cz = z0; cz <= z1; cz++) markDirty(world, cx << 4, cz << 4);
    }

    // After an edit whose area is unknown: the chunks round the players now, and again after a while (an async editor is still working)
    private void sweepSoon(World world) {
        sweepNow(world);
        plugin.getServer().getScheduler().runTaskLater(plugin, () -> sweepNow(world), 100L);
        plugin.getServer().getScheduler().runTaskLater(plugin, () -> sweepNow(world), 400L);
    }

    private void sweepNow(World world) {
        for (var player : world.getPlayers()) {
            int pcx = player.getLocation().getBlockX() >> 4, pcz = player.getLocation().getBlockZ() >> 4;
            for (int dx = -SWEEP_RADIUS; dx <= SWEEP_RADIUS; dx++)
                for (int dz = -SWEEP_RADIUS; dz <= SWEEP_RADIUS; dz++)
                    if (world.isChunkLoaded(pcx + dx, pcz + dz)) markDirty(world, (pcx + dx) << 4, (pcz + dz) << 4);
        }
    }

    // ---- sending ----

    private void run() {
        tick += 4;
        if (dirty.isEmpty() || inFlight.get() >= MAX_IN_FLIGHT) return;
        World world = mainWorld();
        if (world == null) return;
        long started = System.nanoTime();
        int done = 0;
        var it = dirty.entrySet().iterator();
        while (it.hasNext() && done < MAX_PER_RUN && System.nanoTime() - started < BUDGET_NANOS && inFlight.get() < MAX_IN_FLIGHT) {
            var entry = it.next();
            Dirty d = entry.getValue();
            if (tick - d.last < QUIET_TICKS && tick - d.first < MAX_WAIT_TICKS) continue;
            int cx = unpackX(entry.getKey()), cz = unpackZ(entry.getKey());
            it.remove();
            try {
                long t0 = System.nanoTime();
                // A viewer's edit far from every player: Paper loaded the chunk for the edit and let it go again (the change was saved with it), so it may not be loaded now. It is read anyway
                // (loading it): skipping it left the panel, and every viewer, without a block the server reported as placed.
                Chunk chunk = world.getChunkAt(cx, cz, false);
                if (chunk == null) { skipped++; continue; }
                byte[] body = encode(world, chunk, d.borderMask);
                long took = System.nanoTime() - t0;
                encodeNanos += took;
                encoded++;
                if (took > 8_000_000L) plugin.getLogger().info("Slow chunk read: (" + cx + ", " + cz + ") took " + took / 1_000_000 + " ms on the main thread");
                post(cx, cz, body);
                done++;
            } catch (Throwable t) {
                plugin.getLogger().log(Level.WARNING, "Could not read chunk (" + cx + ", " + cz + ") for the panel", t);
            }
        }
    }

    private void post(int cx, int cz, byte[] body) {
        inFlight.incrementAndGet();
        HttpRequest request = HttpRequest.newBuilder(URI.create(endpoint + "?cx=" + cx + "&cz=" + cz))
                .timeout(Duration.ofSeconds(10))
                .header("Content-Type", "application/octet-stream")
                .header("X-Live-Secret", secret)
                .POST(HttpRequest.BodyPublishers.ofByteArray(body))
                .build();
        http.sendAsync(request, HttpResponse.BodyHandlers.discarding()).whenComplete((response, error) -> {
            inFlight.decrementAndGet();
            if (error != null || response.statusCode() / 100 != 2) failed++; else sent++;
            long now = System.currentTimeMillis();
            if (failed > 0 && now - lastReport > 600_000) {
                lastReport = now;
                plugin.getLogger().warning("Chunk updates: " + sent + " sent, " + failed + " failed, " + (encoded == 0 ? 0 : encodeNanos / encoded / 1000) + " us a chunk to read");
            }
        });
    }

    /**
     * One chunk, as FORMAT.md describes it: the blocks of the lowest <code>top</code> layers (indices into a palette of block-data strings, in the panel's order x + z*16 + y*256 from the world's
     * bottom), their light (sky in the high four bits, block in the low four), the height rain stops at for each column, the sign texts and the banner patterns.
     */
    static byte[] encode(World world, Chunk chunk, int borderMask) throws IOException {
        int minY = world.getMinHeight(), height = Math.min(384, world.getMaxHeight() - minY);
        ChunkSnapshot snap = chunk.getChunkSnapshot(true, false, false);
        int top = 0;
        for (int z = 0; z < 16; z++)
            for (int x = 0; x < 16; x++)
                top = Math.max(top, snap.getHighestBlockYAt(x, z) - minY + 1);
        top = Math.max(0, Math.min(top, height));

        // Palette (by the block data's own string) and the indices
        Map<BlockData, Integer> index = new HashMap<>();
        List<String> palette = new ArrayList<>();
        short[] blocks = new short[256 * top];
        byte[] light = new byte[256 * top];
        for (int y = 0; y < top; y++) {
            int wy = minY + y;
            for (int z = 0; z < 16; z++)
                for (int x = 0; x < 16; x++) {
                    int i = x + z * 16 + y * 256;
                    BlockData data = snap.getBlockData(x, wy, z);
                    Integer id = index.get(data);
                    if (id == null) {
                        id = palette.size();
                        index.put(data, id);
                        palette.add(data.getAsString());
                    }
                    blocks[i] = (short) (int) id;
                    light[i] = (byte) ((snap.getBlockSkyLight(x, wy, z) << 4) | snap.getBlockEmittedLight(x, wy, z));
                }
        }

        ByteArrayOutputStream bytes = new ByteArrayOutputStream(blocks.length * 3 + 4096);
        DataOutputStream out = new DataOutputStream(bytes);
        out.writeByte(1);                       // version
        out.writeByte(borderMask);
        writeShort(out, top);
        writeShort(out, palette.size());
        for (String s : palette) writeString(out, s);
        for (short b : blocks) writeShort(out, b);
        out.write(light);
        int bx = chunk.getX() << 4, bz = chunk.getZ() << 4;
        for (int z = 0; z < 16; z++)
            for (int x = 0; x < 16; x++)
                writeShort(out, world.getHighestBlockYAt(bx + x, bz + z, HeightMap.MOTION_BLOCKING) + 1 - minY);

        // Sign texts and banner patterns
        List<byte[]> signs = new ArrayList<>(), banners = new ArrayList<>();
        for (BlockState state : chunk.getTileEntities(false)) {
            if (state instanceof Sign sign && signs.size() < MAX_SIGNS) {
                var side = sign.getSide(Side.FRONT);
                var plain = net.kyori.adventure.text.serializer.plain.PlainTextComponentSerializer.plainText();
                ByteArrayOutputStream b = new ByteArrayOutputStream();
                DataOutputStream o = new DataOutputStream(b);
                o.writeByte(state.getX() & 15); writeShort(o, state.getY()); o.writeByte(state.getZ() & 15);
                for (int i = 0; i < 4; i++) writeString(o, plain.serialize(side.line(i)));
                writeString(o, side.getColor() == null ? "black" : side.getColor().name().toLowerCase());
                o.writeByte(side.isGlowingText() ? 1 : 0);
                signs.add(b.toByteArray());
            } else if (state instanceof Banner banner && banners.size() < MAX_BANNERS && !banner.getPatterns().isEmpty()) {
                ByteArrayOutputStream b = new ByteArrayOutputStream();
                DataOutputStream o = new DataOutputStream(b);
                o.writeByte(state.getX() & 15); writeShort(o, state.getY()); o.writeByte(state.getZ() & 15);
                List<Pattern> patterns = banner.getPatterns();
                o.writeByte(Math.min(patterns.size(), 16));
                for (int i = 0; i < patterns.size() && i < 16; i++) {
                    writeString(o, patterns.get(i).getPattern().getKey().getKey());
                    writeString(o, patterns.get(i).getColor().name().toLowerCase());
                }
                banners.add(b.toByteArray());
            }
        }
        writeShort(out, signs.size());
        for (byte[] s : signs) out.write(s);
        writeShort(out, banners.size());
        for (byte[] s : banners) out.write(s);
        return bytes.toByteArray();
    }

    // Little-endian, like the rest of the live format
    private static void writeShort(DataOutputStream out, int v) throws IOException { out.writeByte(v & 0xFF); out.writeByte((v >> 8) & 0xFF); }

    private static void writeString(DataOutputStream out, String s) throws IOException {
        byte[] b = s.getBytes(StandardCharsets.UTF_8);
        int n = Math.min(b.length, 0xFFFF);
        writeShort(out, n);
        out.write(b, 0, n);
    }
}
