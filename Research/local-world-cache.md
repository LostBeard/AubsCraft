# Local World Cache - Design

**Date:** 2026-04-12
**Status:** Captain's request, design ready

---

## Problem

1. **BUG:** When the server goes down or is unreachable, blocks vanish from the viewer. The code assumes empty/failed responses mean "blocks are gone" instead of "server is unreachable."
2. **SLOW STARTUP:** Every session re-downloads the entire world from scratch via SignalR streaming. For 3000+ chunks, this takes time even on a local network.

## Solution: Client-Side World Cache

Cache all chunk data in the browser using OPFS (Origin Private File System). Load cached data first, update from server when available, never delete cached data just because the server is unreachable.

---

## Architecture

### Storage: IndexedDB (via SpawnDev.BlazorJS IDBDatabase wrappers)

**IndexedDB vs OPFS - both are viable. Pick based on access pattern.**

IndexedDB strengths:
- Purpose-built for key-value storage (chunk key = "0,5", value = binary blob)
- Single database, indexed lookups
- Batched transactions: read/write multiple chunks in one transaction
- Built-in cursor iteration for "load all cached chunks"

OPFS strengths:
- File-based, intuitive for chunk-per-file storage
- Async access from main thread, sync access available in workers
- Potentially better for very large individual blobs

Both have async access from the main thread. Both persist across sessions. SpawnDev.BlazorJS has typed wrappers for both:
- IndexedDB: `IDBFactory`, `IDBDatabase`, `IDBObjectStore`
- OPFS: `FileSystemFileHandle`, `FileSystemSyncAccessHandle`

**Recommendation:** Measure both with real chunk data before committing. Write a quick benchmark - 3000 keyed blob writes + reads - and pick the winner. Don't guess on performance.

### IndexedDB Schema

```
Database: "aubscraft-world-cache"
  Object Store: "heightmaps"
    Key: "x,z" (string)
    Value: { blocks: Uint16Array, heights: Int32Array, palette: string[], timestamp: number }

  Object Store: "chunks"
    Key: "x,z" (string)
    Value: { blocks: Uint16Array, palette: string[], timestamp: number }

  Object Store: "meta"
    Key: "manifest"
    Value: { lastFullSync: number, chunkCount: number, serverVersion: string }
```

### Load Sequence

```
1. READ CACHE FIRST (instant)
   - Read manifest.json from OPFS
   - Load all cached heightmaps -> render immediately
   - Load cached full chunks near camera -> render immediately
   - User sees the world within seconds, even if server is offline

2. CONNECT TO SERVER (background)
   - Open SignalR connection
   - If connection fails: stay on cached data, retry periodically
   - If connection succeeds: proceed to step 3

3. INCREMENTAL UPDATE (streaming)
   - Request chunk timestamps/hashes from server
   - Compare against cache manifest
   - Only download chunks that changed since last cache
   - Update cache as new data arrives
   - Replace rendered meshes with fresh data

4. BACKGROUND VERIFICATION (low priority)
   - When idle (FPS > 50, no user input), verify random cached chunks
   - Compare hash with server hash
   - Re-download any mismatches
   - Eventually entire cache is verified
```

### Server Disconnect Handling

**CRITICAL FIX:** Never delete or clear rendered chunks when the server is unreachable.

```csharp
// WRONG (current behavior):
// Server returns error/empty -> clear the chunk -> blocks vanish

// RIGHT:
// Server returns error/empty -> keep cached/rendered data -> retry later
// Only update a chunk when we get VALID new data from the server
```

The viewer should track connection state:
- `Connected` - live updates flowing
- `Disconnected` - using cache, retry in background
- `Stale` - connected but haven't verified all chunks yet

Show connection status in the UI (small indicator).

---

## Server-Side Changes (minimal)

The server needs ONE new endpoint or hub method:

```csharp
// Returns chunk timestamps so the client knows what's stale
public Dictionary<string, long> GetChunkTimestamps()
{
    // Returns: { "0,0": 1712345678, "0,1": 1712345690, ... }
    // Client compares against cached timestamps
    // Only requests chunks where server timestamp > cache timestamp
}
```

This is lightweight - just file modification times from the region files, not data.

Alternatively, use ETags or Last-Modified headers on the existing `/api/world/chunk/{x}/{z}` endpoint.

---

## Implementation Plan

### Phase 1: Fix the vanishing bug (immediate)
- Don't clear rendered chunks on server error
- Add connection state tracking
- Retry failed requests instead of treating them as empty

### Phase 2: Basic cache (next)
- Cache chunks to OPFS as they stream in
- On startup, load from OPFS first
- Re-stream from server to update

### Phase 3: Incremental updates
- Server-side chunk timestamp endpoint
- Client compares timestamps, only downloads changed chunks
- Background verification

### Phase 4: Offline mode
- Full offline support - viewer works entirely from cache
- Show "offline" indicator
- Auto-reconnect when server comes back

---

## Performance Notes

- OPFS read speed is ~100-500 MB/s for sequential access
- 3000 heightmap chunks at ~2KB each = ~6MB total cache
- 100 full 3D chunks at ~200KB each = ~20MB cache
- Total cache: well under 100MB, browser OPFS handles this easily
- First load from cache: sub-second for heightmaps, 1-2s for full chunks
- SpawnDev.BlazorJS OPFS wrappers avoid any raw JS interop

---

## SpawnDev.BlazorJS IndexedDB Usage

```csharp
// Using SpawnDev.BlazorJS typed IDB wrappers
var factory = JS.Get<IDBFactory>("indexedDB");
var request = factory.Open("aubscraft-world-cache", 1);

// On upgrade needed: create object stores
request.OnUpgradeNeeded += (e) => {
    var db = request.Result;
    db.CreateObjectStore("heightmaps");
    db.CreateObjectStore("chunks");
    db.CreateObjectStore("meta");
};

// Write chunk
var tx = db.Transaction("chunks", "readwrite");
var store = tx.ObjectStore("chunks");
store.Put(chunkData, $"{cx},{cz}");

// Read chunk
var tx = db.Transaction("chunks", "readonly");
var store = tx.ObjectStore("chunks");
var result = store.Get($"{cx},{cz}");
// result contains the cached chunk data

// Read all cached chunks (cursor)
var tx = db.Transaction("heightmaps", "readonly");
var store = tx.ObjectStore("heightmaps");
var cursor = store.OpenCursor();
// iterate all cached heightmaps for instant startup
```
