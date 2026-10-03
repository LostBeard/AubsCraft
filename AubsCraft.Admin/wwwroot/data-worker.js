// AubsCraft Data Worker - Plain JS, no .NET runtime
// Handles: WebSocket chunk streaming, OPFS cache, binary parsing, queue sorting
// Communicates with Render Worker (Blazor) via MessagePort (pull-based)

let renderPort = null;
let ws = null;
let renderReady = false;
let cameraChunkX = 0;
let cameraChunkZ = 0;
let receivedCount = 0;

// Priority queue of parsed chunks, sorted by camera distance
const chunkQueue = [];
// Track which chunks are already loaded (don't re-send)
const sentChunks = new Set();

// ---- Main thread message handler ----
self.onmessage = (e) => {
    const msg = e.data;
    if (msg.type === 'init') {
        renderPort = msg.renderPort;
        renderPort.onmessage = onRenderMessage;
        connectWebSocket(msg.wsUrl);
        // OPFS cache read handled by Blazor render worker (different format)
        // Data worker handles WebSocket streaming only
        console.log('[DataWorker] Initialized, connecting to', msg.wsUrl);
    }
    else if (msg.type === 'camera') {
        cameraChunkX = Math.floor(msg.x / 16);
        cameraChunkZ = Math.floor(msg.z / 16);
        resortQueue();
        // The server re-sorts its send queue around this position (before open, onopen sends it)
        if (ws && ws.readyState === WebSocket.OPEN)
            ws.send(JSON.stringify({ x: msg.x, z: msg.z }));
    }
};

// ---- Render worker message handler ----
function onRenderMessage(e) {
    const msg = e.data;
    // Debug: uncomment to trace port messages
    // console.log('[DataWorker] Received from render:', msg?.type, 'queue:', chunkQueue.length);
    if (msg.type === 'ready') {
        renderReady = true;
        sendNextChunk();
    }
}

// ---- Send highest priority chunk to render worker ----
function sendNextChunk() {
    if (!renderReady || chunkQueue.length === 0) return;
    renderReady = false;

    const chunk = chunkQueue.shift();
    const key = chunk.cx + ',' + chunk.cz;
    sentChunks.add(key);

    // Transfer the ArrayBuffer - zero copy to render worker
    renderPort.postMessage({
        type: 'heightmap',
        cx: chunk.cx,
        cz: chunk.cz,
        buffer: chunk.buffer
    }, [chunk.buffer]);
}

// ---- WebSocket connection ----
function connectWebSocket(url) {
    ws = new WebSocket(url);
    ws.binaryType = 'arraybuffer';

    ws.onopen = () => {
        console.log('[DataWorker] WebSocket connected');
        // Send initial camera position
        ws.send(JSON.stringify({ x: cameraChunkX * 16, z: cameraChunkZ * 16 }));
    };

    ws.onmessage = (e) => {
        if (!(e.data instanceof ArrayBuffer) || e.data.byteLength < 8) return;

        const view = new DataView(e.data);
        const cx = view.getInt32(0, true); // little-endian
        const cz = view.getInt32(4, true);

        const key = cx + ',' + cz;
        if (sentChunks.has(key)) return; // already sent to render worker

        receivedCount++;
        if (receivedCount <= 3 || receivedCount % 500 === 0) {
            console.log(`[DataWorker] Chunk (${cx},${cz}) received, total: ${receivedCount}`);
        }

        // OPFS caching handled by render worker (C# format)

        // Queue for render worker (sorted by camera distance)
        const dx = cx - cameraChunkX;
        const dz = cz - cameraChunkZ;
        chunkQueue.push({
            cx, cz,
            dist: dx * dx + dz * dz,
            buffer: e.data
        });

        // Insert sorted (binary insert would be faster but this is fine for now)
        // Re-sort periodically instead of every insert
        if (chunkQueue.length % 50 === 0) resortQueue();

        // Try to send if render worker is ready
        sendNextChunk();
    };

    ws.onclose = (e) => {
        console.log(`[DataWorker] WebSocket closed: code=${e.code} reason=${e.reason}`);
        // Final sort and flush
        resortQueue();
        sendNextChunk();
    };

    ws.onerror = () => {
        console.log('[DataWorker] WebSocket error');
    };
}

// ---- Sort queue by camera distance ----
function resortQueue() {
    for (let i = 0; i < chunkQueue.length; i++) {
        const c = chunkQueue[i];
        const dx = c.cx - cameraChunkX;
        const dz = c.cz - cameraChunkZ;
        c.dist = dx * dx + dz * dz;
    }
    chunkQueue.sort((a, b) => a.dist - b.dist);
}

// The OPFS chunk cache lives in the render worker (WorldCacheService, one folder per server).
