package com.spawndev.aubscraftlive;

import de.maxhenkel.voicechat.api.BukkitVoicechatService;
import de.maxhenkel.voicechat.api.VoicechatApi;
import de.maxhenkel.voicechat.api.VoicechatConnection;
import de.maxhenkel.voicechat.api.VoicechatPlugin;
import de.maxhenkel.voicechat.api.VoicechatServerApi;
import de.maxhenkel.voicechat.api.audiochannel.LocationalAudioChannel;
import de.maxhenkel.voicechat.api.events.EventRegistration;
import de.maxhenkel.voicechat.api.events.MicrophonePacketEvent;
import de.maxhenkel.voicechat.api.events.VoicechatServerStartedEvent;
import org.bukkit.Bukkit;
import org.bukkit.entity.Player;
import org.bukkit.plugin.java.JavaPlugin;

import java.io.ByteArrayOutputStream;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.WebSocket;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionStage;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.TimeUnit;
import java.util.logging.Level;

/**
 * Voice between the panel's viewers and the players of the server, through Simple Voice Chat (loaded only when that plugin is there). Players' microphones (the voice chat's own Opus packets) are sent
 * to the panel, which plays them to the viewers - only while some viewer listens, so a quiet panel costs nothing. What a viewer says comes back from the panel with the place it speaks from, and is sent
 * into a LOCATIONAL audio channel of the voice chat at that place: the players near it hear it as they hear a player. One channel for each web speaker, closed after a few seconds of silence. The wire format
 * is in <code>VoiceFrames.cs</code> (little-endian): 1 mic (panel to here), 2 voice (panel to browsers), 3 listeners, 4 left, 5 player voice (here to panel).
 */
final class VoiceBridge implements VoicechatPlugin {
    private static final byte MIC = 1, LISTENERS = 3, LEFT = 4, PLAYER_VOICE = 5;

    private final JavaPlugin plugin;
    private final HttpClient http;
    private final URI uri;
    private final String secret;
    private volatile VoicechatServerApi api;
    private volatile WebSocket socket;
    private volatile boolean wanted, running = true, sending;
    private volatile int attempts;
    private final ConcurrentHashMap<Integer, Speaker> speakers = new ConcurrentHashMap<>();
    private final ConcurrentHashMap<UUID, double[]> positions = new ConcurrentHashMap<>();

    private static final class Speaker {
        final LocationalAudioChannel channel;
        volatile long lastFrame = System.currentTimeMillis();
        Speaker(LocationalAudioChannel channel) { this.channel = channel; }
    }

    private VoiceBridge(JavaPlugin plugin, HttpClient http, String panel, String serverId, String secret) {
        this.plugin = plugin;
        this.http = http;
        this.uri = URI.create(panel.replaceFirst("^http", "ws") + "/api/live/voice/plugin/" + serverId);
        this.secret = secret;
    }

    /** Hook into the voice chat if it is installed. */
    static void register(JavaPlugin plugin, HttpClient http, String panel, String serverId, String secret) {
        BukkitVoicechatService service = Bukkit.getServicesManager().load(BukkitVoicechatService.class);
        if (service == null) {
            plugin.getLogger().info("Simple Voice Chat is not running here: no voice in the viewer");
            return;
        }
        VoiceBridge bridge = new VoiceBridge(plugin, http, panel, serverId, secret);
        service.registerPlugin(bridge);
        bridge.start();
        plugin.getLogger().info("Voice: connected to Simple Voice Chat; viewers will hear players and be heard");
    }

    private void start() {
        // Where the players are, for the voice that is sent to the viewers (a microphone packet comes on another thread than the game's)
        plugin.getServer().getScheduler().runTaskTimer(plugin, () -> {
            for (Player p : Bukkit.getOnlinePlayers()) {
                var l = p.getEyeLocation();
                positions.put(p.getUniqueId(), new double[] { l.getX(), l.getY(), l.getZ() });
            }
        }, 20L, 5L);
        // A web speaker who has gone quiet loses their channel
        plugin.getServer().getScheduler().runTaskTimerAsynchronously(plugin, () -> {
            long now = System.currentTimeMillis();
            speakers.entrySet().removeIf(e -> {
                if (now - e.getValue().lastFrame < 8000) return false;
                try { e.getValue().channel.flush(); } catch (Throwable ignored) { }
                return true;
            });
        }, 100L, 100L);
        connect();
    }

    // ---- the voice chat's side ----

    @Override public String getPluginId() { return "aubscraft_live"; }

    @Override public void initialize(VoicechatApi voicechat) { if (voicechat instanceof VoicechatServerApi s) api = s; }

    @Override public void registerEvents(EventRegistration registration) {
        registration.registerEvent(VoicechatServerStartedEvent.class, e -> api = e.getVoicechat());
        registration.registerEvent(MicrophonePacketEvent.class, this::onMicrophone);
    }

    private void onMicrophone(MicrophonePacketEvent event) {
        WebSocket ws = socket;
        if (!wanted || ws == null || sending) return;     // nobody listens, or the last frame is still going out: voice is not worth waiting for
        VoicechatConnection from = event.getSenderConnection();
        if (from == null || !(from.getPlayer().getPlayer() instanceof Player player)) return;
        double[] at = positions.get(player.getUniqueId());
        if (at == null) return;
        byte[] opus = event.getPacket().getOpusEncodedData();
        byte[] name = player.getName().getBytes(StandardCharsets.UTF_8);
        ByteBuffer b = ByteBuffer.allocate(2 + name.length + 12 + 1 + opus.length).order(ByteOrder.LITTLE_ENDIAN);
        b.put(PLAYER_VOICE).put((byte) name.length).put(name);
        b.putFloat((float) at[0]).putFloat((float) at[1]).putFloat((float) at[2]);
        b.put((byte) (event.getPacket().isWhispering() ? 1 : 0)).put(opus);
        b.flip();
        sending = true;
        ws.sendBinary(b, true).whenComplete((w, t) -> sending = false);
    }

    // ---- the panel's side ----

    private void connect() {
        if (!running) return;
        http.newWebSocketBuilder().header("X-Live-Secret", secret).connectTimeout(java.time.Duration.ofSeconds(5)).buildAsync(uri, new Listener()).whenComplete((ws, error) -> {
            if (error != null) { retry(); return; }
            socket = ws;
            attempts = 0;
        });
    }

    private void retry() {
        socket = null;
        wanted = false;
        if (!running) return;
        long delay = Math.min(30_000L, 1000L << Math.min(attempts++, 5));
        CompletableFuture.runAsync(this::connect, CompletableFuture.delayedExecutor(delay, TimeUnit.MILLISECONDS));
    }

    private final class Listener implements WebSocket.Listener {
        private final ByteArrayOutputStream pending = new ByteArrayOutputStream();

        @Override public void onOpen(WebSocket ws) { ws.request(1); }

        @Override public CompletionStage<?> onBinary(WebSocket ws, ByteBuffer data, boolean last) {
            byte[] chunk = new byte[data.remaining()];
            data.get(chunk);
            pending.write(chunk, 0, chunk.length);
            if (last) {
                byte[] frame = pending.toByteArray();
                pending.reset();
                try { handle(frame); } catch (Throwable t) { plugin.getLogger().log(Level.FINE, "A voice frame was not understood", t); }
            }
            ws.request(1);
            return null;
        }

        @Override public CompletionStage<?> onClose(WebSocket ws, int statusCode, String reason) { retry(); return null; }

        @Override public void onError(WebSocket ws, Throwable error) { retry(); }
    }

    private void handle(byte[] frame) {
        if (frame.length < 2) return;
        ByteBuffer b = ByteBuffer.wrap(frame).order(ByteOrder.LITTLE_ENDIAN);
        switch (b.get()) {
            case LISTENERS -> wanted = (b.get() & 0xFF) > 0;
            case LEFT -> {
                Speaker s = speakers.remove(b.getShort() & 0xFFFF);
                if (s != null) try { s.channel.flush(); } catch (Throwable ignored) { }
            }
            case MIC -> {
                VoicechatServerApi voice = api;
                if (voice == null || frame.length < 3 + 12 + 1 + 1) return;
                int id = b.getShort() & 0xFFFF;
                float x = b.getFloat(), y = b.getFloat(), z = b.getFloat();
                b.get();   // flags
                byte[] opus = new byte[b.remaining()];
                b.get(opus);
                var position = voice.createPosition(x, y, z);
                Speaker speaker = speakers.computeIfAbsent(id, i -> {
                    var world = Bukkit.getWorlds().get(0);
                    var channel = voice.createLocationalAudioChannel(UUID.nameUUIDFromBytes(("aubscraft-web-" + i).getBytes(StandardCharsets.UTF_8)), voice.fromServerLevel(world), position);
                    if (channel == null) return null;
                    channel.setDistance((float) voice.getVoiceChatDistance());
                    channel.setCategory("aubscraft_web");
                    return new Speaker(channel);
                });
                if (speaker == null) return;
                speaker.channel.updateLocation(position);
                speaker.channel.send(opus);
                speaker.lastFrame = System.currentTimeMillis();
            }
            default -> { }
        }
    }
}
