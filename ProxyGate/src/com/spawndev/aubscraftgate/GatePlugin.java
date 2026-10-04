package com.spawndev.aubscraftgate;

import com.google.inject.Inject;
import com.velocitypowered.api.event.Subscribe;
import com.velocitypowered.api.event.player.ServerPreConnectEvent;
import com.velocitypowered.api.plugin.PluginContainer;
import com.velocitypowered.api.plugin.annotation.DataDirectory;
import com.velocitypowered.api.proxy.Player;
import com.velocitypowered.api.proxy.ProxyServer;
import net.kyori.adventure.text.Component;
import net.kyori.adventure.text.format.NamedTextColor;
import org.slf4j.Logger;

import java.io.IOException;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Arrays;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Map;
import java.util.Optional;
import java.util.Set;
import java.util.UUID;

/**
 * AubsCraft's gate on the proxy: Bedrock players (Geyser + Floodgate) cannot play servers that need Java Edition
 * client mods, so a switch to one of those - by portal, /server, or anything else - is refused here with a message,
 * and the player stays where they are.
 *
 * The AubsCraft panel writes the Java-only servers to plugins/aubscraft-gate/java-only.txt ("id<TAB>Name" per
 * line). The file is read on each switch, so it never needs a reload.
 */
public class GatePlugin {
    private final ProxyServer proxy;
    private final Logger logger;
    private final Path javaOnlyFile;
    // Test hook (never set in production): players whose names are listed here count as Bedrock players,
    // because a test client cannot connect through Geyser.
    private final Set<String> testBedrockNames = new HashSet<>();

    @Inject
    public GatePlugin(ProxyServer proxy, Logger logger, @DataDirectory Path dataDirectory) {
        this.proxy = proxy;
        this.logger = logger;
        this.javaOnlyFile = dataDirectory.resolve("java-only.txt");
        String names = System.getProperty("aubscraft.gate.testBedrockNames", "");
        for (String n : names.split(",")) if (!n.isBlank()) testBedrockNames.add(n.trim().toLowerCase());
    }

    @Subscribe
    public void onServerPreConnect(ServerPreConnectEvent event) {
        Player player = event.getPlayer();
        String target = event.getOriginalServer().getServerInfo().getName();
        Map<String, String> javaOnly = readJavaOnly();
        if (!javaOnly.containsKey(target.toLowerCase())) return;
        if (!isBedrock(player)) return;

        event.setResult(ServerPreConnectEvent.ServerResult.denied());
        String name = javaOnly.get(target.toLowerCase());
        player.sendMessage(Component.text(name + " needs Java Edition mods (a PC or a Quest headset), so it can't be played from Bedrock.",
                NamedTextColor.GOLD));
        logger.info("Kept Bedrock player {} off Java-only server {}", player.getUsername(), target);
    }

    /** Server id (lower case) to display name. */
    private Map<String, String> readJavaOnly() {
        Map<String, String> servers = new HashMap<>();
        if (!Files.exists(javaOnlyFile)) return servers;
        try {
            for (String line : Files.readAllLines(javaOnlyFile, StandardCharsets.UTF_8)) {
                line = line.trim();
                if (line.isEmpty() || line.startsWith("#")) continue;
                String[] parts = line.split("\t", 2);
                servers.put(parts[0].trim().toLowerCase(), parts.length > 1 ? parts[1].trim() : parts[0].trim());
            }
        } catch (IOException e) {
            logger.warn("Could not read {}: {}", javaOnlyFile, e.getMessage());
        }
        return servers;
    }

    /**
     * Floodgate's own answer (FloodgateApi.getInstance().isFloodgatePlayer(uuid)): true for every Bedrock player,
     * linked accounts included. Called through Floodgate's class loader, so this plugin has no build dependency on it.
     */
    private boolean isBedrock(Player player) {
        if (testBedrockNames.contains(player.getUsername().toLowerCase())) return true;
        Optional<PluginContainer> floodgate = proxy.getPluginManager().getPlugin("floodgate");
        if (floodgate.isEmpty() || floodgate.get().getInstance().isEmpty()) return false;
        try {
            ClassLoader loader = floodgate.get().getInstance().get().getClass().getClassLoader();
            Class<?> api = Class.forName("org.geysermc.floodgate.api.FloodgateApi", true, loader);
            Object instance = api.getMethod("getInstance").invoke(null);
            Method check = api.getMethod("isFloodgatePlayer", UUID.class);
            return (Boolean) check.invoke(instance, player.getUniqueId());
        } catch (ReflectiveOperationException e) {
            logger.warn("Floodgate check failed: {}", e.toString());
            return false;
        }
    }
}
