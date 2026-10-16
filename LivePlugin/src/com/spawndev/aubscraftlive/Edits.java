package com.spawndev.aubscraftlive;

import org.bukkit.Bukkit;
import org.bukkit.World;
import org.bukkit.block.Block;
import org.bukkit.block.BlockState;
import org.bukkit.block.TileState;
import org.bukkit.block.data.BlockData;
import org.bukkit.plugin.java.JavaPlugin;

import java.io.ByteArrayInputStream;
import java.io.DataInputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.function.Consumer;

/**
 * Edits to the world, asked for by the panel's admins (the panel has checked who asks; this trusts the panel, which is on the same machine and holds the secret). An edit is a list of block changes
 * (single blocks, filled boxes, sparse lists of blocks) that is applied on the main thread a slice a tick (<code>edit-budget-ms</code>), so a big one never stalls the server. Everything it
 * overwrites is remembered (block data, and the contents of tile entities such as signs and chests) so the edit can be undone, and undone again (redo): an undo swaps what is in the world with what
 * the journal holds. The journal is bounded; the oldest edits fall out of it.
 */
final class Edits {
    /** Blocks the journal remembers over all edits; past it the oldest edits are forgotten (their undo is no longer possible). */
    private static final long MAX_JOURNAL_BLOCKS = 3_000_000L;
    /** Changes of this many blocks or fewer update their neighbours (fences connect, stairs bend); bigger ones do not, for speed. */
    private static final int PHYSICS_LIMIT = 512;

    /** What an edit changed, in the order it changed it: packed positions and the block data that was there (or is to go back, after an undo). */
    private static final class Journal {
        final String id, editor, label;
        long[] pos = new long[256];
        BlockData[] data = new BlockData[256];
        Map<Integer, BlockState> tiles;   // by index: tile entities that were there (signs, chests...)
        int count;
        boolean applied = true;   // true: the world holds the edit; false: it holds what was there before

        Journal(String id, String editor, String label) { this.id = id; this.editor = editor; this.label = label; }

        void add(int x, int y, int z, BlockData old, BlockState tile) {
            if (count == pos.length) {
                pos = java.util.Arrays.copyOf(pos, count * 2);
                data = java.util.Arrays.copyOf(data, count * 2);
            }
            pos[count] = pack(x, y, z);
            data[count] = old;
            if (tile != null) {
                if (tiles == null) tiles = new HashMap<>();
                tiles.put(count, tile);
            }
            count++;
        }
    }

    /** One piece of work: yields block changes one at a time. */
    private interface Source {
        /** Fills the next change into the job; false when there is no more. */
        boolean next(Change into);
    }

    private static final class Change {
        int x, y, z;
        BlockData data, only;
    }

    private static final class Job {
        final String commandId;
        final Journal journal;
        final List<Source> sources = new ArrayList<>();
        int source;
        long placed, skipped;
        boolean physics;
        // An undo is a swap over a journal
        Journal swap;
        int swapAt;

        Job(String commandId, Journal journal) { this.commandId = commandId; this.journal = journal; }
    }

    private final JavaPlugin plugin;
    private final ChunkWatcher chunks;
    private final long budgetNanos;
    private final ArrayDeque<Job> queue = new ArrayDeque<>();
    private final LinkedHashMap<String, Journal> journals = new LinkedHashMap<>();
    private long journalBlocks;
    private Consumer<String> reply = r -> { };

    Edits(JavaPlugin plugin, ChunkWatcher chunks, int budgetMs) {
        this.plugin = plugin;
        this.chunks = chunks;
        this.budgetNanos = Math.max(1, budgetMs) * 1_000_000L;
    }

    void start(Consumer<String> reply) {
        this.reply = reply;
        plugin.getServer().getScheduler().runTaskTimer(plugin, this::tick, 20L, 1L);
    }

    private static long pack(int x, int y, int z) {
        return ((long) (x & 0x3FFFFFF) << 38) | ((long) (z & 0x3FFFFFF) << 12) | (y & 0xFFF);
    }

    private static int ux(long p) { return (int) (p >> 38) << 6 >> 6; }
    private static int uz(long p) { return (int) ((p >> 12) & 0x3FFFFFF) << 6 >> 6; }
    private static int uy(long p) { return (int) (p & 0xFFF) << 20 >> 20; }

    private World world() { return plugin.getServer().getWorlds().get(0); }

    // ---- commands from the panel (main thread) ----

    // A command is binary, little-endian: u8 kind (1 edit, 2 undo), string id; an edit goes on with string editor, string label, u16 palette count and that many strings (block data), u16 op count
    // and the ops: u8 type (1 set: i32 x y z, u16 block; 2 fill: i32 x1 y1 z1 x2 y2 z2, u16 block, i32 only (-1 none), u8 hollow; 3 blocks: i32 x y z of an anchor, i32 length, that many bytes of
    // (i16 dx, i16 dy, i16 dz, u16 block)); an undo goes on with string edit. A string is u16 length and UTF-8 bytes.

    /** A command from the panel. The answer goes back through the reply callback (a JSON object) when it is done. */
    void submit(byte[] payload) {
        String id = "";
        try {
            DataInputStream in = new DataInputStream(new ByteArrayInputStream(payload));
            int kind = in.readUnsignedByte();
            id = readString(in);
            switch (kind) {
                case 1 -> queue.add(buildEdit(id, in));
                case 2 -> queue.add(buildUndo(id, readString(in)));
                default -> fail(id, "Unknown command kind");
            }
        } catch (Throwable t) {
            fail(id, t.getMessage() == null ? t.toString() : t.getMessage());
        }
    }

    private void fail(String id, String message) {
        reply.accept("{\"id\":" + json(id) + ",\"ok\":false,\"error\":" + json(message) + "}");
    }

    private static String json(String s) {
        StringBuilder b = new StringBuilder("\"");
        for (char c : s.toCharArray()) {
            switch (c) {
                case '"' -> b.append("\\\"");
                case '\\' -> b.append("\\\\");
                case '\n' -> b.append("\\n");
                case '\r' -> b.append("\\r");
                case '\t' -> b.append("\\t");
                default -> { if (c < 0x20) b.append(String.format("\\u%04x", (int) c)); else b.append(c); }
            }
        }
        return b.append('"').toString();
    }

    // Little-endian reads
    private static int i32(DataInputStream in) throws IOException { return Integer.reverseBytes(in.readInt()); }
    private static int u16(DataInputStream in) throws IOException { int a = in.readUnsignedByte(); return a | (in.readUnsignedByte() << 8); }

    private static String readString(DataInputStream in) throws IOException {
        byte[] b = new byte[u16(in)];
        in.readFully(b);
        return new String(b, StandardCharsets.UTF_8);
    }

    private Job buildEdit(String id, DataInputStream in) throws IOException {
        String editor = readString(in), label = readString(in);
        BlockData[] palette = new BlockData[u16(in)];
        for (int i = 0; i < palette.length; i++) {
            String s = readString(in);
            try {
                palette[i] = Bukkit.createBlockData(s);
            } catch (IllegalArgumentException e) {
                throw new IllegalArgumentException("Unknown block: " + s);
            }
        }
        Journal journal = new Journal(id, editor, label);
        Job job = new Job(id, journal);
        long expected = 0;
        int ops = u16(in);
        for (int n = 0; n < ops; n++) {
            switch (in.readUnsignedByte()) {
                case 1 -> {
                    int x = i32(in), y = i32(in), z = i32(in);
                    job.sources.add(single(x, y, z, palette[u16(in)]));
                    expected++;
                }
                case 2 -> {
                    int x1 = i32(in), y1 = i32(in), z1 = i32(in), x2 = i32(in), y2 = i32(in), z2 = i32(in);
                    BlockData data = palette[u16(in)];
                    int onlyIndex = i32(in);
                    boolean hollow = in.readUnsignedByte() != 0;
                    job.sources.add(box(Math.min(x1, x2), Math.min(y1, y2), Math.min(z1, z2), Math.max(x1, x2), Math.max(y1, y2), Math.max(z1, z2), data, onlyIndex < 0 ? null : palette[onlyIndex], hollow));
                    expected += (long) (Math.abs(x2 - x1) + 1) * (Math.abs(y2 - y1) + 1) * (Math.abs(z2 - z1) + 1);
                }
                case 3 -> {
                    int ax = i32(in), ay = i32(in), az = i32(in);
                    byte[] raw = new byte[i32(in)];
                    in.readFully(raw);
                    job.sources.add(sparse(raw, ax, ay, az, palette));
                    expected += raw.length / 8;
                }
                default -> throw new IllegalArgumentException("Unknown op");
            }
        }
        job.physics = expected <= PHYSICS_LIMIT;
        return job;
    }

    private Job buildUndo(String id, String editId) {
        Journal j = journals.get(editId);
        if (j == null) throw new IllegalArgumentException("That edit is no longer remembered");
        Job job = new Job(id, j);
        job.swap = j;
        job.physics = j.count <= PHYSICS_LIMIT;
        return job;
    }

    // ---- where changes come from ----

    private static Source single(int x, int y, int z, BlockData data) {
        boolean[] done = { false };
        return c -> {
            if (done[0]) return false;
            done[0] = true;
            c.x = x; c.y = y; c.z = z; c.data = data;
            return true;
        };
    }

    private static Source box(int x1, int y1, int z1, int x2, int y2, int z2, BlockData data, BlockData only, boolean hollow) {
        int[] at = { x1, y1, z1 };
        boolean[] first = { true };
        return c -> {
            while (true) {
                if (!first[0]) {
                    if (++at[0] > x2) { at[0] = x1; if (++at[2] > z2) { at[2] = z1; if (++at[1] > y2) return false; } }
                }
                first[0] = false;
                boolean edge = at[0] == x1 || at[0] == x2 || at[1] == y1 || at[1] == y2 || at[2] == z1 || at[2] == z2;
                if (hollow && !edge) {
                    // Jump over the inside of a row
                    if (at[0] == x1 && x2 > x1) at[0] = x2 - 1;
                    continue;
                }
                c.x = at[0]; c.y = at[1]; c.z = at[2]; c.data = data;
                c.only = only;
                return true;
            }
        };
    }

    private static Source sparse(byte[] raw, int ax, int ay, int az, BlockData[] palette) {
        int[] i = { 0 };
        return c -> {
            if (i[0] + 8 > raw.length) return false;
            int p = i[0];
            c.x = ax + (short) ((raw[p] & 0xFF) | (raw[p + 1] << 8));
            c.y = ay + (short) ((raw[p + 2] & 0xFF) | (raw[p + 3] << 8));
            c.z = az + (short) ((raw[p + 4] & 0xFF) | (raw[p + 5] << 8));
            c.data = palette[(raw[p + 6] & 0xFF) | ((raw[p + 7] & 0xFF) << 8)];
            i[0] += 8;
            return true;
        };
    }

    // ---- applying (main thread, a slice a tick) ----

    private void tick() {
        Job job = queue.peek();
        if (job == null) return;
        long end = System.nanoTime() + budgetNanos;
        World world = world();
        boolean finished;
        try {
            finished = job.swap != null ? stepSwap(job, world, end) : stepEdit(job, world, end);
        } catch (Throwable t) {
            queue.poll();
            fail(job.commandId, t.getMessage() == null ? t.toString() : t.getMessage());
            return;
        }
        if (!finished) return;
        queue.poll();
        if (job.swap == null) remember(job.journal);
        else job.swap.applied = !job.swap.applied;
        reply.accept("{\"id\":" + json(job.commandId) + ",\"ok\":true,\"edit\":" + json(job.journal.id) + ",\"placed\":" + job.placed + ",\"skipped\":" + job.skipped + ",\"applied\":" + job.journal.applied + "}");
    }

    private boolean stepEdit(Job job, World world, long end) {
        Change c = new Change();
        int sinceClock = 0;
        while (job.source < job.sources.size()) {
            c.only = null;
            if (!job.sources.get(job.source).next(c)) { job.source++; continue; }
            if (c.y < world.getMinHeight() || c.y >= world.getMaxHeight()) { job.skipped++; continue; }
            Block block = world.getBlockAt(c.x, c.y, c.z);
            BlockData old = block.getBlockData();
            if (c.only != null && !old.matches(c.only)) { job.skipped++; continue; }
            if (old.equals(c.data)) { job.skipped++; continue; }
            BlockState tile = block.getState() instanceof TileState ts ? ts : null;
            job.journal.add(c.x, c.y, c.z, old, tile);
            block.setBlockData(c.data, job.physics);
            chunks.markDirty(world, c.x, c.z);
            job.placed++;
            if ((++sinceClock & 63) == 0 && System.nanoTime() > end) return false;
        }
        return true;
    }

    // An undo (or redo): put the journal's blocks into the world, and the blocks that were there into the journal
    private boolean stepSwap(Job job, World world, long end) {
        Journal j = job.swap;
        int sinceClock = 0;
        while (job.swapAt < j.count) {
            int i = job.swapAt++;
            int x = ux(j.pos[i]), y = uy(j.pos[i]), z = uz(j.pos[i]);
            Block block = world.getBlockAt(x, y, z);
            BlockData current = block.getBlockData();
            BlockState currentTile = block.getState() instanceof TileState ts ? ts : null;
            BlockData put = j.data[i];
            BlockState putTile = j.tiles == null ? null : j.tiles.get(i);
            block.setBlockData(put, job.physics);
            if (putTile != null) putTile.update(true, false);
            j.data[i] = current;
            if (currentTile != null) { if (j.tiles == null) j.tiles = new HashMap<>(); j.tiles.put(i, currentTile); }
            else if (j.tiles != null) j.tiles.remove(i);
            chunks.markDirty(world, x, z);
            job.placed++;
            if ((++sinceClock & 63) == 0 && System.nanoTime() > end) return false;
        }
        return true;
    }

    private void remember(Journal j) {
        if (j.count == 0) return;
        journals.put(j.id, j);
        journalBlocks += j.count;
        var it = journals.values().iterator();
        while (journalBlocks > MAX_JOURNAL_BLOCKS && it.hasNext()) {
            Journal old = it.next();
            if (old == j) break;
            journalBlocks -= old.count;
            it.remove();
        }
    }
}
