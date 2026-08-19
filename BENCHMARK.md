# Benchmarks

Performance comparison of **LzmaNet** (pure managed C#) against **liblzma** (native C) via the [ZCS.XZ](https://github.com/AshleighAdams/ZCS.XZ) P/Invoke wrapper and the `xz` command-line tool.

## Setup

- **Data**: 16 MB of semi-compressible synthetic data (repeating patterns + random bytes)
- **Preset**: 6 (default)
- **Platform**: .NET 10, Windows (WSL2 for xz CLI), AMD Ryzen 9 (20 logical cores)
- **xz version**: 5.8.3

## Results

Percentages are relative to the `xz` CLI (native) at the same thread count.

### Single-threaded (1 thread)

| Implementation | Compress | % of xz | Decompress | % of xz | Ratio | Compressed Size |
|---|---:|---:|---:|---:|---:|---:|
| **LzmaNet** (pure C#) | 38.9 MB/s | 437% | 60.8 MB/s | 99% | 22.9% | 3,840,956 |
| ZCS.XZ (liblzma P/Invoke) | 10.5 MB/s | 118% | 78.0 MB/s | 127% | 22.9% | 3,843,916 |
| xz CLI (native) — *baseline* | 8.9 MB/s | 100% | 61.3 MB/s | 100% | 22.9% | 3,843,916 |

### Multi-threaded (20 threads)

In this table the xz CLI is invoked with `--block-size=1MiB`, matching LzmaNet's
multi-threaded block layout, so both split the input into 16 independently
processed blocks (see the next section for why this matters).

| Implementation | Compress | % of xz | Decompress | % of xz | Ratio | Compressed Size |
|---|---:|---:|---:|---:|---:|---:|
| **LzmaNet** (pure C#) | 186.0 MB/s | 425% | 333.3 MB/s | 454% | 22.9% | 3,841,428 |
| ZCS.XZ (liblzma P/Invoke) | 9.0 MB/s | 21% | 74.1 MB/s | 101% | 22.9% | 3,843,924 |
| xz CLI (native, `--block-size=1MiB`) — *baseline* | 43.8 MB/s | 100% | 73.4 MB/s | 100% | 22.6% | 3,792,704 |

LzmaNet's multi-threaded decompression uses `XzCompressor.Decompress(data, threads)` /
`new XzDecompressStream(stream, threads)`, which decode XZ blocks in parallel.
The input here is the multi-block stream produced by multi-threaded compression;
single-block streams decode at the single-threaded rate.

### What "multi-threaded" means for each implementation

The thread count is passed to every implementation that accepts one, but they do
not all parallelize equally on this workload:

- **LzmaNet** — the benchmark sets `Threads = N` *and* `BlockSize = 1 MB`, producing a 16-block stream. Both compression and decompression genuinely run N-wide.
- **xz CLI** — invoked with `-T N`, plus `--block-size=1MiB` in the multi-threaded run. The explicit block size matters: in threaded mode xz's *default* block size is 3× the dictionary (24 MB at preset 6), so 16 MB of input becomes a **single block** and `-T 20` yields no speedup at all (measured: ~9 MB/s, same as one thread). With 1 MiB blocks it genuinely parallelizes (~4.7× its single-thread rate) at a small ratio cost. Larger inputs (≫ 24 MB) would parallelize even at the default block size.
- **ZCS.XZ** — `XZCompressOptions.Threads` is passed for compression, but the wrapper exposes no block-size option, so liblzma's threaded encoder keeps its 24 MB default block and the 16 MB input stays a single block — effectively serial. Its `XZDecompressStream` exposes **no thread option at all** (single-threaded liblzma decoder), so its decompress numbers are single-threaded in both tables.

## Key Takeaways

- **Compression**: LzmaNet is **~3.7× faster** than native liblzma single-threaded, and **~4.2× faster** than a fairly configured `xz -T20 --block-size=1MiB` with parallel block compression.
- **Decompression**: LzmaNet matches the native `xz` CLI single-threaded and reaches ~80% of the in-process liblzma rate. With parallel block decode of multi-block streams it is **~4.5× faster** than `xz -T20` on the same block layout.
- **Compression ratio**: All implementations produce essentially identical ratios (~22.9%) at the same preset, confirming algorithmic correctness.
- **xz CLI overhead**: The CLI tool shows additional process startup and I/O overhead versus the library-based approaches.

## How the managed decoder stays close to C

LZMA decompression is dominated by **range decoding** — a tight loop of branch-heavy,
data-dependent arithmetic. The LzmaNet decoder applies the same disciplines liblzma
uses, expressed in C#:

- **Span-based range decoder** — the coder reads from a cached `ReadOnlySpan<byte>`; there is no `Memory<T>.Span` conversion or stream call per normalization.
- **Register discipline** — `range`, `code`, position, LZMA state, and rep distances live in locals inside one decode loop and are written back once per chunk.
- **No bounds checks on probability models** — probability arrays are accessed via `MemoryMarshal.GetArrayDataReference` + `Unsafe.Add`, mirroring liblzma's pointer arithmetic.
- **Output-as-window** — every XZ block starts with a dictionary reset, so the block's output buffer *is* the dictionary. Each decoded byte is written once (no separate sliding-window buffer), match copies are straight in-buffer copies with a geometric overlap strategy, and no circular-buffer arithmetic runs in the hot loop.
- **Slicing-by-8 CRC32/CRC64** — integrity checks run at multi-GB/s instead of byte-at-a-time table lookups.

The encoder gets its edge from a hash-chain match finder with power-of-two masked
chain slots (no integer division), 8-byte-at-a-time match comparison, a buffered
range encoder, and a match-finder window capped at the LZMA2 chunk size so per-chunk
table resets touch well under 1 MB.

## Running the benchmarks

```shell
dotnet run --project LzmaNet.Benchmark -c Release
```

Requires the `xz` CLI to be available in `PATH` (or at `/usr/bin/xz`) for the native comparison. The [ZCS.XZ](https://www.nuget.org/packages/ZCS.XZ) NuGet package is included in the benchmark project.
