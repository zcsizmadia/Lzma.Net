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
- **Multi-threaded compression** — parallel block compression via the `Threads` option
- **Multi-threaded decompression** — parallel block decoding of multi-block streams via `XzCompressor.Decompress(data, threads)` / `new XzDecompressStream(stream, threads)`
- **Presets 0–9** — matching `xz` CLI compression levels and dictionary sizes
- **Extreme mode** — equivalent to `xz -e`, spends more CPU time for better compression
- **Integrity checks** — CRC32, CRC64, SHA-256, or no check
- **Concatenated streams** — reads multiple XZ streams appended back-to-back
- **Zero-copy design** — uses `Span<T>`, `ReadOnlySpan<T>`, `ArrayPool<T>`, and `stackalloc` throughout
- **.NET 8 / 9 / 10** — multi-target support

## Benchmarks

See [BENCHMARK.md](BENCHMARK.md) for detailed performance comparisons against native liblzma.

Quick summary (16 MB data, preset 6; percentages relative to the `xz` CLI at the same thread count):

| | Compress | % of xz | Decompress | % of xz |
|---|---:|---:|---:|---:|
| **LzmaNet** (pure C#, 1 thread) | 38.9 MB/s | 437% | 60.8 MB/s | 99% |
| **LzmaNet** (pure C#, 20 threads) | 186.0 MB/s | 425% | 333.3 MB/s | 454% |
| liblzma (native C, 1 thread) | 10.5 MB/s | 118% | 78.0 MB/s | 127% |
| xz CLI (native, 1 thread) — *baseline* | 8.9 MB/s | 100% | 61.3 MB/s | 100% |
| xz CLI (native, 20 threads, `--block-size=1MiB`) — *baseline* | 43.8 MB/s | 100% | 73.4 MB/s | 100% |

*The 20-thread xz baseline uses `--block-size=1MiB` to match LzmaNet's multi-threaded block layout — without it, xz's default 24 MiB blocks leave 16 MB of input as a single block and `-T 20` brings no speedup at all.*

LzmaNet compresses ~3.7× faster than native liblzma and decompresses at native `xz` speed single-threaded. Multi-block streams can additionally be compressed *and* decompressed with parallel block processing (`XzCompressOptions.Threads`, `XzCompressor.Decompress(data, threads)`).

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
| `Preset` | `int` | `6` | Compression level 0–9. Higher = smaller output, more CPU/memory. |
| `Extreme` | `bool` | `false` | When `true`, spends significantly more CPU to improve ratio. Equivalent to `xz -e`. |
| `Threads` | `int` | `1` | `0` = all CPUs, `1` = single-threaded, `N` = N threads. |
| `CheckType` | `XzCheckType` | `Crc64` | Integrity check: `None`, `Crc32`, `Crc64`, or `Sha256`. |
| `DictionarySize` | `int?` | `null` | Override the preset's dictionary size (bytes, min 4 KB). |
| `BlockSize` | `int?` | `null` | XZ block size (bytes, min 4 KB). `null` = `max(dict×2, 1 MB)`. |

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
XzCompressor / XzCompressStream / XzDecompressStream   (public API)
  └─ XZ container format   (header, block, index, footer)
       └─ LZMA2 codec      (chunked wrapper over LZMA)
            └─ LZMA codec  (LZ77 + adaptive range coding)
                 ├─ HC4 match finder (hash-chain, 4-byte hashing)
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
