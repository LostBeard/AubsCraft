# World Cache Benchmark Results

**Date:** 2026-04-12
**Test:** 1000 chunks, 2KB each, 2.0MB total

---

## Results

| Operation | Time (ms) | Throughput | Notes |
|-----------|-----------|-----------|-------|
| IDB Write (individual tx) | 2323 | 1.1 MB/s | One transaction per chunk |
| IDB Read (individual get) | 557 | 4.5 MB/s | One get per chunk |
| IDB Read (getAll) | 117 | 21.2 MB/s | Bulk read |
| OPFS Write (individual) | 4300 | 0.6 MB/s | One file per chunk |
| OPFS Read (individual) | 1446 | 1.7 MB/s | One file per chunk |
| **OPFS Write (region, batched)** | **21** | **118.1 MB/s** | **WINNER** |
| **OPFS Read (region, batched)** | **8** | **309.9 MB/s** | **WINNER** |

## Verdict: OPFS Region Files

**OPFS region-batched is the winner by a massive margin.**

- Write: 118.1 MB/s (107x faster than IDB individual, 197x faster than OPFS individual)
- Read: 309.9 MB/s (69x faster than IDB individual, 15x faster than IDB getAll)

## Architecture Decision

Use OPFS with region-file batching - pack multiple chunks into single files, matching Minecraft's own .mca region file pattern (32x32 chunks per region).

The key insight: individual operations are slow for BOTH storage APIs. Batching into region-sized files eliminates per-item overhead entirely.

### Storage format
- One OPFS file per region (32x32 chunk area)
- File name: `r.{rx}.{rz}.bin` (matching Minecraft region coordinates)
- Internal format: fixed-size slots per chunk, header with offsets
- This means 1 file read loads up to 1024 chunks at once

### Why this works
- OPFS `FileSystemSyncAccessHandle` (in workers) or async access from main thread
- Single file I/O operation for hundreds of chunks
- No per-item overhead (no transactions, no key lookups)
- Browser handles file caching at the OS level
- SpawnDev.BlazorJS has OPFS wrappers ready to go
