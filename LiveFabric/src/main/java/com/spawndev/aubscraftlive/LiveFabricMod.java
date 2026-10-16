package com.spawndev.aubscraftlive;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import com.mojang.authlib.GameProfile;
import com.mojang.authlib.properties.Property;
import net.fabricmc.api.DedicatedServerModInitializer;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerTickEvents;
import net.fabricmc.loader.api.FabricLoader;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.resources.ResourceLocation;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.animal.Cat;
import net.minecraft.world.entity.animal.Fox;
import net.minecraft.world.entity.animal.MushroomCow;
import net.minecraft.world.entity.animal.Parrot;
import net.minecraft.world.entity.animal.Rabbit;
import net.minecraft.world.entity.animal.TropicalFish;
import net.minecraft.world.entity.animal.horse.Horse;
import net.minecraft.world.entity.animal.horse.Llama;
import net.minecraft.world.entity.animal.sheep.Sheep;
import net.minecraft.world.entity.npc.Villager;
import net.minecraft.world.phys.Vec3;

import java.io.IOException;
import java.io.InputStream;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.ArrayList;
import java.util.Base64;
import java.util.List;
import java.util.Properties;
import java.util.concurrent.atomic.AtomicBoolean;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

/**
 * AubsCraft Live for Fabric servers: every few ticks it samples the world's entities on the server thread and POSTs one binary frame (the same format the
 * Paper plugin sends, see LivePlugin/FORMAT.md) to the AubsCraft panel on this machine, with a shared secret. Without the secret in
 * config/aubscraft-live.properties (the panel writes it when it installs the mod) it does nothing.
 */
public final class LiveFabricMod implements DedicatedServerModInitializer {
    private static final Logger LOG = LoggerFactory.getLogger("AubsCraftLive");

    private HttpClient http;
    private URI endpoint;
    private String secret;
    private int maxEntities;
    private int interval;
    private int counter;
    private final AtomicBoolean inFlight = new AtomicBoolean();
    private long sent, dropped, failed, lastReport;

    @Override
    public void onInitializeServer() {
        Path file = FabricLoader.getInstance().getConfigDir().resolve("aubscraft-live.properties");
        Properties config = new Properties();
        if (Files.exists(file)) {
            try (InputStream in = Files.newInputStream(file)) {
                config.load(new java.io.InputStreamReader(in, StandardCharsets.UTF_8));
            } catch (IOException e) {
                LOG.warn("Could not read {}", file, e);
            }
        }
        secret = config.getProperty("secret", "").trim();
        String panel = config.getProperty("panel-url", "http://127.0.0.1:5080").trim();
        String serverId = config.getProperty("server-id", "aubscraft").trim();
        interval = Math.max(1, parseInt(config.getProperty("interval-ticks"), 4));
        maxEntities = Math.max(1, parseInt(config.getProperty("max-entities"), 3000));

        if (secret.isEmpty()) {
            LOG.warn("No secret in {}: AubsCraft Live is idle. Install it from the AubsCraft panel, which writes it.", file);
            return;
        }
        endpoint = URI.create(panel.replaceAll("/+$", "") + "/api/live/ingest/" + serverId);
        http = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(2)).build();
        ServerTickEvents.END_SERVER_TICK.register(this::onTick);
        LOG.info("Sending the world's activity to {} every {} ticks", endpoint, interval);
    }

    private static int parseInt(String s, int fallback) {
        try { return s == null ? fallback : Integer.parseInt(s.trim()); } catch (NumberFormatException e) { return fallback; }
    }

    private void onTick(MinecraftServer server) {
        if (++counter < interval) return;
        counter = 0;
        // One frame in flight at a time: a slow panel loses frames, it does not queue them
        if (!inFlight.compareAndSet(false, true)) {
            dropped++;
            return;
        }
        byte[] frame;
        try {
            frame = buildFrame(server);
        } catch (Throwable t) {
            inFlight.set(false);
            LOG.warn("Could not sample the world", t);
            return;
        }
        HttpRequest request = HttpRequest.newBuilder(endpoint)
                .timeout(Duration.ofSeconds(2))
                .header("Content-Type", "application/octet-stream")
                .header("X-Live-Secret", secret)
                .POST(HttpRequest.BodyPublishers.ofByteArray(frame))
                .build();
        http.sendAsync(request, HttpResponse.BodyHandlers.discarding()).whenComplete((response, error) -> {
            inFlight.set(false);
            if (error != null || response.statusCode() / 100 != 2) failed++; else sent++;
            long now = System.currentTimeMillis();
            if (failed > 0 && now - lastReport > 600_000) {
                lastReport = now;
                LOG.warn("Live frames: {} sent, {} failed, {} dropped (is the panel running at {}?)", sent, failed, dropped, endpoint);
            }
        });
    }

    private byte[] buildFrame(MinecraftServer server) {
        List<Frames.WorldSample> samples = new ArrayList<>();
        long fullTime = 0, dayTime = 0;
        int weather = 0;
        boolean first = true;
        int budget = maxEntities;
        for (ServerLevel level : server.getAllLevels()) {
            if (first) {
                first = false;
                fullTime = level.getGameTime();
                dayTime = level.getDayTime() % 24000L;
                if (level.isRaining()) weather |= 1;
                if (level.isThundering()) weather |= 2;
            }
            List<Frames.EntitySample> entities = new ArrayList<>();
            List<Frames.PlayerSample> players = new ArrayList<>();
            for (Entity e : level.getAllEntities()) {
                if (budget <= 0) break;
                // A creature that has died is gone as far as the viewer is concerned (it is removed a second later, or lingers if it has no AI)
                if (e.isRemoved() || (e instanceof LivingEntity dying && dying.isDeadOrDying())) continue;
                String type = key(BuiltInRegistries.ENTITY_TYPE.getKey(e.getType()));
                if (type.equals("item")) continue;
                budget--;
                entities.add(sample(e, type));
                if (e instanceof ServerPlayer p) players.add(sample(p));
            }
            samples.add(new Frames.WorldSample(level.dimension().location().getPath(), entities, players));
        }
        return Frames.build(fullTime, dayTime, weather, samples);
    }

    // "minecraft:cow" -> "cow"; another namespace keeps it ("mymod:thing")
    private static String key(ResourceLocation id) {
        return id.getNamespace().equals("minecraft") ? id.getPath() : id.toString();
    }

    private static Frames.EntitySample sample(Entity e, String type) {
        Vec3 v = e.getDeltaMovement();
        int flags = 0;
        if (e.onGround()) flags |= Frames.FLAG_ON_GROUND;
        if (e.isShiftKeyDown()) flags |= Frames.FLAG_SNEAKING;
        if (e.isSprinting()) flags |= Frames.FLAG_SPRINTING;
        if (e.isSwimming()) flags |= Frames.FLAG_SWIMMING;
        if (e.isCurrentlyGlowing()) flags |= Frames.FLAG_GLOWING;
        if (e.isOnFire()) flags |= Frames.FLAG_BURNING;
        if (e.isInvisible()) flags |= Frames.FLAG_INVISIBLE;
        if (e instanceof ServerPlayer) flags |= Frames.FLAG_PLAYER;
        if (e.isPassenger()) flags |= Frames.FLAG_RIDING;
        float body = e.getYRot();
        if (e instanceof LivingEntity le) {
            if (le.isFallFlying()) flags |= Frames.FLAG_GLIDING;
            if (le.isSleeping()) flags |= Frames.FLAG_SLEEPING;
            if (le.isBaby()) flags |= Frames.FLAG_BABY;
            if (le.isDeadOrDying()) flags |= Frames.FLAG_DEAD;
            body = le.yBodyRot;
        }
        String name = null;
        if (e.hasCustomName() && e.getCustomName() != null) {
            name = e.getCustomName().getString();
            if (name.length() > 64) name = name.substring(0, 64);
            if (name.isEmpty()) name = null;
        }
        return new Frames.EntitySample(e.getId(), type, e.getX(), e.getY(), e.getZ(), e.getYRot(), e.getXRot(), body, flags,
                v.x, v.y, v.z, name, variantOf(e));
    }

    private static String variantOf(Entity e) {
        if (e instanceof Sheep s) return s.getColor().getName();
        // The same variants as the Paper plugin sends (the viewer reads them as lowercase names)
        if (e instanceof Cat c) return c.getVariant().unwrapKey().map(k -> k.location().getPath()).orElse(null);
        if (e instanceof Rabbit r) return r.getVariant().name().toLowerCase();
        if (e instanceof Fox f) return f.getVariant().name().toLowerCase();
        if (e instanceof Horse h) return h.getVariant().name().toLowerCase() + "/" + h.getMarkings().name().toLowerCase();
        if (e instanceof Llama l) return l.getVariant().name().toLowerCase();
        if (e instanceof Parrot p) return p.getVariant().name().toLowerCase();
        if (e instanceof MushroomCow m) return m.getVariant().name().toLowerCase();
        if (e instanceof TropicalFish t) return t.getPattern().name().toLowerCase() + "|" + t.getBaseColor().getName() + "|" + t.getPatternColor().getName();
        if (e instanceof Villager v) {
            ResourceLocation id = v.getVillagerData().profession().unwrapKey().map(k -> k.location()).orElse(null);
            return id == null ? null : id.getPath();
        }
        return null;
    }

    private static Frames.PlayerSample sample(ServerPlayer p) {
        String skin = "";
        boolean slim = false;
        try {
            GameProfile profile = p.getGameProfile();
            for (Property prop : profile.getProperties().get("textures")) {
                JsonObject textures = JsonParser.parseString(new String(Base64.getDecoder().decode(prop.value()), StandardCharsets.UTF_8))
                        .getAsJsonObject().getAsJsonObject("textures");
                if (textures != null && textures.has("SKIN")) {
                    JsonObject s = textures.getAsJsonObject("SKIN");
                    skin = s.get("url").getAsString();
                    slim = s.has("metadata") && s.getAsJsonObject("metadata").has("model") && s.getAsJsonObject("metadata").get("model").getAsString().equals("slim");
                }
                break;
            }
        } catch (Throwable ignored) {
            // a profile without textures (offline mode, a Bedrock player): the viewer draws the default skin
        }
        ResourceLocation held = BuiltInRegistries.ITEM.getKey(p.getMainHandItem().getItem());
        int health = (int) Math.max(0, Math.min(255, Math.round(p.getHealth() * 255.0 / Math.max(1f, p.getMaxHealth()))));
        return new Frames.PlayerSample(p.getId(), p.getUUID(), p.getGameProfile().getName(), skin, slim,
                p.gameMode.getGameModeForPlayer().getId(), key(held), health);
    }
}
