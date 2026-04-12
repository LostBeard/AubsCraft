# Storage Benchmark Results

**Date:** 2026-04-12
**Test:** 1000 chunks, 2.6KB each, 2.5MB total
**Browser:** Chrome (Blazor WASM)
**Machine:** TJ's dev PC

## Results

| Operation | Time (ms) | Throughput (MB/s) | Notes |
|-----------|-----------|-------------------|-------|
| **OPFS Write (region, 1 file)** | **20** | **124.0** | Clear winner for writes |
| **OPFS Read (region, 1 file)** | **9** | **275.5** | Clear winner for reads |
| IDB Read (getAll) | 117 | 21.2 | Best IndexedDB read path |
| IDB Read (individual get) | 564 | 4.4 | Per-chunk transaction overhead |
| IDB Write (individual tx) | 2343 | 1.1 | Per-chunk transaction overhead kills it |
| OPFS Write (individual files) | 4323 | 0.6 | File-per-chunk is terrible |
| OPFS Read (individual files) | 1459 | 1.7 | File-per-chunk is terrible |

## Key Findings

1. **OPFS region files are 13x faster reads and 117x faster writes than IndexedDB**
2. Individual file/record approaches are terrible for both OPFS and IndexedDB
3. Batching into region files (1024 chunks per file) is the key optimization
4. OPFS region read returns ArrayBuffer that can go directly to GPU via CopyFromJS
5. IndexedDB getAll is decent (21 MB/s) but still 13x slower than OPFS region read

## Decision

**OPFS region files** for all world cache storage. One file per 32x32 region (matching Minecraft's .mca format granularity).

Data flow: OPFS -> ArrayBuffer (JS) -> CopyFromJS -> GPU buffer -> ILGPU kernel

Zero .NET involvement in the hot read path.

## Benchmark Page

The benchmark page is deployed at `/benchmark` in the AubsCraft admin panel. Slider adjusts chunk count from 100 to 3000. Tests all 6 approaches with real data sizes.
