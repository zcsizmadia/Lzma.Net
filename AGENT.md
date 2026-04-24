# Agent Guide

Instructions for AI coding agents working on this codebase.

## Project Overview

Pure managed C# implementation of the XZ/LZMA2/LZMA compression format, ported from [XZ Utils 5.8.3](https://github.com/tukaani-project/xz). No native binaries, no P/Invoke.

## Build & Test

```shell
# Build (all 3 TFMs)
dotnet build -c Release

# Run all tests (all TFMs)
dotnet test -c Release

# Run tests on a single TFM (faster iteration)
dotnet test -c Release --framework net10.0

# Run benchmarks (not in solution, net10.0 only)
dotnet run --project bench/LzmaNet.Bench -c Release
```

The SDK is .NET 10 preview. All three commands run across `net8.0`, `net9.0`, and `net10.0` targets.

## Solution Structure

```
Lzma.Net.slnx                    # XML-format solution (not classic .sln)
LzmaNet/                          # Main library
  Check/                          # CRC32, CRC64 implementations
  Filters/                        # BCJ/Delta filters (X86, ARM, ARM64, etc.)
  LZ/                             # LZ77 match finder (HC4)
  Lzma/                           # LZMA encoder/decoder
  Lzma2/                          # LZMA2 chunked wrapper
  RangeCoder/                     # Range encoder/decoder
  Xz/                             # XZ container (header, block, index, footer)
  XzCompressor.cs                 # One-shot compress/decompress API (sync + async)
  XzCompressStream.cs             # Streaming compression (sync + async + IAsyncDisposable)
  XzDecompressStream.cs           # Streaming decompression (sync + async)
  XzCompressOptions.cs            # Options + XzCheckType enum
  LzmaException.cs                # Exception types
LzmaNet.Tests/                    # TUnit tests
LzmaNet.Benchmark/                # Benchmark (not in solution, net10.0 only)
```

## Key Conventions

### Language & Framework
- **C# latest**, file-scoped namespaces (`namespace LzmaNet.Xz;`)
- **Nullable** enabled, **implicit usings** enabled
- **AllowUnsafeBlocks** enabled — use unsafe code when needed for performance
- Namespaces follow folder structure: `LzmaNet.{FolderName}`

### Zero-Copy Design
This is a core design principle. Always prefer:
- `Span<T>` / `ReadOnlySpan<T>` for buffer parameters
- `ArrayPool<byte>.Shared` for temporary buffers (always return in `finally`)
- `stackalloc` for small fixed-size buffers
- Avoid `byte[]` allocations in hot paths

### Testing
- **TUnit** — the test project requires `<OutputType>Exe</OutputType>` and `<IsTestProject>true</IsTestProject>`
- Uses Microsoft.Testing.Platform runner (configured via `global.json`)
- Tests can access internals via `InternalsVisibleTo`
- The `xz` CLI is available at `/usr/bin/xz` (WSL) for interop tests
- Use `await Assert.That(...)` for assertions (TUnit assertions are async)
- For byte array equality, use `.SequenceEqual(expected)).IsTrue()` (not `.IsEqualTo()` which does reference equality)

### BCJ Filters
The filters in `src/LzmaNet/Filters/` are ported from liblzma C source. When modifying:
- Preserve algorithmic fidelity to the C originals
- Watch for C-to-C# pitfalls: `uint` array indexing (needs cast to `int`), bitwise AND as bool (`& mask` needs `!= 0`), signed/unsigned arithmetic differences
- Filters implement `IBcjFilter` with `Encode`/`Decode` methods
- `FilterFactory` creates filter instances by filter ID

### Public API Surface
- Public types: `XzCompressor`, `XzCompressStream`, `XzDecompressStream`, `XzCompressOptions`, `XzCheckType`, `LzmaException`, `LzmaDataErrorException`, `LzmaFormatException`
- Everything else is `internal`
- XML documentation is generated (`GenerateDocumentationFile`)
- Async variants use `ReadOnlyMemory<byte>` instead of `ReadOnlySpan<byte>` (spans cannot cross `await` boundaries)

## Architecture

```
XzCompressor / XzCompressStream / XzDecompressStream   (public API)
  └─ XZ container    (XzHeader, XzBlock, XzIndex — header/block/index/footer)
       └─ BCJ/Delta filters   (X86, ARM, ARM64, IA64, PowerPC, SPARC, RISC-V, ARM-Thumb, Delta)
            └─ LZMA2 codec    (chunked wrapper with dictionary resets)
                 └─ LZMA codec   (LZ77 + adaptive range coding)
                      ├─ HC4 match finder
                      ├─ Range encoder / decoder
                      └─ CRC32 / CRC64
```

## Common Pitfalls

1. **TUnit assertions are async** — always `await Assert.That(...)`, not `Assert.Equal(...)`
2. **Byte array equality** — use `a.SequenceEqual(b)).IsTrue()`, not `.IsEqualTo()` (reference equality) or `.IsEquivalentTo()` (O(n²) set comparison)
3. **Multi-TFM builds** — errors may appear in one TFM but not others; always check all three
4. **ArrayPool returns** — every `Rent()` must have a matching `Return()` in a `finally` block
5. **OutputWindow.TotalPos is `long`** — supports >2GB single blocks; callers must cast appropriately
6. **XZ spec compliance** — the decoder handles concatenated streams, validates backward size, and cross-validates index records against decoded blocks
7. **ReadOnlySpan in async** — cannot use `ReadOnlySpan<byte>` across `await` boundaries; use `ReadOnlyMemory<byte>` or `byte[]` instead

## License

[0BSD](https://opensource.org/license/0bsd)
