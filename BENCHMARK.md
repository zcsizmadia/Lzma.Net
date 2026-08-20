# Benchmarks

Performance comparison of **LzmaNet** (pure managed C#) against the native **xz** CLI (liblzma, C).

## Methodology

- **Scenarios**: the [Silesia corpus](https://sun.aei.polsl.pl/~sdeor/index.php?page=silesia) (211.9 MB, the standard real-world compressor benchmark), a synthetic 16 MB small-file case (repeating patterns + random bytes), and 32 MB of incompressible random data.
- **Timing**: median of 3 timed runs for the large inputs, 5 for the small one, after JIT warmup.
- **Compression**: every implementation runs its own defaults at 1 thread and at 20 threads. On the 16 MB scenario both additionally run a matched 1 MiB block size, because at that input size *both* implementations' default block sizes leave a single block and no parallelism (xz defaults to 3× dictionary = 24 MiB blocks; LzmaNet to `max(2× dictionary, 1 MB)` = 16 MB).
- **Decompression is cross-decode**: all decoders decode the *same* two reference files — one single-block and one multi-block (1 MiB blocks), both produced by the native xz encoder — so the decode columns measure decoder speed, not the shape of each encoder's own output. Every decode is byte-verified against the original.
- **Caveats**: the xz CLI numbers include process spawn and pipe overhead (amortized on the large inputs, visible on the small ones). Peak memory is not measured; block-parallel modes hold up to N blocks in flight.
- **Platform**: .NET 10, Windows 11, AMD Ryzen 9 (20 logical cores), xz 5.8.3, preset 6.

Percentages are relative to the xz CLI at the same thread count.

## Silesia corpus — 211.9 MB, real-world mix

### Compression

| Implementation | Config | MB/s | % of xz | Ratio | Size |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T defaults | 6.7 | 231% | 27.7% | 58,797,892 |
| **LzmaNet** | 20T defaults | 33.1 | 263% | 27.8% | 58,969,832 |
| xz CLI — *baseline* | 1T defaults | 2.9 | 100% | 23.2% | 49,211,276 |
| xz CLI — *baseline* | 20T defaults | 12.6 | 100% | 23.4% | 49,495,408 |

### Decompression (shared xz-produced references)

| Implementation | Config | Single-block MB/s | % of xz | Multi-block MB/s | % of xz |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T | 83.6 | 108% | 76.0 | 102% |
| **LzmaNet** | 20T | 404.4 | 263% | 500.8 | 311% |
| xz CLI — *baseline* | 1T | 77.3 | 100% | 74.3 | 100% |
| xz CLI — *baseline* | 20T | 153.7 | 100% | 161.0 | 100% |

## Synthetic 16 MB — patterns + random bytes

### Compression

| Implementation | Config | MB/s | % of xz | Ratio | Size |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T defaults | 16.1 | 173% | 23.0% | 3,863,160 |
| **LzmaNet** | 20T defaults | 16.0 | 168% | 23.0% | 3,863,160 |
| **LzmaNet** | 20T BlockSize=1MiB | 150.7 | 390% | 22.7% | 3,809,616 |
| xz CLI — *baseline* | 1T defaults | 9.3 | 100% | 22.9% | 3,843,916 |
| xz CLI — *baseline* | 20T defaults | 9.5 | 100% | 22.9% | 3,843,924 |
| xz CLI — *baseline* | 20T --block-size=1MiB | 38.6 | 100% | 22.6% | 3,792,704 |

Neither implementation parallelizes 16 MB at default block sizes (a single block has
nothing to split); with matched 1 MiB blocks both scale, and LzmaNet is 3.9× faster.

### Decompression (shared xz-produced references)

| Implementation | Config | Single-block MB/s | % of xz | Multi-block MB/s | % of xz |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T | 79.7 | 197% | 80.7 | 199% |
| **LzmaNet** | 20T | 81.0 | 170% | 786.5 | 1608% |
| xz CLI — *baseline* | 1T | 40.5 | 100% | 40.5 | 100% |
| xz CLI — *baseline* | 20T | 47.6 | 100% | 48.9 | 100% |

At this input size the xz CLI numbers are dominated by process spawn and pipe
overhead (see Methodology); the in-process 1T rows (~80 MB/s) are the
representative decoder speed.

## Incompressible 32 MB — random bytes

### Compression

| Implementation | Config | MB/s | % of xz | Ratio |
|---|---|---:|---:|---:|
| **LzmaNet** | 1T defaults | 4.4 | 163% | 100.0% |
| **LzmaNet** | 20T defaults | 9.4 | 261% | 100.0% |
| xz CLI — *baseline* | 1T defaults | 2.7 | 100% | 100.0% |
| xz CLI — *baseline* | 20T defaults | 3.6 | 100% | 100.0% |

### Decompression (shared xz-produced references)

| Implementation | Config | Single-block MB/s | % of xz | Multi-block MB/s | % of xz |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T | 4,990.6 | 10596% | 8,034.8 | 16844% |
| **LzmaNet** | 20T | 1,440.5 | 3145% | 2,637.6 | 5772% |
| xz CLI — *baseline* | 1T | 47.1 | 100% | 47.7 | 100% |
| xz CLI — *baseline* | 20T | 45.8 | 100% | 45.7 | 100% |

Incompressible data is stored in uncompressed LZMA2 chunks, so decoding is close
to a memory copy for the in-process library; the xz CLI is bounded by pipe I/O.

## Key Takeaways

- **Compression speed**: LzmaNet is **2.3–2.6× faster** than native xz on real-world data at every thread count, with honest like-for-like configurations.
- **Compression ratio**: xz compresses ~4.5 percentage points better on Silesia (23.2% vs 27.7%). LzmaNet uses a greedy hash-chain (HC4) match finder; xz preset 6 uses BT4 with near-optimal parsing. This is an algorithmic trade (speed vs ratio), not overhead — closing it would need a BT match finder + optimal parser.
- **Decompression**: LzmaNet decodes **faster than the native xz CLI single-threaded** on identical real-world input (102–108% on Silesia), helped by the carry-less-multiply CRC64 verification. With multi-block streams and threads, parallel block decode reaches **2.6–3.1×** the xz CLI rate on Silesia.
- **Incompressible data**: LzmaNet detects expanding chunks early and stores them raw — 1.6–2.6× faster than xz to compress, and **multi-GB/s to decompress** in-process (stored blocks decode as memcpy + CLMUL CRC).
- **Interoperability**: every decompression figure above is LzmaNet decoding files produced by the native xz encoder, byte-verified.

## How the managed implementation stays close to C

The decoder applies the same disciplines liblzma uses, expressed in C#:

- **Span-based range decoder** — the coder reads from a cached `ReadOnlySpan<byte>`; there is no `Memory<T>.Span` conversion or stream call per normalization.
- **Register discipline** — `range`, `code`, position, LZMA state, and rep distances live in locals inside one decode loop and are written back once per chunk.
- **No bounds checks on probability models** — probability arrays are accessed via `MemoryMarshal.GetArrayDataReference` + `Unsafe.Add`, mirroring liblzma's pointer arithmetic.
- **Output-as-window** — every XZ block starts with a dictionary reset, so the block's output buffer *is* the dictionary. Each decoded byte is written once, match copies are straight in-buffer copies with a geometric overlap strategy, and no circular-buffer arithmetic runs in the hot loop.
- **Carry-less-multiply CRC32/CRC64** — integrity checks use PCLMULQDQ folding where available (slicing-by-8 fallback), with constants derived from the polynomial at startup.

The encoder carries the **dictionary across LZMA2 chunks** within a block (like xz), uses a hash-chain match finder with power-of-two masked chain slots, SIMD match comparison (32 bytes per step with `Vector256`, 8-byte word fallback), a buffered range encoder, and early abort on expanding (incompressible) chunks.

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
