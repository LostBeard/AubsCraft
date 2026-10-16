package com.spawndev.aubscraftlive;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;

/**
 * The wire format of one frame (little-endian), independent of Bukkit so it can be tested with plain data (see LivePlugin/test). The
 * panel's LiveFrameParser (AubsCraft.Live.Model) reads it; LivePlugin/FORMAT.md describes it.
 *
 * <pre>
 * "ACL1"  u8 version(1)  i64 fullTime  i64 dayTime  u8 weather(bit0 storm, bit1 thunder)
 * u16 typeCount, typeCount x str8 type key ("cow"; "mymod:thing")
 * u8 worldCount, per world:
 *   str8 name
 *   u16 entityCount, per entity: i32 id, u16 typeIndex, f32 x, f32 y, f32 z, i8 yaw, i8 pitch, i8 bodyYaw (256 steps a turn),
 *                                u16 flags, i16 vx, i16 vy, i16 vz (1/8000 block per tick)
 *   u16 nameCount,    per name:    i32 id, str8 name
 *   u16 variantCount, per variant: i32 id, str8 variant
 *   u16 playerCount,  per player:  i32 id, i64 uuidMost, i64 uuidLeast, str8 name, str16 skinUrl, u8 slim, u8 gameMode, str8 heldItem, u8 health(0-255)
 * </pre>
 */
final class Frames {
    private Frames() {}

    static final int FLAG_ON_GROUND = 1, FLAG_SNEAKING = 2, FLAG_SPRINTING = 4, FLAG_SWIMMING = 8, FLAG_GLIDING = 16, FLAG_SLEEPING = 32,
            FLAG_BABY = 64, FLAG_BURNING = 128, FLAG_GLOWING = 256, FLAG_INVISIBLE = 512, FLAG_HAS_NAME = 1024, FLAG_PLAYER = 2048,
            FLAG_RIDING = 4096, FLAG_DEAD = 8192, FLAG_SWINGING = 16384, FLAG_USING = 32768;

    record EntitySample(int id, String type, double x, double y, double z, float yaw, float pitch, float bodyYaw, int flags,
                        double vx, double vy, double vz, String name, String variant) {}

    record PlayerSample(int entityId, UUID uuid, String name, String skinUrl, boolean slim, int gameMode, String heldItem, int health) {}

    record WorldSample(String name, List<EntitySample> entities, List<PlayerSample> players) {}

    /** Encode a frame. Entity types are tabled in the order they first appear. */
    static byte[] build(long fullTime, long dayTime, int weather, List<WorldSample> worlds) {
        Map<String, Integer> types = new LinkedHashMap<>();
        for (WorldSample w : worlds)
            for (EntitySample e : w.entities()) types.computeIfAbsent(e.type(), k -> types.size());

        Writer w = new Writer(64 * 1024);
        w.bytes(new byte[] {'A', 'C', 'L', '1'});
        w.u8(1);
        w.i64(fullTime);
        w.i64(dayTime);
        w.u8(weather);
        w.u16(types.size());
        for (String key : types.keySet()) w.str8(key);

        w.u8(worlds.size());
        for (WorldSample world : worlds) {
            w.str8(world.name());
            w.u16(world.entities().size());
            int names = 0, variants = 0;
            for (EntitySample e : world.entities()) {
                w.i32(e.id());
                w.u16(types.get(e.type()));
                w.f32((float) e.x());
                w.f32((float) e.y());
                w.f32((float) e.z());
                w.i8(angle(e.yaw()));
                w.i8(angle(e.pitch()));
                w.i8(angle(e.bodyYaw()));
                w.u16(e.flags());
                w.i16(velocity(e.vx()));
                w.i16(velocity(e.vy()));
                w.i16(velocity(e.vz()));
                if (e.name() != null) names++;
                if (e.variant() != null) variants++;
            }
            w.u16(names);
            for (EntitySample e : world.entities()) if (e.name() != null) { w.i32(e.id()); w.str8(e.name()); }
            w.u16(variants);
            for (EntitySample e : world.entities()) if (e.variant() != null) { w.i32(e.id()); w.str8(e.variant()); }
            w.u16(world.players().size());
            for (PlayerSample p : world.players()) {
                w.i32(p.entityId());
                w.i64(p.uuid().getMostSignificantBits());
                w.i64(p.uuid().getLeastSignificantBits());
                w.str8(p.name());
                w.str16(p.skinUrl());
                w.u8(p.slim() ? 1 : 0);
                w.u8(p.gameMode());
                w.str8(p.heldItem());
                w.u8(p.health());
            }
        }
        return w.toArray();
    }

    /** Degrees to a signed byte: 256 steps a turn. */
    static int angle(float degrees) {
        float turns = (degrees % 360f) / 360f;
        return (byte) Math.round(turns * 256f);
    }

    /** Blocks per tick to a short in 1/8000ths (the protocol's own unit), clamped. */
    static int velocity(double blocksPerTick) {
        return (int) Math.max(-32767, Math.min(32767, Math.round(blocksPerTick * 8000.0)));
    }

    /** A little-endian byte writer. */
    static final class Writer {
        private ByteBuffer buf;

        Writer(int capacity) { buf = ByteBuffer.allocate(capacity).order(ByteOrder.LITTLE_ENDIAN); }

        private void room(int n) {
            if (buf.remaining() >= n) return;
            ByteBuffer bigger = ByteBuffer.allocate(Math.max(buf.capacity() * 2, buf.position() + n + 1024)).order(ByteOrder.LITTLE_ENDIAN);
            buf.flip();
            bigger.put(buf);
            buf = bigger;
        }

        void u8(int v) { room(1); buf.put((byte) v); }
        void i8(int v) { room(1); buf.put((byte) v); }
        void u16(int v) { room(2); buf.putShort((short) v); }
        void i16(int v) { room(2); buf.putShort((short) v); }
        void i32(int v) { room(4); buf.putInt(v); }
        void i64(long v) { room(8); buf.putLong(v); }
        void f32(float v) { room(4); buf.putFloat(v); }
        void bytes(byte[] b) { room(b.length); buf.put(b); }

        /** A string of at most 255 UTF-8 bytes: a length byte and the bytes (cut at a character boundary). */
        void str8(String s) {
            byte[] b = clip(s.getBytes(StandardCharsets.UTF_8), 255);
            u8(b.length);
            room(b.length);
            buf.put(b);
        }

        /** A string of at most 65535 UTF-8 bytes: a 16-bit length and the bytes. */
        void str16(String s) {
            byte[] b = clip(s.getBytes(StandardCharsets.UTF_8), 65535);
            u16(b.length);
            room(b.length);
            buf.put(b);
        }

        // Cut UTF-8 at a whole character: never in the middle of a multi-byte sequence
        private static byte[] clip(byte[] b, int max) {
            if (b.length <= max) return b;
            int n = max;
            while (n > 0 && (b[n] & 0xC0) == 0x80) n--;
            return java.util.Arrays.copyOf(b, n);
        }

        byte[] toArray() {
            byte[] out = new byte[buf.position()];
            System.arraycopy(buf.array(), 0, out, 0, out.length);
            return out;
        }
    }
}
