# Benchmarks

Performance comparison of **LzmaNet** (pure managed C#) against the native **xz** CLI (liblzma, C).

## Methodology

- **Scenarios**: the [Silesia corpus](https://sun.aei.polsl.pl/~sdeor/index.php?page=silesia) (211.9 MB, the standard real-world compressor benchmark), a synthetic 16 MB small-file case (repeating patterns + random bytes), and 32 MB of incompressible random data.
- **Timing**: median of 3 timed runs for the large inputs, 5 for the small one, after JIT warmup.
- **Compression**: every implementation runs its own defaults at 1 thread and at 64 threads. On the 16 MB scenario both additionally run a matched 1 MiB block size, because at that input size *both* implementations' default block sizes leave a single block and no parallelism (xz defaults to 3× dictionary = 24 MiB blocks; LzmaNet to `max(2× dictionary, 1 MB)` = 16 MB).
- **Decompression is cross-decode**: all decoders decode the *same* two reference files — one single-block and one multi-block (1 MiB blocks), both produced by the native xz encoder — so the decode columns measure decoder speed, not the shape of each encoder's own output. Every decode is byte-verified against the original.
- **Multi-threading**: LzmaNet compression runs a bounded pipeline — each block starts encoding as soon as it is complete, at most N encodes are in flight, and finished blocks stream out in order while newer ones encode. Decompression decodes batches of N blocks concurrently in both the sync and async read paths. Multi-threaded output is byte-identical to single-threaded output.
- **Memory**: compression rows report managed bytes allocated during the median run (Alloc column; n/a for the out-of-process CLI), and the benchmark prints its peak working set at the end. Block-parallel modes hold up to N blocks in flight.
- **Caveats**: the xz CLI numbers include process spawn and pipe overhead (amortized on the large inputs, visible on the small ones).
- **Platform**: .NET 10.0.12, Ubuntu (Linux 5.15), 2× AMD EPYC 7543. The whole run is pinned to one socket with `numactl --cpunodebind=0 --membind=0` (64 logical CPUs, `DOTNET_PROCESSOR_COUNT=64`), and the corpus and temporary files live in tmpfs. xz 5.8.4 built from the release tarball with its default build flags. Preset 6 unless stated.

Percentages are relative to the xz CLI at the same thread count.

## Silesia corpus — 211.9 MB, real-world mix

### Compression

| Implementation | Config | MB/s | % of xz | Ratio | Size | Alloc |
|---|---|---:|---:|---:|---:|---:|
| **LzmaNet** | 1T defaults | 7.1 | 209% | 27.0% | 57,150,924 | 544 MB |
| **LzmaNet** | 64T defaults | 41.6 | 191% | 27.0% | 57,150,924 | 698 MB |
| xz CLI — *baseline* | 1T defaults | 3.4 | 100% | 23.2% | 49,211,276 | n/a |
| xz CLI — *baseline* | 64T defaults | 21.8 | 100% | 23.4% | 49,495,408 | n/a |

LzmaNet's 1- and 64-thread outputs are byte-identical: blocks are encoded
deterministically and independently, so only the degree of parallelism differs.

### Decompression (shared xz-produced references)

| Implementation | Config | Single-block MB/s | % of xz | Multi-block MB/s | % of xz |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T | 64.1 | 48% | 58.3 | 48% |
| **LzmaNet** | 64T | 331.9 | 58% | 714.1 | 54% |
| xz CLI — *baseline* | 1T | 133.7 | 100% | 120.8 | 100% |
| xz CLI — *baseline* | 64T | 573.7 | 100% | 1,326.7 | 100% |

## Synthetic 16 MB — patterns + random bytes

### Compression

| Implementation | Config | MB/s | % of xz | Ratio | Size | Alloc |
|---|---|---:|---:|---:|---:|---:|
| **LzmaNet** | 1T defaults | 22.9 | 208% | 23.0% | 3,862,672 | 46 MB |
| **LzmaNet** | 64T defaults | 22.4 | 206% | 23.0% | 3,862,672 | 57 MB |
| **LzmaNet** | 64T BlockSize=1MiB | 248.1 | 347% | 22.7% | 3,809,056 | 54 MB |
| xz CLI — *baseline* | 1T defaults | 11.0 | 100% | 22.9% | 3,843,916 | n/a |
| xz CLI — *baseline* | 64T defaults | 10.9 | 100% | 22.9% | 3,843,924 | n/a |
| xz CLI — *baseline* | 64T --block-size=1MiB | 71.4 | 100% | 22.6% | 3,792,704 | n/a |

Neither implementation parallelizes 16 MB at default block sizes (a single block has
nothing to split); with matched 1 MiB blocks both scale.

### Decompression (shared xz-produced references)

| Implementation | Config | Single-block MB/s | % of xz | Multi-block MB/s | % of xz |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T | 65.3 | 42% | 66.0 | 40% |
| **LzmaNet** | 64T | 64.4 | 44% | 656.5 | 73% |
| xz CLI — *baseline* | 1T | 156.6 | 100% | 166.5 | 100% |
| xz CLI — *baseline* | 64T | 147.6 | 100% | 895.3 | 100% |

## Incompressible 32 MB — random bytes

### Compression

| Implementation | Config | MB/s | % of xz | Ratio | Alloc |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T defaults | 7.5 | 242% | 100.0% | 306 MB |
| **LzmaNet** | 64T defaults | 14.1 | 336% | 100.0% | 402 MB |
| xz CLI — *baseline* | 1T defaults | 3.1 | 100% | 100.0% | n/a |
| xz CLI — *baseline* | 64T defaults | 4.2 | 100% | 100.0% | n/a |

### Decompression (shared xz-produced references)

| Implementation | Config | Single-block MB/s | % of xz | Multi-block MB/s | % of xz |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T | 5,239.9 | 540% | 7,257.7 | 642% |
| **LzmaNet** | 64T | 2,252.0 | 395% | 896.5 | 122% |
| xz CLI — *baseline* | 1T | 970.5 | 100% | 1,129.7 | 100% |
| xz CLI — *baseline* | 64T | 570.6 | 100% | 734.5 | 100% |

Incompressible data is stored in uncompressed LZMA2 chunks, so decoding is close
to a memory copy for the in-process library; the xz CLI is bounded by pipe I/O.

## Silesia corpus — preset 9 (BT4 match finder + optimal parser)

Presets 7–9 use the binary-tree match finder with price-based optimal parsing —
the same architecture as xz's high presets. One timed run each (these are slow
by design).

| Implementation | Config | MB/s | % of xz | Ratio | Size | Alloc |
|---|---|---:|---:|---:|---:|---:|
| **LzmaNet** | preset 9, 1T | 2.5 | 86% | 23.1% | 48,867,432 | 1,379 MB |
| **LzmaNet** | preset 9, 64T | 2.5 | 83% | 23.1% | 48,867,432 | 1,592 MB |
| xz CLI — *baseline* | -9, 1T | 2.9 | 100% | 23.0% | 48,755,320 | n/a |
| xz CLI — *baseline* | -9, 64T | 3.0 | 100% | 23.0% | 48,772,064 | n/a |

**Ratio parity**: LzmaNet's preset-9 output is within 0.23% of `xz -9`'s size on
real-world data, at ~85% of its speed. Preset 9 gains nothing from threads at
this input size — its default 128 MB blocks leave little to parallelize on
212 MB (use a smaller `BlockSize` to trade a little ratio for scaling).

The BT4 match finder sizes its hash table like liblzma (2^24 entries at a 64 MiB
dictionary), uses xz's search depth (`16 + nice_len / 2` = 48), and prefetches
the hash slot, tree node and buffer bytes the next positions will read. Measured
single-threaded against the previous release on the same corpus, preset 9 encodes
in 80.6 s instead of 107.9 s (1.34×), for +0.04% output size. The hash sizing and
search depth account for that size change; the prefetching and the parser's
price caching (below) leave the output byte-identical.

### Optimal-parser encode throughput

`dotnet run --project LzmaNet.Benchmark -c Release -- optimal` isolates the
parser on 8 MB of text-like data with a fixed 4 MB dictionary, single-threaded
(median of 3). It exists to measure parser changes without the Silesia
download, and reports output size alongside speed so a speedup can be checked
against an unchanged ratio.

| Config | MB/s | Size |
|---|---:|---:|
| preset 7 | 2.11 | 986,644 |
| preset 9 | 2.12 | 986,644 |
| preset 9 `--extreme` | 2.12 | 986,536 |

Length and distance prices are cached per dynamic-programming window (see
`EnsureLenPrices` and `PriceDistCached`). Nothing is emitted between the
window's relaxation passes, so the coder probabilities cannot change while it
runs and the cached prices are exactly the ones a per-edge computation would
produce — the output is byte-for-byte unchanged.

## Key Takeaways

- **Compression ratio**: at preset 9 LzmaNet **matches `xz -9`** (23.1% vs 23.0% on Silesia, within 0.23% of its output size) using the same BT4 + optimal-parsing architecture, at ~85% of its speed. The default preset 6 stays speed-first: 27.0% at ~2× xz's speed.
- **Compression speed** (preset 6): LzmaNet is **1.9–2.1× faster** than native xz on real-world data at both thread counts, with honest like-for-like configurations. Multi-threaded compression is a bounded pipeline: with a streaming producer, encoding starts at the first complete block instead of after a full batch has buffered.
- **Decompression**: native xz on Linux decodes real-world data about **twice as fast** single-threaded (LzmaNet 48% of xz on Silesia), and its own multi-threaded decoder stays ahead with multi-block input (54–73%). LzmaNet's in-process multi-block decode still scales with threads (58 → 714 MB/s on Silesia).
- **Incompressible data**: LzmaNet detects expanding chunks early and stores them raw — ~2.4–3.4× faster than xz to compress, and **multi-GB/s to decompress** in-process (stored blocks decode as memcpy + CLMUL CRC).
- **Memory**: the Alloc column shows the throughput trades — parallel modes allocate roughly one block's working set per in-flight encoder, and preset 9's BT4 tables are large (like xz -9's ~700 MB).
- **Interoperability**: every decompression figure above is LzmaNet decoding files produced by the native xz encoder, byte-verified.

## How the managed implementation stays close to C

The decoder applies the same disciplines liblzma uses, expressed in C#:

- **Span-based range decoder** — the coder reads from a cached `ReadOnlySpan<byte>`; there is no `Memory<T>.Span` conversion or stream call per normalization.
- **Register discipline** — `range`, `code`, position, LZMA state, and rep distances live in locals inside one decode loop and are written back once per chunk.
- **No bounds checks on probability models** — probability arrays are accessed via `MemoryMarshal.GetArrayDataReference` + `Unsafe.Add`, mirroring liblzma's pointer arithmetic.
- **Output-as-window** — every XZ block starts with a dictionary reset, so the block's output buffer *is* the dictionary. Each decoded byte is written once, match copies are straight in-buffer copies with a geometric overlap strategy, and no circular-buffer arithmetic runs in the hot loop.
- **Carry-less-multiply CRC32/CRC64** — integrity checks use PCLMULQDQ folding on x86/x64 and PMULL on ARM64 (slicing-by-8 fallback elsewhere), with constants derived from the polynomial at startup.

The encoder carries the **dictionary across LZMA2 chunks** within a block (like xz), uses SIMD match comparison (32 bytes per step with `Vector256`, 8-byte word fallback), a buffered range encoder, and early abort on expanding (incompressible) chunks. Presets 0–6 use a hash-chain match finder (greedy at 0–2, **lazy lookahead** at 3–6); presets 7–9 use a **BT4 binary-tree match finder with price-based optimal parsing**, the same architecture as xz's high presets.

### CRC micro-benchmark

256 MB of random data, median of 5 runs (`dotnet run --project LzmaNet.Benchmark -- crc`):

| | Slicing-by-8 (table) | CLMUL folding | Speedup |
|---|---:|---:|---:|
| CRC32 | 1.50 GB/s | **12.37 GB/s** | 8.2× |
| CRC64 | 1.36 GB/s | **12.38 GB/s** | 9.1× |

The CRC64 check runs over all uncompressed bytes in both directions, so this
shows up directly in decompression throughput — most dramatically on
stored-uncompressed (incompressible) blocks, whose decoding is essentially
memcpy + CRC.

## Running the benchmarks

```shell
dotnet run --project LzmaNet.Benchmark -c Release
```

Requires the `xz` CLI in `PATH` (or at `/usr/bin/xz`) for the native comparison and
for producing the cross-decode reference files. The Silesia corpus (~68 MB download)
is fetched and cached automatically on first run.

To reproduce the figures above on a dual-socket machine, pin the run to one socket:

```shell
DOTNET_PROCESSOR_COUNT=64 numactl --cpunodebind=0 --membind=0 \
  dotnet run --project LzmaNet.Benchmark -c Release
```
