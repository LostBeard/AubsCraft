package com.spawndev.aubscraftlive;

import org.bukkit.plugin.java.JavaPlugin;

import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Duration;
import java.util.logging.Level;

/**
 * The panel's way to ask the server for edits: a long poll. The plugin asks <code>GET /api/live/commands/{serverId}</code> and the panel answers when it has commands for it (or after about
 * half a minute with none); each answer is a JSON array of commands, handed to {@link Edits} on the main thread, and each command's result goes back with
 * <code>POST /api/live/commands/{serverId}/result</code>. The plugin makes every connection (the panel never connects in), over loopback with the shared secret, like the frames.
 */
final class EditChannel {
    private final JavaPlugin plugin;
    private final HttpClient http;
    private final URI commands, results;
    private final String secret;
    private final Edits edits;
    private volatile boolean running;
    private int failures;

    EditChannel(JavaPlugin plugin, HttpClient http, String panel, String serverId, String secret, Edits edits) {
        this.plugin = plugin;
        this.http = http;
        this.commands = URI.create(panel + "/api/live/commands/" + serverId);
        this.results = URI.create(panel + "/api/live/commands/" + serverId + "/result");
        this.secret = secret;
        this.edits = edits;
    }

    void start() {
        running = true;
        edits.start(this::sendResult);
        poll();
    }

    void stop() { running = false; }

    private void poll() {
        if (!running) return;
        HttpRequest request = HttpRequest.newBuilder(commands)
                .timeout(Duration.ofSeconds(40))
                .header("X-Live-Secret", secret)
                .GET()
                .build();
        http.sendAsync(request, HttpResponse.BodyHandlers.ofByteArray()).whenComplete((response, error) -> {
            if (error != null || response.statusCode() / 100 != 2) {
                // The panel is not there (restarting, not up yet): ask again soon, less often the longer it stays away
                long delay = Math.min(30_000L, 1000L << Math.min(failures++, 5));
                later(delay);
                return;
            }
            failures = 0;
            if (response.statusCode() == 200 && response.body().length >= 2) {
                try {
                    // u16 count, then for each command an i32 length and its bytes (little-endian)
                    java.nio.ByteBuffer in = java.nio.ByteBuffer.wrap(response.body()).order(java.nio.ByteOrder.LITTLE_ENDIAN);
                    int count = in.getShort() & 0xFFFF;
                    for (int i = 0; i < count; i++) {
                        byte[] cmd = new byte[in.getInt()];
                        in.get(cmd);
                        plugin.getServer().getScheduler().runTask(plugin, () -> edits.submit(cmd));
                    }
                } catch (Throwable t) {
                    plugin.getLogger().log(Level.WARNING, "Could not read the panel's commands", t);
                }
            }
            poll();
        });
    }

    private void later(long millis) {
        if (!running) return;
        CompletableDelay.run(millis, this::poll);
    }

    private void sendResult(String result) {
        HttpRequest request = HttpRequest.newBuilder(results)
                .timeout(Duration.ofSeconds(10))
                .header("Content-Type", "application/json")
                .header("X-Live-Secret", secret)
                .POST(HttpRequest.BodyPublishers.ofString(result))
                .build();
        http.sendAsync(request, HttpResponse.BodyHandlers.discarding()).whenComplete((response, error) -> {
            if (error != null || response.statusCode() / 100 != 2)
                plugin.getLogger().warning("Could not report an edit's result to the panel");
        });
    }

    /** A delay without a thread of its own. */
    private static final class CompletableDelay {
        static void run(long millis, Runnable task) {
            java.util.concurrent.CompletableFuture.runAsync(task, java.util.concurrent.CompletableFuture.delayedExecutor(millis, java.util.concurrent.TimeUnit.MILLISECONDS));
        }
    }
}
