package com.spawndev.aubscraftlive;

import org.bukkit.World;
import org.bukkit.entity.Ageable;
import org.bukkit.entity.Axolotl;
import org.bukkit.entity.Cat;
import org.bukkit.entity.Entity;
import org.bukkit.entity.EntityType;
import org.bukkit.entity.FallingBlock;
import org.bukkit.entity.Fox;
import org.bukkit.entity.Frog;
import org.bukkit.entity.Horse;
import org.bukkit.entity.Item;
import org.bukkit.entity.ItemFrame;
import org.bukkit.entity.Painting;
import org.bukkit.Material;
import org.bukkit.entity.Llama;
import org.bukkit.entity.MushroomCow;
import org.bukkit.entity.Panda;
import org.bukkit.entity.Parrot;
import org.bukkit.entity.LivingEntity;
import org.bukkit.entity.Player;
import org.bukkit.entity.TropicalFish;
import org.bukkit.entity.Pose;
import org.bukkit.entity.Rabbit;
import org.bukkit.entity.Sheep;
import org.bukkit.entity.Shulker;
import org.bukkit.entity.Villager;
import org.bukkit.entity.Wolf;
import org.bukkit.plugin.java.JavaPlugin;
import org.bukkit.profile.PlayerTextures;
import org.bukkit.scheduler.BukkitTask;
import org.bukkit.util.Vector;

import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.logging.Level;

/**
 * AubsCraft Live: what is happening in the world, for the panel's 3D viewer. A few times a second it samples every entity in the loaded
 * chunks (animals, mobs, villagers, players...), encodes them in a compact binary frame ({@link Frames}, documented in FORMAT.md) and
 * POSTs it to the panel over loopback with a shared secret. It reads the world only; it changes nothing and sends nothing anywhere but
 * the panel URL in its config.
 *
 * Config (plugins/AubsCraftLive/config.yml, written by the panel when it installs the plugin): panel-url, server-id, secret,
 * interval-ticks, max-entities, include-items. With no secret the plugin does nothing.
 */
public final class LivePlugin extends JavaPlugin {
    private HttpClient http;
    private URI endpoint;
    private String secret;
    private int maxEntities;
    private boolean includeItems;
    /// <summary>When each player's last arm swing stops showing (System.nanoTime): a swing takes about a third of a second, a player who mines swings again and again.</summary>
    private static final java.util.concurrent.ConcurrentHashMap<Integer, Long> SWING_UNTIL = new java.util.concurrent.ConcurrentHashMap<>();
    private int maxItems;
    private BukkitTask task;
    private ChunkWatcher chunks;
    private final AtomicBoolean inFlight = new AtomicBoolean();
    private long sent, dropped, failed;
    private long lastReport;

    @Override
    public void onEnable() {
        saveDefaultConfig();
        secret = getConfig().getString("secret", "");
        String panel = getConfig().getString("panel-url", "http://127.0.0.1:5080");
        String serverId = getConfig().getString("server-id", "aubscraft");
        int interval = Math.max(1, getConfig().getInt("interval-ticks", 4));
        maxEntities = Math.max(1, getConfig().getInt("max-entities", 3000));
        includeItems = getConfig().getBoolean("include-items", true);
        maxItems = Math.max(0, getConfig().getInt("max-items", 500));

        if (secret == null || secret.isBlank()) {
            getLogger().warning("No secret in config.yml: AubsCraft Live is idle. Install it from the AubsCraft panel, which writes it.");
            return;
        }
        endpoint = URI.create(panel.replaceAll("/+$", "") + "/api/live/ingest/" + serverId);
        http = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(2)).build();
        // A player's arm swing (hitting, mining, placing, using): remembered for a third of a second, which is what a swing takes
        getServer().getPluginManager().registerEvents(new org.bukkit.event.Listener() {
            @org.bukkit.event.EventHandler(priority = org.bukkit.event.EventPriority.MONITOR, ignoreCancelled = false)
            public void onSwing(org.bukkit.event.player.PlayerAnimationEvent event) {
                SWING_UNTIL.put(event.getPlayer().getEntityId(), System.nanoTime() + 330_000_000L);
            }
        }, this);
        // Entities may only be touched from the main thread: a synchronous repeating task, never an async one
        task = getServer().getScheduler().runTaskTimer(this, this::sample, 40L, interval);
        // The blocks: the panel reads region files, which lag the running world; chunks that change are sent as they settle
        if (getConfig().getBoolean("chunk-updates", true)) {
            chunks = new ChunkWatcher(this, http, URI.create(panel.replaceAll("/+$", "") + "/api/live/chunk/" + serverId), secret);
            chunks.start();
        }
        getLogger().info("Sending the world's activity to " + endpoint + " every " + interval + " ticks");
    }

    @Override
    public void onDisable() {
        if (task != null) task.cancel();
    }

    private void sample() {
        // One frame in flight at a time: a slow panel loses frames, it does not queue them
        if (!inFlight.compareAndSet(false, true)) {
            dropped++;
            return;
        }
        byte[] frame;
        try {
            frame = buildFrame();
        } catch (Throwable t) {
            inFlight.set(false);
            getLogger().log(Level.WARNING, "Could not sample the world", t);
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
            if (failed > 0 && now - lastReport > 600_000) { // once in ten minutes, and only to say something is wrong
                lastReport = now;
                getLogger().warning("Live frames: " + sent + " sent, " + failed + " failed, " + dropped + " dropped (is the panel running at " + endpoint + "?)");
            }
        });
    }

    private byte[] buildFrame() {
        List<World> worlds = getServer().getWorlds();
        World first = worlds.isEmpty() ? null : worlds.get(0);
        int weather = 0;
        if (first != null) { if (first.hasStorm()) weather |= 1; if (first.isThundering()) weather |= 2; }

        List<Frames.WorldSample> samples = new ArrayList<>();
        int budget = maxEntities;
        int itemBudget = maxItems;
        for (World world : worlds) {
            List<Frames.EntitySample> entities = new ArrayList<>();
            List<Frames.PlayerSample> players = new ArrayList<>();
            for (Entity e : world.getEntities()) {
                if (budget <= 0) break;
                // Dropped items (there can be a great many, a farm's worth): only up to max-items of them, so they never crowd the creatures out
                if (e.getType() == EntityType.ITEM) {
                    if (!includeItems || itemBudget <= 0) continue;
                    itemBudget--;
                }
                if (!e.isValid()) continue;
                budget--;
                entities.add(sample(e));
                if (e instanceof Player p) players.add(sample(p));
            }
            samples.add(new Frames.WorldSample(world.getName(), entities, players));
        }
        // The clock of the frame is the world's GAME time (it ticks always); getFullTime() is the day time, which stands still when the server's day cycle is off, and a clock that stands still
        // stops the viewer's interpolation of the creatures and the removal of those that are gone
        return Frames.build(first == null ? 0 : first.getGameTime(), first == null ? 0 : first.getTime(), weather, samples);
    }

    private static Frames.EntitySample sample(Entity e) {
        var loc = e.getLocation();
        Vector v = e.getVelocity();
        int flags = 0;
        if (e.isOnGround()) flags |= Frames.FLAG_ON_GROUND;
        Pose pose = e.getPose();
        if (pose == Pose.SNEAKING) flags |= Frames.FLAG_SNEAKING;
        if (e instanceof Player p && p.isSprinting()) flags |= Frames.FLAG_SPRINTING;
        if (pose == Pose.SWIMMING) flags |= Frames.FLAG_SWIMMING;
        if (pose == Pose.FALL_FLYING) flags |= Frames.FLAG_GLIDING;
        if (pose == Pose.SLEEPING) flags |= Frames.FLAG_SLEEPING;
        if (e instanceof Ageable a && !a.isAdult()) flags |= Frames.FLAG_BABY;
        if (e.getFireTicks() > 0) flags |= Frames.FLAG_BURNING;
        if (e.isGlowing()) flags |= Frames.FLAG_GLOWING;
        if (e instanceof LivingEntity le && le.isInvisible()) flags |= Frames.FLAG_INVISIBLE;
        if (e instanceof Player) flags |= Frames.FLAG_PLAYER;
        if (e.isInsideVehicle()) flags |= Frames.FLAG_RIDING;
        if (e instanceof LivingEntity le2 && le2.isDead()) flags |= Frames.FLAG_DEAD;
        // Using what is held (eating, drinking, a bow drawn, a shield up); an arm swinging (a player's, from their last swing)
        if (e instanceof LivingEntity le4 && le4.isHandRaised()) flags |= Frames.FLAG_USING;
        if (e instanceof Player sp && System.nanoTime() < SWING_UNTIL.getOrDefault(sp.getEntityId(), 0L)) flags |= Frames.FLAG_SWINGING;
        String name = customName(e);
        if (name != null) flags |= Frames.FLAG_HAS_NAME;
        // The location's yaw is the head's; a living entity's body has its own
        float body = e instanceof LivingEntity le3 ? le3.getBodyYaw() : loc.getYaw();
        return new Frames.EntitySample(e.getEntityId(), typeKey(e.getType().getKey().getNamespace(), e.getType().getKey().getKey()),
                loc.getX(), loc.getY(), loc.getZ(), loc.getYaw(), loc.getPitch(), body, flags, v.getX(), v.getY(), v.getZ(), name, variantOf(e));
    }

    private static Frames.PlayerSample sample(Player p) {
        String skin = "";
        boolean slim = false;
        try {
            PlayerTextures textures = p.getPlayerProfile().getTextures();
            if (textures.getSkin() != null) skin = textures.getSkin().toString();
            slim = textures.getSkinModel() == PlayerTextures.SkinModel.SLIM;
        } catch (Throwable ignored) {
            // a profile without textures (offline mode, a Bedrock player): the viewer draws the default skin
        }
        var held = p.getInventory().getItemInMainHand().getType().getKey();
        int health = (int) Math.max(0, Math.min(255, Math.round(p.getHealth() * 255.0 / 20.0)));
        return new Frames.PlayerSample(p.getEntityId(), p.getUniqueId(), p.getName(), skin, slim, p.getGameMode().ordinal(),
                typeKey(held.getNamespace(), held.getKey()), health);
    }

    // "minecraft:cow" -> "cow"; another namespace keeps it ("mymod:thing")
    private static String typeKey(String namespace, String key) {
        return namespace == null || namespace.equals("minecraft") ? key : namespace + ":" + key;
    }

    @SuppressWarnings("deprecation")
    private static String customName(Entity e) {
        String n = e.getCustomName();
        if (n == null || n.isEmpty()) return null;
        return n.length() > 64 ? n.substring(0, 64) : n;
    }

    // The armor a player wears: helmet|chestplate|leggings|boots, each the material (diamond, iron, ...), leather as leather#rrggbb, nothing empty
    private static String armorOf(Player p) {
        var inv = p.getInventory();
        String[] pieces = { armorPiece(inv.getHelmet(), "_helmet"), armorPiece(inv.getChestplate(), "_chestplate"), armorPiece(inv.getLeggings(), "_leggings"), armorPiece(inv.getBoots(), "_boots") };
        boolean any = false;
        for (String s : pieces) if (!s.isEmpty()) any = true;
        return any ? String.join("|", pieces) : null;
    }

    private static String armorPiece(org.bukkit.inventory.ItemStack stack, String suffix) {
        if (stack == null) return "";
        String name = stack.getType().getKey().getKey();
        if (!name.endsWith(suffix)) return "";
        String material = name.substring(0, name.length() - suffix.length());
        if (material.equals("turtle")) return "turtle_scute";
        if (material.equals("leather") && stack.getItemMeta() instanceof org.bukkit.inventory.meta.LeatherArmorMeta meta) {
            org.bukkit.Color c = meta.getColor();
            return String.format("leather#%02X%02X%02X", c.getRed(), c.getGreen(), c.getBlue());
        }
        return material;
    }

    private static String variantOf(Entity e) {
        if (e instanceof Player player) return armorOf(player);
        // A painting: which one; an item frame: what is in it
        if (e instanceof Painting painting) {
            var key = painting.getArt().getKey();
            return typeKey(key.getNamespace(), key.getKey());
        }
        if (e instanceof ItemFrame frame) {
            Material held = frame.getItem().getType();
            if (held == Material.AIR) return null;
            var key = held.getKey();
            return typeKey(key.getNamespace(), key.getKey());
        }
        // A falling block: which block (the viewer draws a cube of it)
        if (e instanceof FallingBlock fb) {
            var key = fb.getBlockData().getMaterial().getKey();
            return typeKey(key.getNamespace(), key.getKey());
        }
        // A dropped item: what it is (the viewer draws its picture)
        if (e instanceof Item item) {
            var key = item.getItemStack().getType().getKey();
            return typeKey(key.getNamespace(), key.getKey());
        }
        if (e instanceof Sheep s) return s.getColor() == null ? "white" : s.getColor().name().toLowerCase();
        if (e instanceof Cat c) return c.getCatType().getKey().getKey();
        if (e instanceof Rabbit r) return r.getRabbitType().name().toLowerCase();
        if (e instanceof Fox f) return f.getFoxType().name().toLowerCase();
        if (e instanceof Wolf w) return w.getVariant().getKey().getKey();
        if (e instanceof Villager v) return v.getProfession().getKey().getKey();
        // A horse: "color/markings" (the viewer draws the coat and the markings over it); a llama or trader llama: its colour
        if (e instanceof Horse h) return h.getColor().name().toLowerCase() + "/" + h.getStyle().name().toLowerCase();
        if (e instanceof Llama l) return l.getColor().name().toLowerCase();
        if (e instanceof Parrot p) return p.getVariant().name().toLowerCase();
        if (e instanceof MushroomCow m) return m.getVariant().name().toLowerCase();
        // An axolotl's colour, a frog's biome, a panda's gene (a recessive one only shows when both genes are it), a shulker's dye
        if (e instanceof Axolotl a) return a.getVariant().name().toLowerCase();
        if (e instanceof Frog f) return f.getVariant().getKey().getKey();
        if (e instanceof Panda p) {
            Panda.Gene main = p.getMainGene(), hidden = p.getHiddenGene();
            return (main.isRecessive() && main != hidden ? Panda.Gene.NORMAL : main).name().toLowerCase();
        }
        if (e instanceof Shulker s) return s.getColor() == null ? null : s.getColor().name().toLowerCase();
        // A tropical fish: "pattern|body colour|pattern colour"
        if (e instanceof TropicalFish t) return t.getPattern().name().toLowerCase() + "|" + t.getBodyColor().name().toLowerCase() + "|" + t.getPatternColor().name().toLowerCase();
        return null;
    }
}
