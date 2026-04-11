package com.spawndev.vrdetect;

import org.bukkit.entity.Player;
import org.bukkit.event.EventHandler;
import org.bukkit.event.Listener;
import org.bukkit.event.player.PlayerJoinEvent;
import org.bukkit.event.player.PlayerQuitEvent;
import org.bukkit.plugin.java.JavaPlugin;
import org.bukkit.plugin.messaging.PluginMessageListener;

import java.io.File;
import java.io.FileWriter;
import java.io.IOException;
import java.nio.file.Files;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;

/**
 * Tiny Paper plugin that detects Vivecraft VR players.
 * Listens on the "vivecraft:data" plugin channel.
 * When a VR player connects, their UUID is added to vr-players.json.
 * The AubsCraft Admin panel reads this file to show VR badges.
 */
public class VRDetectPlugin extends JavaPlugin implements Listener, PluginMessageListener {

    // Tracks currently online VR players
    private final Map<UUID, String> vrPlayers = new ConcurrentHashMap<>();
    // Tracks all-time VR players (persisted)
    private final Map<String, String> knownVRPlayers = new ConcurrentHashMap<>();
    private File dataFile;

    @Override
    public void onEnable() {
        dataFile = new File(getDataFolder(), "vr-players.json");

        // Load existing data
        loadData();

        // Register the Vivecraft plugin channel
        getServer().getMessenger().registerIncomingPluginChannel(this, "vivecraft:data", this);

        // Register events
        getServer().getPluginManager().registerEvents(this, this);

        getLogger().info("VRDetect enabled - listening for Vivecraft players");
    }

    @Override
    public void onDisable() {
        getServer().getMessenger().unregisterIncomingPluginChannel(this, "vivecraft:data");
        saveData();
        getLogger().info("VRDetect disabled");
    }

    @Override
    public void onPluginMessageReceived(String channel, Player player, byte[] message) {
        if (!"vivecraft:data".equals(channel)) return;

        // Any message on vivecraft:data means this is a VR player
        UUID uuid = player.getUniqueId();
        String name = player.getName();

        if (!vrPlayers.containsKey(uuid)) {
            vrPlayers.put(uuid, name);
            knownVRPlayers.put(uuid.toString(), name);
            getLogger().info("VR player detected: " + name + " (" + uuid + ")");
            saveData();
        }
    }

    @EventHandler
    public void onPlayerQuit(PlayerQuitEvent event) {
        vrPlayers.remove(event.getPlayer().getUniqueId());
        saveData();
    }

    private void saveData() {
        try {
            if (!dataFile.getParentFile().exists()) {
                dataFile.getParentFile().mkdirs();
            }

            // Write simple JSON manually (no Gson dependency needed)
            StringBuilder sb = new StringBuilder();
            sb.append("{\n");
            sb.append("  \"online\": {");
            boolean first = true;
            for (Map.Entry<UUID, String> entry : vrPlayers.entrySet()) {
                if (!first) sb.append(",");
                sb.append("\n    \"").append(entry.getKey()).append("\": \"").append(entry.getValue()).append("\"");
                first = false;
            }
            sb.append("\n  },\n");
            sb.append("  \"known\": {");
            first = true;
            for (Map.Entry<String, String> entry : knownVRPlayers.entrySet()) {
                if (!first) sb.append(",");
                sb.append("\n    \"").append(entry.getKey()).append("\": \"").append(entry.getValue()).append("\"");
                first = false;
            }
            sb.append("\n  }\n");
            sb.append("}\n");

            try (FileWriter writer = new FileWriter(dataFile)) {
                writer.write(sb.toString());
            }
        } catch (IOException e) {
            getLogger().warning("Failed to save VR player data: " + e.getMessage());
        }
    }

    private void loadData() {
        if (!dataFile.exists()) return;
        try {
            String content = new String(Files.readAllBytes(dataFile.toPath()));
            // Simple JSON parsing for the "known" section
            int knownStart = content.indexOf("\"known\"");
            if (knownStart < 0) return;
            int braceStart = content.indexOf('{', knownStart);
            int braceEnd = content.indexOf('}', braceStart + 1);
            if (braceStart < 0 || braceEnd < 0) return;

            String knownBlock = content.substring(braceStart + 1, braceEnd);
            // Parse "uuid": "name" pairs
            java.util.regex.Pattern p = java.util.regex.Pattern.compile("\"([^\"]+)\"\\s*:\\s*\"([^\"]+)\"");
            java.util.regex.Matcher m = p.matcher(knownBlock);
            while (m.find()) {
                knownVRPlayers.put(m.group(1), m.group(2));
            }
            getLogger().info("Loaded " + knownVRPlayers.size() + " known VR player(s)");
        } catch (IOException e) {
            getLogger().warning("Failed to load VR player data: " + e.getMessage());
        }
    }
}
