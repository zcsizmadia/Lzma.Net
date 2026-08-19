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
| **LzmaNet** | 1T defaults | 6.1 | 226% | 27.7% | 58,797,716 |
| **LzmaNet** | 20T defaults | 26.6 | 240% | 27.8% | 58,951,940 |
| xz CLI — *baseline* | 1T defaults | 2.7 | 100% | 23.2% | 49,201,356 |
| xz CLI — *baseline* | 20T defaults | 11.1 | 100% | 23.4% | 49,488,036 |

### Decompression (shared xz-produced references)

| Implementation | Config | Single-block MB/s | % of xz | Multi-block MB/s | % of xz |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T | 70.8 | 95% | 65.1 | 91% |
| **LzmaNet** | 20T | 301.9 | 200% | 348.5 | 224% |
| xz CLI — *baseline* | 1T | 74.6 | 100% | 71.4 | 100% |
| xz CLI — *baseline* | 20T | 150.9 | 100% | 155.8 | 100% |

## Synthetic 16 MB — patterns + random bytes

### Compression

| Implementation | Config | MB/s | % of xz | Ratio | Size |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T defaults | 13.0 | 131% | 23.0% | 3,863,160 |
| **LzmaNet** | 20T defaults | 15.3 | 151% | 23.0% | 3,863,160 |
| **LzmaNet** | 20T BlockSize=1MiB | 126.8 | 237% | 22.7% | 3,809,616 |
| xz CLI — *baseline* | 1T defaults | 9.9 | 100% | 22.9% | 3,843,916 |
| xz CLI — *baseline* | 20T defaults | 10.1 | 100% | 22.9% | 3,843,924 |
| xz CLI — *baseline* | 20T --block-size=1MiB | 53.4 | 100% | 22.6% | 3,792,704 |

Neither implementation parallelizes 16 MB at default block sizes (a single block has
nothing to split); with matched 1 MiB blocks both scale, and LzmaNet is 2.4× faster.

### Decompression (shared xz-produced references)

| Implementation | Config | Single-block MB/s | % of xz | Multi-block MB/s | % of xz |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T | 67.4 | 106% | 69.6 | 109% |
| **LzmaNet** | 20T | 68.3 | 76% | 594.4 | 629% |
| xz CLI — *baseline* | 1T | 63.6 | 100% | 63.8 | 100% |
| xz CLI — *baseline* | 20T | 89.6 | 100% | 94.5 | 100% |

## Incompressible 32 MB — random bytes

### Compression

| Implementation | Config | MB/s | % of xz | Ratio |
|---|---|---:|---:|---:|
| **LzmaNet** | 1T defaults | 4.4 | 157% | 100.0% |
| **LzmaNet** | 20T defaults | 9.1 | 246% | 100.0% |
| xz CLI — *baseline* | 1T defaults | 2.8 | 100% | 100.0% |
| xz CLI — *baseline* | 20T defaults | 3.7 | 100% | 100.0% |

### Decompression (shared xz-produced references)

| Implementation | Config | Single-block MB/s | % of xz | Multi-block MB/s | % of xz |
|---|---|---:|---:|---:|---:|
| **LzmaNet** | 1T | 1,182.6 | 975% | 1,321.9 | 1067% |
| **LzmaNet** | 20T | 737.7 | 658% | 1,887.0 | 1708% |
| xz CLI — *baseline* | 1T | 121.3 | 100% | 123.9 | 100% |
| xz CLI — *baseline* | 20T | 112.2 | 100% | 110.5 | 100% |

Incompressible data is stored in uncompressed LZMA2 chunks, so decoding is close
to a memory copy for the in-process library; the xz CLI is bounded by pipe I/O.

## Key Takeaways

- **Compression speed**: LzmaNet is **2.3–2.4× faster** than native xz on real-world data at every thread count, with honest like-for-like configurations.
- **Compression ratio**: xz compresses ~4.5 percentage points better on Silesia (23.2% vs 27.7%). LzmaNet uses a greedy hash-chain (HC4) match finder; xz preset 6 uses BT4 with near-optimal parsing. This is an algorithmic trade (speed vs ratio), not overhead — closing it would need a BT match finder + optimal parser.
- **Decompression**: LzmaNet decodes at **91–109% of the native xz CLI** single-threaded on identical input files. With multi-block streams and threads, parallel block decode reaches **2.2× (large blocks) to 6.3× (many small blocks)** the xz CLI rate.
- **Incompressible data**: LzmaNet detects expanding chunks early and stores them raw — 1.6–2.5× faster than xz to compress, and near-memcpy to decompress in-process.
- **Interoperability**: every decompression figure above is LzmaNet decoding files produced by the native xz encoder, byte-verified.

## How the managed implementation stays close to C

The decoder applies the same disciplines liblzma uses, expressed in C#:

- **Span-based range decoder** — the coder reads from a cached `ReadOnlySpan<byte>`; there is no `Memory<T>.Span` conversion or stream call per normalization.
- **Register discipline** — `range`, `code`, position, LZMA state, and rep distances live in locals inside one decode loop and are written back once per chunk.
- **No bounds checks on probability models** — probability arrays are accessed via `MemoryMarshal.GetArrayDataReference` + `Unsafe.Add`, mirroring liblzma's pointer arithmetic.
- **Output-as-window** — every XZ block starts with a dictionary reset, so the block's output buffer *is* the dictionary. Each decoded byte is written once, match copies are straight in-buffer copies with a geometric overlap strategy, and no circular-buffer arithmetic runs in the hot loop.
- **Slicing-by-8 CRC32/CRC64** — integrity checks run at multi-GB/s.

The encoder carries the **dictionary across LZMA2 chunks** within a block (like xz), uses a hash-chain match finder with power-of-two masked chain slots, 8-byte-at-a-time match comparison, a buffered range encoder, and early abort on expanding (incompressible) chunks.

## Running the benchmarks

```shell
dotnet run --project LzmaNet.Benchmark -c Release
```

Requires the `xz` CLI in `PATH` (or at `/usr/bin/xz`) for the native comparison and
for producing the cross-decode reference files. The Silesia corpus (~68 MB download)
is fetched and cached automatically on first run.
