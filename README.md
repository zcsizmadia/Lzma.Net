# Lzma.Net

[![Build](https://github.com/zcsizmadia/Lzma.Net/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/zcsizmadia/Lzma.Net/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/Lzma.Net?logo=nuget)](https://www.nuget.org/packages/Lzma.Net)
[![Downloads](https://img.shields.io/nuget/dt/Lzma.Net?logo=nuget)](https://www.nuget.org/packages/Lzma.Net)
![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512BD4?logo=dotnet)
[![License](https://img.shields.io/github/license/zcsizmadia/Lzma.Net)](LICENSE)

A **native C# implementation** of the XZ/LZMA2/LZMA compression format. No native binaries, no P/Invoke, no liblzma dependency — just pure managed C# code that runs anywhere .NET runs.

## Features

- **100% managed C#** — zero native dependencies, works on any platform .NET supports
- **XZ format** — full read/write support for the `.xz` container format ([spec](https://tukaani.org/xz/xz-file-format.txt))
- **LZMA2 codec** — chunked LZMA compression with automatic dictionary resets
- **Streaming API** — `XzCompressStream` and `XzDecompressStream` for processing data without loading it all into memory
- **One-shot API** — `XzCompressor.Compress` / `Decompress` for simple byte-array operations
- **Async API** — `CompressAsync` / `DecompressAsync` and async stream methods for non-blocking I/O
- **Multi-threaded compression** — pipelined parallel block compression via the `Threads` option; output is byte-identical to single-threaded mode
- **Multi-threaded decompression** — parallel block decoding of multi-block streams via `XzCompressor.Decompress(data, threads)` / `new XzDecompressStream(stream, threads)`, in both sync and async read paths
- **Random access** — `XzSeekableStream` seeks anywhere in the uncompressed data and decodes only the blocks it needs, using the XZ index
- **BCJ/Delta filters** — encode *and* decode support for x86, ARM, ARM64, ARM-Thumb, PowerPC, SPARC, IA-64, RISC-V, and Delta filters; BCJ dramatically improves ratio on executables (`XzCompressOptions.Filter`)
- **Decompression-bomb protection** — `XzDecompressOptions.MaxOutputSize` rejects oversized output claims before any allocation happens
- **Presets 0–9** — matching `xz` CLI compression levels and dictionary sizes; presets 7–9 use a BT4 match finder with price-based **optimal parsing** for near-xz ratios
- **Extreme mode** — equivalent to `xz -e`, spends more CPU time for better compression
- **Legacy `.lzma` format** — `LzmaAloneCompressStream` / `LzmaAloneDecompressStream` for the LZMA-alone format (7-Zip / `xz --format=lzma` compatible, including unknown-size streams)
- **Progress reporting** — `IProgress<long>` hooks on both compression and decompression options
- **Integrity checks** — CRC32, CRC64, SHA-256, or no check
- **Concatenated streams** — reads multiple XZ streams appended back-to-back
- **Zero-copy design** — uses `Span<T>`, `ReadOnlySpan<T>`, `ArrayPool<T>`, and `stackalloc` throughout
- **SIMD-accelerated** — carry-less-multiply CRC32/CRC64 (PCLMULQDQ on x64, PMULL on ARM64) and vectorized match comparison (AVX2/NEON), with portable fallbacks
- **.NET 8 / 9 / 10** — multi-target support

## Benchmarks

See [BENCHMARK.md](BENCHMARK.md) for the full methodology and results across real-world, synthetic, and incompressible data.

Quick summary — [Silesia corpus](https://sun.aei.polsl.pl/~sdeor/index.php?page=silesia) (211.9 MB real-world mix), medians of repeated runs, percentages relative to the native `xz` CLI at the same preset and thread count. Decompression is measured on identical xz-produced input files, byte-verified:

| | Compress | % of xz | Decompress | % of xz | Ratio |
|---|---:|---:|---:|---:|---:|
| **LzmaNet** (preset 6, 1 thread) | 4.7 MB/s | 174% | 80.1 MB/s | 102% | 27.0% |
| **LzmaNet** (preset 6, 20 threads) | 17.0 MB/s | 140% | 369.0 MB/s | 222% | 27.0% |
| **LzmaNet** (preset 9, optimal parser) | 1.6 MB/s | 67% | — | — | **23.0%** |
| xz CLI (preset 6, 1 thread) — *baseline* | 2.7 MB/s | 100% | 78.8 MB/s | 100% | 23.2% |
| xz CLI (preset 6, 20 threads) — *baseline* | 12.1 MB/s | 100% | 165.9 MB/s | 100% | 23.4% |
| xz CLI (-9, 1 thread) — *baseline* | 2.4 MB/s | 100% | — | — | 23.0% |

At the default preset, LzmaNet compresses ~1.7× faster than native xz and decodes at native speed single-threaded (2.2× with parallel block decode of multi-block streams). At preset 9 — BT4 match finder with price-based optimal parsing, the same architecture as xz's high presets — LzmaNet **matches `xz -9`'s compression ratio** (within 0.2% of its output size) at ~two-thirds of its speed.

## Installation

```shell
dotnet add package LzmaNet
```

## Quick Start

### Compress and decompress a byte array

```csharp
using LzmaNet;

byte[] original = File.ReadAllBytes("data.bin");

// Compress with default settings (preset 6, CRC64)
byte[] compressed = XzCompressor.Compress(original);

// Decompress
byte[] restored = XzCompressor.Decompress(compressed);
```

### Compress with options

```csharp
using LzmaNet;

var options = new XzCompressOptions
{
    Preset  = 9,       // Maximum compression
    Extreme = true,    // Spend more CPU for slightly better ratio
    Threads = 0,       // Use all available CPUs
};

byte[] compressed = XzCompressor.Compress(data, options);
```

### Stream API — compress a file

```csharp
using LzmaNet;

using var input  = File.OpenRead("data.bin");
using var output = File.Create("data.xz");
using (var xz = new XzCompressStream(output))
{
    input.CopyTo(xz);
}
```

### Stream API — decompress a file

```csharp
using LzmaNet;

using var input  = File.OpenRead("data.xz");
using var output = File.Create("data.bin");
using var xz = new XzDecompressStream(input);
xz.CopyTo(output);
```

### Async — compress and decompress

```csharp
using LzmaNet;

// One-shot async
byte[] compressed = await XzCompressor.CompressAsync(data);
byte[] restored   = await XzCompressor.DecompressAsync(compressed);
```

### Async — stream API

```csharp
using LzmaNet;

using var input  = File.OpenRead("data.bin");
using var output = File.Create("data.xz");
await using (var xz = new XzCompressStream(output))
{
    await input.CopyToAsync(xz);
}
```

### Random access — read a slice without decompressing the file

```csharp
using LzmaNet;

// Works best on multi-block files (BlockSize controls seek granularity)
using var xz = new XzSeekableStream(File.OpenRead("large.xz"));
xz.Position = 100_000_000;          // seek in UNCOMPRESSED coordinates
byte[] slice = new byte[4096];
xz.ReadExactly(slice);              // decodes only the containing block(s)
```

### Compress an executable with a BCJ filter

```csharp
using LzmaNet;

var options = new XzCompressOptions
{
    Preset = 6,
    Filter = XzFilterType.X86,   // rel->abs branch conversion for x86/x64 code
};
byte[] compressed = XzCompressor.Compress(File.ReadAllBytes("app.dll"), options);
```

### Decompress untrusted data safely

```csharp
using LzmaNet;

var options = new XzDecompressOptions
{
    MaxOutputSize = 100 * 1024 * 1024, // reject bombs before allocating
    Threads = 0,
};
byte[] data = XzCompressor.Decompress(untrustedBytes, options);
```

### ASP.NET — decompress an upload on the fly

```csharp
app.MapPost("/upload", async (HttpRequest request) =>
{
    using var xz = new XzDecompressStream(request.Body);
    using var output = File.Create("uploaded.bin");
    await xz.CopyToAsync(output);
});
```

## Compression Options

All tuning knobs are exposed through the `XzCompressOptions` class:

| Property | Type | Default | Description |
|---|---|---|---|
| `Preset` | `int` | `6` | Compression level 0–9. Higher = smaller output, more CPU/memory. Presets 0–2 are greedy, 3–6 use lazy matching, 7–9 use BT4 + optimal parsing. |
| `Extreme` | `bool` | `false` | When `true`, spends significantly more CPU to improve ratio. Equivalent to `xz -e`. |
| `Threads` | `int` | `1` | `0` = all CPUs, `1` = single-threaded, `N` = N threads. |
| `CheckType` | `XzCheckType` | `Crc64` | Integrity check: `None`, `Crc32`, `Crc64`, or `Sha256`. |
| `DictionarySize` | `int?` | `null` | Override the preset's dictionary size (bytes, min 4 KB). For one-shot `Compress`/`CompressAsync` and `.lzma` compression, the effective dictionary is automatically capped at the input size when not set explicitly. |
| `BlockSize` | `int?` | `null` | XZ block size (bytes, min 4 KB). `null` = `max(dict×2, 1 MB)`. Blocks are the unit of parallel processing and random access. |
| `Filter` | `XzFilterType` | `None` | Optional BCJ/Delta pre-compression filter (`X86`, `Arm64`, `Delta`, …). |
| `DeltaDistance` | `int` | `1` | Byte distance for the Delta filter (1–256). |

Decompression is configured through `XzDecompressOptions` (`Threads`, `MaxOutputSize`).

### Preset dictionary sizes

| Preset | Dictionary Size |
|--------|----------------|
| 0 | 64 KB |
| 1 | 1 MB |
| 2 | 2 MB |
| 3–4 | 4 MB |
| 5–6 | 8 MB |
| 7 | 16 MB |
| 8 | 32 MB |
| 9 | 64 MB |

## Interoperability

Output is fully compatible with the standard `xz` tool and any other XZ-compliant decoder. The test suite validates round-trip interoperability with the `xz` CLI in both directions.

## Architecture

LzmaNet is structured as a set of layered codecs, all implemented in pure C#:

```
XzCompressor / XzCompressStream / XzDecompressStream / XzSeekableStream   (public API)
LzmaAloneCompressStream / LzmaAloneDecompressStream   (legacy .lzma format)
  └─ XZ container format   (header, block, index, footer)
       └─ LZMA2 codec      (chunked wrapper over LZMA)
            └─ LZMA codec  (LZ77 + adaptive range coding)
                 ├─ Match finders: HC4 (hash-chain) and BT4 (binary tree)
                 ├─ Parsers: greedy / lazy / price-based optimal
                 ├─ Range encoder / decoder
                 └─ CRC32 / CRC64 integrity checks
```

## Acknowledgments

This implementation is based on the algorithms and file format from [XZ Utils 5.8.3](https://github.com/tukaani-project/xz), originally created by **Lasse Collin** and maintained by the [Tukaani Project](https://tukaani.org/xz/). The BCJ filter implementations are ported from the liblzma C source.

Special thanks to:

- **Lasse Collin** — original author of liblzma and the XZ file format
- **Jia Tan** and other contributors to the Tukaani Project
- **Igor Pavlov** — creator of the LZMA algorithm and [7-Zip](https://www.7-zip.org/)

## License

[0BSD](https://opensource.org/license/0bsd) — free for any use, no attribution required.
