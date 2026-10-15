package com.spawndev.aubscraftlive;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.UUID;

/**
 * Writes a frame of KNOWN contents with the plugin's own encoder, for the panel's parser tests (AubsCraft.Admin.Server.Tests/TestData/live-frame-v1.bin):
 * the two sides are tested against the same bytes, in two languages.
 * Usage (from build.sh): java -cp build/classes com.spawndev.aubscraftlive.FixtureGen out.bin
 */
public final class FixtureGen {
    public static void main(String[] args) throws Exception {
        var cow = new Frames.EntitySample(101, "cow", 10.5, 64.0, -20.25, 90f, -10f, 85f, Frames.FLAG_ON_GROUND, 0.0, 0.0, 0.0, null, null);
        var sheep = new Frames.EntitySample(102, "sheep", -3.0, 70.5, 8.0, 180f, 0f, 180f, Frames.FLAG_ON_GROUND | Frames.FLAG_BABY, 0.05, 0.0, -0.1, "Dolly", "pink");
        var steve = new Frames.EntitySample(7, "player", 100.0, 80.0, 100.0, -90f, 30f, -92f,
                Frames.FLAG_PLAYER | Frames.FLAG_SNEAKING | Frames.FLAG_ON_GROUND, 0.0, -0.0784, 0.0, null, null);
        var bat = new Frames.EntitySample(103, "mymod:thing", 0.0, 120.0, 0.0, 0f, 0f, 0f, 0, 0.0, 0.0, 0.0, "Café über", null);
        var player = new Frames.PlayerSample(7, new UUID(0x0123456789ABCDEFL, 0xFEDCBA9876543210L), "Steve",
                "http://textures.minecraft.net/texture/abc123", true, 1, "diamond_sword", 200);
        var overworld = new Frames.WorldSample("world", List.of(cow, sheep, steve, bat), List.of(player));
        var nether = new Frames.WorldSample("world_nether", List.of(), List.of());
        byte[] frame = Frames.build(123456789L, 6000L, 3, List.of(overworld, nether));
        Files.write(Path.of(args[0]), frame);
        System.out.println("Wrote " + frame.length + " bytes to " + args[0]);
    }
}
