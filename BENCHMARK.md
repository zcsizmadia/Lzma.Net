# Benchmarks

Performance comparison of **LzmaNet** (pure managed C#) against the native **xz** CLI (liblzma, C).

## Methodology

- **Scenarios**: the [Silesia corpus](https://sun.aei.polsl.pl/~sdeor/index.php?page=silesia) (211.9 MB, the standard real-world compressor benchmark), a synthetic 16 MB small-file case (repeating patterns + random bytes), and 32 MB of incompressible random data.
- **Timing**: median of 3 timed runs for the large inputs, 5 for the small one, after JIT warmup.
- **Compression**: every implementation runs its own defaults at 1 thread and at 20 threads. On the 16 MB scenario both additionally run a matched 1 MiB block size, because at that input size *both* implementations' default block sizes leave a single block and no parallelism (xz defaults to 3× dictionary = 24 MiB blocks; LzmaNet to `max(2× dictionary, 1 MB)` = 16 MB).
- **Decompression is cross-decode**: all decoders decode the *same* two reference files — one single-block and one multi-block (1 MiB blocks), both produced by the native xz encoder — so the decode columns measure decoder speed, not the shape of each encoder's own output. Every decode is byte-verified against the original.
- **Multi-threading**: LzmaNet compression runs a bounded pipeline — each block starts encoding as soon as it is complete, at most N encodes are in flight, and finished blocks stream out in order while newer ones encode. Decompression decodes batches of N blocks concurrently in both the sync and async read paths. Multi-threaded output is byte-identical to single-threaded output.
- **Caveats**: the xz CLI numbers include process spawn and pipe overhead (amortized on the large inputs, visible on the small ones). Peak memory is not measured; block-parallel modes hold up to N blocks in flight.
- **Platform**: .NET 10, Windows 11, AMD Ryzen 9 (20 logical cores), xz 5.8.3, preset 6.

Percentages are relative to the xz CLI at the same thread count.

## Silesia corpus — 211.9 MB, real-world mix

### Compression

| Implementation | Config | MB/s | % of xz | Ratio | Size |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T defaults | 5.3 | 183% | 27.0% | 57,150,924 |
| **LzmaNet** | 20T defaults | 18.4 | 148% | 27.0% | 57,150,924 |
| xz CLI — *baseline* | 1T defaults | 2.9 | 100% | 23.2% | 49,211,276 |
| xz CLI — *baseline* | 20T defaults | 12.4 | 100% | 23.4% | 49,495,408 |

LzmaNet's 1- and 20-thread outputs are byte-identical: blocks are encoded
deterministically and independently, so only the degree of parallelism differs.

### Decompression (shared xz-produced references)

| Implementation | Config | Single-block MB/s | % of xz | Multi-block MB/s | % of xz |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T | 83.0 | 119% | 76.5 | 112% |
| **LzmaNet** | 20T | 383.4 | 302% | 507.3 | 386% |
| xz CLI — *baseline* | 1T | 69.6 | 100% | 68.2 | 100% |
| xz CLI — *baseline* | 20T | 126.8 | 100% | 131.5 | 100% |

## Synthetic 16 MB — patterns + random bytes

### Compression

| Implementation | Config | MB/s | % of xz | Ratio | Size |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T defaults | 15.7 | 194% | 23.0% | 3,862,672 |
| **LzmaNet** | 20T defaults | 17.0 | 210% | 23.0% | 3,862,672 |
| **LzmaNet** | 20T BlockSize=1MiB | 158.2 | 688% | 22.7% | 3,809,056 |
| xz CLI — *baseline* | 1T defaults | 8.1 | 100% | 22.9% | 3,843,916 |
| xz CLI — *baseline* | 20T defaults | 8.1 | 100% | 22.9% | 3,843,924 |
| xz CLI — *baseline* | 20T --block-size=1MiB | 23.0 | 100% | 22.6% | 3,792,704 |

Neither implementation parallelizes 16 MB at default block sizes (a single block has
nothing to split); with matched 1 MiB blocks both scale.

### Decompression (shared xz-produced references)

| Implementation | Config | Single-block MB/s | % of xz | Multi-block MB/s | % of xz |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T | 79.7 | 335% | 83.2 | 351% |
| **LzmaNet** | 20T | 78.9 | 300% | 760.4 | 2848% |
| xz CLI — *baseline* | 1T | 23.8 | 100% | 23.7 | 100% |
| xz CLI — *baseline* | 20T | 26.3 | 100% | 26.7 | 100% |

At this input size the xz CLI numbers are dominated by process spawn and pipe
overhead (see Methodology); the in-process 1T rows (~80 MB/s) are the
representative decoder speed.

## Incompressible 32 MB — random bytes

### Compression

| Implementation | Config | MB/s | % of xz | Ratio |
|---|---|---:|---:|---:|
| **LzmaNet** | 1T defaults | 5.0 | 185% | 100.0% |
| **LzmaNet** | 20T defaults | 9.4 | 261% | 100.0% |
| xz CLI — *baseline* | 1T defaults | 2.7 | 100% | 100.0% |
| xz CLI — *baseline* | 20T defaults | 3.6 | 100% | 100.0% |

### Decompression (shared xz-produced references)

| Implementation | Config | Single-block MB/s | % of xz | Multi-block MB/s | % of xz |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T | 5,462.8 | 11824% | 8,949.5 | 19001% |
| **LzmaNet** | 20T | 2,170.8 | 4824% | 2,422.3 | 5371% |
| xz CLI — *baseline* | 1T | 46.2 | 100% | 47.1 | 100% |
| xz CLI — *baseline* | 20T | 45.0 | 100% | 45.1 | 100% |

Incompressible data is stored in uncompressed LZMA2 chunks, so decoding is close
to a memory copy for the in-process library; the xz CLI is bounded by pipe I/O.

## Key Takeaways

- **Compression speed**: LzmaNet is **1.5–1.8× faster** than native xz on real-world data at every thread count, with honest like-for-like configurations. Multi-threaded compression is a bounded pipeline: with a streaming producer, encoding starts at the first complete block instead of after a full batch has buffered.
- **Compression ratio**: xz compresses ~3.8 percentage points better on Silesia (23.2% vs 27.0%). LzmaNet uses a hash-chain (HC4) match finder with lazy one-step lookahead; xz preset 6 uses BT4 with near-optimal parsing. The remaining gap is an algorithmic trade (speed vs ratio) — closing it would need a BT match finder + optimal parser.
- **Decompression**: LzmaNet decodes **faster than the native xz CLI single-threaded** on identical real-world input (112–119% on Silesia), helped by the carry-less-multiply CRC64 verification. With multi-block streams and threads, parallel block decode reaches **3.0–3.9×** the xz CLI rate on Silesia, in both the sync and async read paths.
- **Incompressible data**: LzmaNet detects expanding chunks early and stores them raw — ~1.8–2.7× faster than xz to compress, and **multi-GB/s to decompress** in-process (stored blocks decode as memcpy + CLMUL CRC).
- **Interoperability**: every decompression figure above is LzmaNet decoding files produced by the native xz encoder, byte-verified.

## How the managed implementation stays close to C

The decoder applies the same disciplines liblzma uses, expressed in C#:

- **Span-based range decoder** — the coder reads from a cached `ReadOnlySpan<byte>`; there is no `Memory<T>.Span` conversion or stream call per normalization.
- **Register discipline** — `range`, `code`, position, LZMA state, and rep distances live in locals inside one decode loop and are written back once per chunk.
- **No bounds checks on probability models** — probability arrays are accessed via `MemoryMarshal.GetArrayDataReference` + `Unsafe.Add`, mirroring liblzma's pointer arithmetic.
- **Output-as-window** — every XZ block starts with a dictionary reset, so the block's output buffer *is* the dictionary. Each decoded byte is written once, match copies are straight in-buffer copies with a geometric overlap strategy, and no circular-buffer arithmetic runs in the hot loop.
- **Carry-less-multiply CRC32/CRC64** — integrity checks use PCLMULQDQ folding on x86/x64 and PMULL on ARM64 (slicing-by-8 fallback elsewhere), with constants derived from the polynomial at startup.

The encoder carries the **dictionary across LZMA2 chunks** within a block (like xz), uses a hash-chain match finder with **lazy one-step match lookahead**, power-of-two masked chain slots, SIMD match comparison (32 bytes per step with `Vector256`, 8-byte word fallback), a buffered range encoder, and early abort on expanding (incompressible) chunks.

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
