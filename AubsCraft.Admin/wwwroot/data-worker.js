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
    }
};

// ---- Render worker message handler ----
function onRenderMessage(e) {
    const msg = e.data;
    console.log('[DataWorker] Received from render:', msg?.type, 'queue:', chunkQueue.length);
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

// ---- OPFS Cache ----
let opfsRoot = null;
let opfsCacheDir = null;

async function getOPFSCacheDir() {
    if (opfsCacheDir) return opfsCacheDir;
    try {
        opfsRoot = await navigator.storage.getDirectory();
        const cacheDir = await opfsRoot.getDirectoryHandle('aubscraft-cache', { create: true });
        opfsCacheDir = await cacheDir.getDirectoryHandle('heightmaps', { create: true });
        return opfsCacheDir;
    } catch (ex) {
        console.log('[DataWorker] OPFS not available:', ex.message);
        return null;
    }
}

async function cacheChunk(cx, cz, buffer) {
    try {
        const dir = await getOPFSCacheDir();
        if (!dir) return;
        // Region-based file: r.{rx}.{rz}.bin
        const rx = cx >> 5;
        const rz = cz >> 5;
        const fileName = `r.${rx}.${rz}.bin`;

        // Simple append-based cache: each chunk is [int32 cx][int32 cz][int32 len][bytes]
        const handle = await dir.getFileHandle(fileName, { create: true });
        const writable = await handle.createWritable({ keepExistingData: true });
        const file = await handle.getFile();
        const header = new ArrayBuffer(12);
        const hv = new DataView(header);
        hv.setInt32(0, cx, true);
        hv.setInt32(4, cz, true);
        hv.setInt32(8, buffer.byteLength, true);
        await writable.seek(file.size);
        await writable.write(header);
        await writable.write(buffer);
        await writable.close();
    } catch (ex) {
        // Cache write failure is not critical
    }
}

async function loadOPFSCache() {
    try {
        const dir = await getOPFSCacheDir();
        if (!dir) return;

        let totalChunks = 0;
        const startTime = performance.now();

        for await (const [name, handle] of dir) {
            if (!name.startsWith('r.') || !name.endsWith('.bin')) continue;
            if (handle.kind !== 'file') continue;

            const file = await handle.getFile();
            const data = await file.arrayBuffer();
            const view = new DataView(data);
            let offset = 0;

            while (offset + 12 <= data.byteLength) {
                const cx = view.getInt32(offset, true);
                const cz = view.getInt32(offset + 4, true);
                const len = view.getInt32(offset + 8, true);
                offset += 12;

                if (offset + len > data.byteLength) break;

                const key = cx + ',' + cz;
                if (!sentChunks.has(key)) {
                    // Copy chunk data to its own ArrayBuffer (so it can be transferred)
                    const chunkBuffer = data.slice(offset, offset + len);
                    const dx = cx - cameraChunkX;
                    const dz = cz - cameraChunkZ;
                    chunkQueue.push({ cx, cz, dist: dx * dx + dz * dz, buffer: chunkBuffer });
                    totalChunks++;
                }
                offset += len;
            }
        }

        if (totalChunks > 0) {
            resortQueue();
            const elapsed = (performance.now() - startTime).toFixed(0);
            console.log(`[DataWorker] Loaded ${totalChunks} chunks from OPFS cache in ${elapsed}ms`);
            // Try to send immediately if render worker is ready
            sendNextChunk();
        }
    } catch (ex) {
        console.log('[DataWorker] OPFS cache load error:', ex.message);
    }
}
