# Benchmarks

Performance comparison of **LzmaNet** (pure managed C#) against the native **xz** CLI (liblzma, C).

## Methodology

- **Scenarios**: the [Silesia corpus](https://sun.aei.polsl.pl/~sdeor/index.php?page=silesia) (211.9 MB, the standard real-world compressor benchmark), a synthetic 16 MB small-file case (repeating patterns + random bytes), and 32 MB of incompressible random data.
- **Timing**: median of 3 timed runs for the large inputs, 5 for the small one, after JIT warmup.
- **Compression**: every implementation runs its own defaults at 1 thread and at 64 threads. On the 16 MB scenario both additionally run a matched 1 MiB block size, because at that input size *both* implementations' default block sizes leave a single block and no parallelism (xz defaults to 3× dictionary = 24 MiB blocks; LzmaNet to `max(2× dictionary, 1 MB)` = 16 MB).
- **Decompression is cross-decode**: all decoders decode the *same* two reference files — one produced by `xz -T64` with its default block size (24 MiB blocks: nine on Silesia, one on the 16 MB input, two on the 32 MB one) and one with 1 MiB blocks — so the decode columns measure decoder speed, not the shape of each encoder's own output. Every decode is byte-verified against the original.
- **Multi-threading**: LzmaNet compression runs a bounded pipeline — each block starts encoding as soon as it is complete, at most N encodes are in flight, and finished blocks stream out in order while newer ones encode. Decompression keeps up to N block decodes in flight, starting the next block as soon as the oldest is handed out, in both the sync and async read paths. Multi-threaded output is byte-identical to single-threaded output.
- **Memory**: compression rows report managed bytes allocated during the median run (Alloc column; n/a for the out-of-process CLI), and the benchmark prints its peak working set at the end. Block-parallel modes hold up to N blocks in flight.
- **Caveats**: the xz CLI numbers include process spawn and pipe overhead (amortized on the large inputs, visible on the small ones).
- **Platform**: .NET 11.0.0, Ubuntu (Linux 5.15), 2× AMD EPYC 7543. The whole run is pinned to one socket with `numactl --cpunodebind=0 --membind=0` (64 logical CPUs, `DOTNET_PROCESSOR_COUNT=64`), and the corpus and temporary files live in tmpfs. xz 5.8.4 built from the release tarball with its default build flags. Preset 6 unless stated.

Percentages are relative to the xz CLI at the same thread count.

## Silesia corpus — 211.9 MB, real-world mix

### Compression

| Implementation | Config | MB/s | % of xz | Ratio | Size | Alloc |
|---|---|---:|---:|---:|---:|---:|
| **LzmaNet** | 1T defaults | 2.8 | 82% | 23.5% | 49,851,064 | 507 MB |
| **LzmaNet** | 64T defaults | 18.2 | 83% | 23.5% | 49,851,064 | 859 MB |
| xz CLI — *baseline* | 1T defaults | 3.4 | 100% | 23.2% | 49,211,276 | n/a |
| xz CLI — *baseline* | 64T defaults | 21.9 | 100% | 23.4% | 49,495,408 | n/a |

LzmaNet's 1- and 64-thread outputs are byte-identical: blocks are encoded
deterministically and independently, so only the degree of parallelism differs.

### Decompression (shared xz-produced references)

| Implementation | Config | 24 MiB blocks MB/s | % of xz | 1 MiB blocks MB/s | % of xz |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T | 106.3 | 80% | 94.8 | 79% |
| **LzmaNet** | 64T | 619.0 | 108% | **1,832.8** | **143%** |
| xz CLI — *baseline* | 1T | 133.6 | 100% | 120.5 | 100% |
| xz CLI — *baseline* | 64T | 573.7 | 100% | 1,284.7 | 100% |

## Synthetic 16 MB — patterns + random bytes

### Compression

| Implementation | Config | MB/s | % of xz | Ratio | Size | Alloc |
|---|---|---:|---:|---:|---:|---:|
| **LzmaNet** | 1T defaults | 13.0 | 118% | 22.9% | 3,846,384 | 49 MB |
| **LzmaNet** | 64T defaults | 12.8 | 117% | 22.9% | 3,846,384 | 60 MB |
| **LzmaNet** | 64T BlockSize=1MiB | 130.8 | 177% | 22.6% | 3,797,780 | 82 MB |
| xz CLI — *baseline* | 1T defaults | 11.0 | 100% | 22.9% | 3,843,916 | n/a |
| xz CLI — *baseline* | 64T defaults | 10.9 | 100% | 22.9% | 3,843,924 | n/a |
| xz CLI — *baseline* | 64T --block-size=1MiB | 73.8 | 100% | 22.6% | 3,792,704 | n/a |

Neither implementation parallelizes 16 MB at default block sizes (a single block has
nothing to split); with matched 1 MiB blocks both scale.

### Decompression (shared xz-produced references)

| Implementation | Config | 24 MiB blocks MB/s | % of xz | 1 MiB blocks MB/s | % of xz |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T | 133.6 | 87% | 133.8 | 81% |
| **LzmaNet** | 64T | 130.7 | 91% | 1,061.3 | 128% |
| xz CLI — *baseline* | 1T | 153.8 | 100% | 166.2 | 100% |
| xz CLI — *baseline* | 64T | 144.0 | 100% | 829.4 | 100% |

## Incompressible 32 MB — random bytes

### Compression

| Implementation | Config | MB/s | % of xz | Ratio | Alloc |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T defaults | 8.5 | 266% | 100.0% | 308 MB |
| **LzmaNet** | 64T defaults | 16.8 | 400% | 100.0% | 407 MB |
| xz CLI — *baseline* | 1T defaults | 3.2 | 100% | 100.0% | n/a |
| xz CLI — *baseline* | 64T defaults | 4.2 | 100% | 100.0% | n/a |

### Decompression (shared xz-produced references)

| Implementation | Config | 24 MiB blocks MB/s | % of xz | 1 MiB blocks MB/s | % of xz |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T | 5,192.9 | 550% | 6,817.1 | 578% |
| **LzmaNet** | 64T | 2,257.8 | 408% | 1,940.6 | 251% |
| xz CLI — *baseline* | 1T | 943.6 | 100% | 1,178.7 | 100% |
| xz CLI — *baseline* | 64T | 553.1 | 100% | 772.6 | 100% |

Incompressible data is stored in uncompressed LZMA2 chunks, so decoding is close
to a memory copy for the in-process library; the xz CLI is bounded by pipe I/O.

## Every preset — Silesia, 1 thread

All ten levels follow xz's `lzma_lzma_preset`: the same dictionary sizes, match
finder, nice length and search depth. Presets 0–3 use xz's fast-mode parser over
a hash chain; 4–9 use the BT4 binary-tree match finder with price-based optimal
parsing. One run each, with up to eight runs in parallel on separate CCDs of the
same socket.

| Preset | LzmaNet time | LzmaNet size | xz time | xz size | Size vs xz | Speed vs xz |
|---:|---:|---:|---:|---:|---:|---:|
| 0 | 12.3 s | 61,421,856 | 9.5 s | 62,587,880 | −1.9% | 77% |
| 1 | 12.5 s | 59,325,644 | 10.7 s | 58,416,876 | +1.6% | 85% |
| 2 | 16.3 s | 57,532,272 | 15.2 s | 56,717,256 | +1.4% | 93% |
| 3 | 24.1 s | 56,382,684 | 22.6 s | 55,754,164 | +1.1% | 94% |
| 4 | 51.2 s | 52,042,056 | 36.2 s | 52,120,204 | −0.1% | 71% |
| 5 | 62.1 s | 50,402,892 | 50.1 s | 49,978,320 | +0.8% | 81% |
| 6 | 69.1 s | 49,851,064 | 58.2 s | 49,211,276 | +1.3% | 84% |
| 7 | 74.0 s | 49,462,724 | 63.4 s | 48,934,844 | +1.1% | 86% |
| 8 | 80.2 s | 49,062,632 | 67.4 s | 48,787,284 | +0.6% | 84% |
| 9 | 83.0 s | 49,010,452 | 68.3 s | 48,755,320 | +0.5% | 82% |

Decoding speed does not depend on the preset: LzmaNet decodes every level's
`xz -N` output at 70–73% of xz's single-threaded speed in this setup.

## Silesia corpus — preset 9 (BT4 match finder + optimal parser)

One timed run each (these are slow by design).

| Implementation | Config | MB/s | % of xz | Ratio | Size | Alloc |
|---|---|---:|---:|---:|---:|---:|
| **LzmaNet** | preset 9, 1T | 2.4 | 83% | 23.1% | 49,010,452 | 1,379 MB |
| **LzmaNet** | preset 9, 64T | 2.5 | 83% | 23.1% | 49,010,452 | 1,593 MB |
| xz CLI — *baseline* | -9, 1T | 2.9 | 100% | 23.0% | 48,755,320 | n/a |
| xz CLI — *baseline* | -9, 64T | 3.0 | 100% | 23.0% | 48,772,064 | n/a |

**Ratio parity**: LzmaNet's preset-9 output is within 0.6% of `xz -9`'s size on
real-world data, at ~83% of its speed. Preset 9 gains nothing from threads at
this input size — its default 128 MB blocks leave little to parallelize on
212 MB (use a smaller `BlockSize` to trade a little ratio for scaling).

The BT4 match finder sizes its hash table like liblzma (2^24 entries at a 64 MiB
dictionary), uses xz's search depth (`16 + nice_len / 2`), and prefetches the
hash slot, tree node and buffer bytes the next positions will read. Like
liblzma's nice_len limit, it searches and orders the tree on prefixes of
`max(nice_len, 64)` bytes and extends only the longest match to full length:
walking to the full 273 bytes made repetitive data encode about 2× slower,
while the limit costs 0.2–0.3% output size on Silesia.

### Optimal-parser encode throughput

`dotnet run --project LzmaNet.Benchmark -c Release -- optimal` isolates the
parser on 8 MB of text-like data with a fixed 4 MB dictionary, single-threaded
(median of 3). It exists to measure parser changes without the Silesia
download, and reports output size alongside speed so a speedup can be checked
against an unchanged ratio.

| Config | MB/s | Size |
|---|---:|---:|
| preset 7 | 2.09 | 986,644 |
| preset 9 | 2.10 | 986,644 |
| preset 9 `--extreme` | 2.11 | 986,536 |

Length and distance prices are cached per dynamic-programming window (see
`EnsureLenPrices` and `PriceDistCached`). Nothing is emitted between the
window's relaxation passes, so the coder probabilities cannot change while it
runs and the cached prices are exactly the ones a per-edge computation would
produce.

## Key Takeaways

- **Compression ratio**: every preset follows xz's parameters and lands within **−1.9% to +1.6%** of `xz -N`'s output size on Silesia (preset 9: within 0.6%). The default preset 6 compresses Silesia to 23.5% (xz: 23.2%).
- **Compression speed**: 71–94% of native xz single-threaded on Silesia depending on the preset (82% at the default), 83% at 64 threads. On repetitive data (the 16 MB synthetic scenario) LzmaNet is faster than xz: 118% at the default preset, 177% with matched 1 MiB blocks at 64 threads.
- **Decompression**: single-threaded, native xz decodes real-world data about 1.25× faster (LzmaNet 79–80% on Silesia). Multi-threaded, LzmaNet is faster: **143% of xz** on 1 MiB-block Silesia (1.8 GB/s), 108% with xz's default 24 MiB blocks.
- **Incompressible data**: LzmaNet detects expanding chunks early and stores them raw — 2.7–4× faster than xz to compress, and **multi-GB/s to decompress** in-process (stored blocks decode as memcpy + CLMUL CRC).
- **Memory**: the Alloc column shows the throughput trades — parallel modes allocate roughly one block's working set per in-flight encoder, and the BT4 tables of presets 4–9 are large (like xz's).
- **Interoperability**: every decompression figure above is LzmaNet decoding files produced by the native xz encoder, byte-verified.

## How the managed implementation stays close to C

The decoder applies the same disciplines liblzma uses, expressed in C#:

- **Span-based range decoder** — the coder reads from a cached `ReadOnlySpan<byte>`; there is no `Memory<T>.Span` conversion or stream call per normalization.
- **Register discipline** — `range`, `code`, position, LZMA state, and rep0 live in locals inside one decode loop and are written back once per chunk; all probability models sit in one array so the loop holds a single base reference.
- **Preloaded bit trees** — like liblzma's x86-64 assembly, each bit-tree step loads both child probabilities (one 32-bit read) before the current bit is decoded and selects one with the bit mask, so the next probability load is off the range coder's dependency chain. Bits inside trees are decoded without data-dependent branches.
- **No bounds checks on probability models** — probability arrays are accessed via `MemoryMarshal.GetArrayDataReference` + `Unsafe.Add`, mirroring liblzma's pointer arithmetic.
- **Output-as-window** — every XZ block starts with a dictionary reset, so the block's output buffer *is* the dictionary. Each decoded byte is written once, match copies are straight in-buffer copies with a geometric overlap strategy, and no circular-buffer arithmetic runs in the hot loop.
- **Carry-less-multiply CRC32/CRC64** — integrity checks use PCLMULQDQ folding on x86/x64 and PMULL on ARM64 (slicing-by-8 fallback elsewhere), with constants derived from the polynomial at startup.

The encoder carries the **dictionary across LZMA2 chunks** within a block (like xz), uses SIMD match comparison (32 bytes per step with `Vector256`, 8-byte word fallback), a buffered branch-free range encoder, and early abort on expanding (incompressible) chunks. Presets 0–3 use a hash-chain match finder with a port of **xz's fast-mode parser**; presets 4–9 use a **BT4 binary-tree match finder with price-based optimal parsing** — the same split xz makes.

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
