# Benchmarks

Performance comparison of **LzmaNet** (pure managed C#) against **liblzma** (native C) via the [ZCS.XZ](https://github.com/AshleighAdams/ZCS.XZ) P/Invoke wrapper and the `xz` command-line tool.

## Setup

- **Data**: 16 MB of semi-compressible synthetic data (repeating patterns + random bytes)
- **Preset**: 6 (default)
- **Platform**: .NET 10, Windows (WSL2 for xz CLI), AMD Ryzen 9 (20 logical cores)
- **xz version**: 5.8.3

## Results

### Single-threaded (1 thread)

| Implementation | Compress | Decompress | Ratio | Compressed Size |
|---|---:|---:|---:|---:|
| **LzmaNet** (pure C#) | 10.9 MB/s | 40.7 MB/s | 22.9% | 3,840,936 |
| ZCS.XZ (liblzma P/Invoke) | 8.9 MB/s | 64.5 MB/s | 22.9% | 3,843,916 |
| xz CLI (native) | 7.0 MB/s | 23.5 MB/s | 22.9% | 3,843,916 |

### Multi-threaded (20 threads)

| Implementation | Compress | Decompress | Ratio | Compressed Size |
|---|---:|---:|---:|---:|
| **LzmaNet** (pure C#) | 49.7 MB/s | 39.9 MB/s | 22.9% | 3,841,412 |
| ZCS.XZ (liblzma P/Invoke) | 9.0 MB/s | 65.3 MB/s | 22.9% | 3,843,924 |
| xz CLI (native) | 7.2 MB/s | 28.3 MB/s | 22.9% | 3,843,924 |

## Key Takeaways

- **Compression**: LzmaNet is **1.2–1.6× faster** than native liblzma for single-threaded compression, and dramatically faster with multi-threaded compression thanks to parallel block processing.
- **Decompression**: liblzma (via ZCS.XZ) is **~1.6× faster** at decompression. This is expected — liblzma's decoder is hand-optimized C with decades of micro-optimization, and decompression is heavily bottlenecked by branch-heavy range decoding that benefits from C compiler optimizations (branch prediction hints, instruction scheduling) that the JIT doesn't yet match.
- **Compression ratio**: All implementations produce essentially identical ratios (~22.9%) at the same preset, confirming algorithmic correctness.
- **xz CLI overhead**: The CLI tool shows additional process startup and I/O overhead, making it slower than the library-based approaches.

## Why is liblzma decompression faster?

LZMA decompression is dominated by **range decoding** — a tight inner loop of branch-heavy, data-dependent arithmetic operations. liblzma's C decoder has been micro-optimized over 20+ years with:

- Carefully chosen data layouts to minimize cache misses
- Branch-free arithmetic patterns where possible
- Compiler-specific hints (`__builtin_expect`, `restrict`, etc.)
- Manual inlining and loop unrolling tuned for specific architectures

The .NET JIT produces excellent code for most workloads, but the LZMA decoder's deeply nested, branch-heavy structure with unpredictable data-dependent branches is a worst case for JIT compilation. The native C compiler has more optimization time and can apply whole-program optimizations that the JIT cannot.

## Running the benchmarks

```shell
dotnet run --project bench/LzmaNet.Bench -c Release
```

Requires the `xz` CLI to be available in `PATH` (or at `/usr/bin/xz`) for the native comparison. The [ZCS.XZ](https://www.nuget.org/packages/ZCS.XZ) NuGet package is included in the benchmark project.
