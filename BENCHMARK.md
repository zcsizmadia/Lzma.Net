# Benchmarks

Performance comparison of **LzmaNet** (pure managed C#) against the native **xz** CLI (liblzma, C).

## Methodology

- **Scenarios**: the [Silesia corpus](https://sun.aei.polsl.pl/~sdeor/index.php?page=silesia) (211.9 MB, the standard real-world compressor benchmark), a synthetic 16 MB small-file case (repeating patterns + random bytes), and 32 MB of incompressible random data.
- **Timing**: median of 3 timed runs for the large inputs, 5 for the small one, after JIT warmup.
- **Compression**: every implementation runs its own defaults at 1 thread and at 20 threads. On the 16 MB scenario both additionally run a matched 1 MiB block size, because at that input size *both* implementations' default block sizes leave a single block and no parallelism (xz defaults to 3× dictionary = 24 MiB blocks; LzmaNet to `max(2× dictionary, 1 MB)` = 16 MB).
- **Decompression is cross-decode**: all decoders decode the *same* two reference files — one single-block and one multi-block (1 MiB blocks), both produced by the native xz encoder — so the decode columns measure decoder speed, not the shape of each encoder's own output. Every decode is byte-verified against the original.
- **Multi-threading**: LzmaNet compression runs a bounded pipeline — each block starts encoding as soon as it is complete, at most N encodes are in flight, and finished blocks stream out in order while newer ones encode. Decompression decodes batches of N blocks concurrently in both the sync and async read paths. Multi-threaded output is byte-identical to single-threaded output.
- **Memory**: compression rows report managed bytes allocated during the median run (Alloc column; n/a for the out-of-process CLI), and the benchmark prints its peak working set at the end. Block-parallel modes hold up to N blocks in flight.
- **Caveats**: the xz CLI numbers include process spawn and pipe overhead (amortized on the large inputs, visible on the small ones).
- **Platform**: .NET 10, Windows 11, AMD Ryzen 9 (20 logical cores), xz 5.8.3, preset 6.

Percentages are relative to the xz CLI at the same thread count.

## Silesia corpus — 211.9 MB, real-world mix

### Compression

| Implementation | Config | MB/s | % of xz | Ratio | Size | Alloc |
|---|---|---:|---:|---:|---:|---:|
| **LzmaNet** | 1T defaults | 4.7 | 174% | 27.0% | 57,150,772 | 544 MB |
| **LzmaNet** | 20T defaults | 17.0 | 140% | 27.0% | 57,150,772 | 698 MB |
| xz CLI — *baseline* | 1T defaults | 2.7 | 100% | 23.2% | 49,211,276 | n/a |
| xz CLI — *baseline* | 20T defaults | 12.1 | 100% | 23.4% | 49,495,408 | n/a |

LzmaNet's 1- and 20-thread outputs are byte-identical: blocks are encoded
deterministically and independently, so only the degree of parallelism differs.

### Decompression (shared xz-produced references)

| Implementation | Config | Single-block MB/s | % of xz | Multi-block MB/s | % of xz |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T | 80.1 | 102% | 75.1 | 97% |
| **LzmaNet** | 20T | 288.8 | 188% | 369.0 | 222% |
| xz CLI — *baseline* | 1T | 78.8 | 100% | 77.3 | 100% |
| xz CLI — *baseline* | 20T | 153.8 | 100% | 165.9 | 100% |

## Synthetic 16 MB — patterns + random bytes

### Compression

| Implementation | Config | MB/s | % of xz | Ratio | Size | Alloc |
|---|---|---:|---:|---:|---:|---:|
| **LzmaNet** | 1T defaults | 15.2 | 150% | 23.0% | 3,862,684 | 46 MB |
| **LzmaNet** | 20T defaults | 14.9 | 143% | 23.0% | 3,862,684 | 97 MB |
| **LzmaNet** | 20T BlockSize=1MiB | 146.6 | 232% | 22.7% | 3,809,092 | 942 MB |
| xz CLI — *baseline* | 1T defaults | 10.1 | 100% | 22.9% | 3,843,916 | n/a |
| xz CLI — *baseline* | 20T defaults | 10.4 | 100% | 22.9% | 3,843,924 | n/a |
| xz CLI — *baseline* | 20T --block-size=1MiB | 63.1 | 100% | 22.6% | 3,792,704 | n/a |

Neither implementation parallelizes 16 MB at default block sizes (a single block has
nothing to split); with matched 1 MiB blocks both scale.

### Decompression (shared xz-produced references)

| Implementation | Config | Single-block MB/s | % of xz | Multi-block MB/s | % of xz |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T | 78.7 | 117% | 79.8 | 119% |
| **LzmaNet** | 20T | 81.2 | 86% | 723.7 | 701% |
| xz CLI — *baseline* | 1T | 67.5 | 100% | 67.2 | 100% |
| xz CLI — *baseline* | 20T | 94.7 | 100% | 103.2 | 100% |

At this input size the xz CLI numbers are dominated by process spawn and pipe
overhead (see Methodology); the in-process 1T rows (~80 MB/s) are the
representative decoder speed.

## Incompressible 32 MB — random bytes

### Compression

| Implementation | Config | MB/s | % of xz | Ratio | Alloc |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T defaults | 4.7 | 174% | 100.0% | 306 MB |
| **LzmaNet** | 20T defaults | 9.1 | 246% | 100.0% | 434 MB |
| xz CLI — *baseline* | 1T defaults | 2.7 | 100% | 100.0% | n/a |
| xz CLI — *baseline* | 20T defaults | 3.7 | 100% | 100.0% | n/a |

### Decompression (shared xz-produced references)

| Implementation | Config | Single-block MB/s | % of xz | Multi-block MB/s | % of xz |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T | 4,934.1 | 3795% | 8,286.1 | 6423% |
| **LzmaNet** | 20T | 1,587.9 | 1367% | 1,631.9 | 1408% |
| xz CLI — *baseline* | 1T | 130.0 | 100% | 129.0 | 100% |
| xz CLI — *baseline* | 20T | 116.2 | 100% | 115.9 | 100% |

Incompressible data is stored in uncompressed LZMA2 chunks, so decoding is close
to a memory copy for the in-process library; the xz CLI is bounded by pipe I/O.

## Silesia corpus — preset 9 (BT4 match finder + optimal parser)

Presets 7–9 use the binary-tree match finder with price-based optimal parsing —
the same architecture as xz's high presets. One timed run each (these are slow
by design).

| Implementation | Config | MB/s | % of xz | Ratio | Size | Alloc |
|---|---|---:|---:|---:|---:|---:|
| **LzmaNet** | preset 9, 1T | 1.6 | 67% | 23.0% | 48,848,144 | 1,259 MB |
| **LzmaNet** | preset 9, 20T | 1.6 | 64% | 23.0% | 48,848,144 | 1,472 MB |
| xz CLI — *baseline* | -9, 1T | 2.4 | 100% | 23.0% | 48,755,320 | n/a |
| xz CLI — *baseline* | -9, 20T | 2.5 | 100% | 23.0% | 48,772,064 | n/a |

**Ratio parity**: LzmaNet's preset-9 output is within 0.2% of `xz -9`'s size on
real-world data, at ~70% of its speed. Preset 9 gains nothing from threads at
this input size — its default 128 MB blocks leave little to parallelize on
212 MB (use a smaller `BlockSize` to trade a little ratio for scaling).

## Key Takeaways

- **Compression ratio**: at preset 9 LzmaNet **matches `xz -9`** (23.0% vs 23.0% on Silesia, within 0.2% of its output size) using the same BT4 + optimal-parsing architecture. The default preset 6 stays speed-first: 27.0% at ~1.6× xz's speed.
- **Compression speed** (preset 6): LzmaNet is **1.4–1.7× faster** than native xz on real-world data at every thread count, with honest like-for-like configurations. Multi-threaded compression is a bounded pipeline: with a streaming producer, encoding starts at the first complete block instead of after a full batch has buffered.
- **Decompression**: LzmaNet decodes at or above the native xz CLI single-threaded on identical real-world input (97–102% on Silesia), helped by the carry-less-multiply CRC64 verification. With multi-block streams and threads, parallel block decode reaches **1.9–2.2×** the xz CLI rate on Silesia, in both the sync and async read paths.
- **Incompressible data**: LzmaNet detects expanding chunks early and stores them raw — ~1.7–2.5× faster than xz to compress, and **multi-GB/s to decompress** in-process (stored blocks decode as memcpy + CLMUL CRC).
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
| CRC32 | 1.68 GB/s | **14.31 GB/s** | 8.5× |
| CRC64 | 1.64 GB/s | **13.47 GB/s** | 8.2× |

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
