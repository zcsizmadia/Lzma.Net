# Agent Guide

Instructions for AI coding agents working on this codebase.

## Project Overview

Pure managed C# implementation of the XZ/LZMA2/LZMA compression format, ported from [XZ Utils 5.8.4](https://github.com/tukaani-project/xz). No native binaries, no P/Invoke.

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
The library also builds `netstandard2.0` and `netstandard2.1` (locally and in CI)
for consumers that are not on .NET 8+; see **Portable targets** below.

## Solution Structure

```
Lzma.Net.slnx                    # XML-format solution (not classic .sln)
LzmaNet/                          # Main library
  Check/                          # CRC32, CRC64 implementations
  Compatibility/                  # netstandard shims (see Portable targets)
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
LzmaNet.Tests.Portable20/         # Same tests, run against the netstandard2.0 asset
LzmaNet.Tests.Portable21/         # Same tests, run against the netstandard2.1 asset
LzmaNet.Tests.NetFx/              # Targeted suite on .NET Framework (not in the solution)
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

### Performance-Critical Code (hot loops)
- `RangeDecoder` is a **ref struct** over `ReadOnlySpan<byte>` — it cannot be stored in fields, captured in lambdas, or live across `await`; pass it by `ref`
- The LZMA decoder uses **output-as-window**: the block's output buffer is the dictionary (XZ blocks always start with a dict reset), so there is no separate sliding-window buffer (`OutputWindow` was removed)
- Hot-loop state (range, code, LZMA state, rep distances) is hoisted into locals and written back once per chunk — keep it that way
- Probability arrays are accessed via `MemoryMarshal.GetArrayDataReference` + `Unsafe.Add` to skip bounds checks; the index invariants are established by array construction — don't change sizes without checking indices
- CRC32/CRC64 use PCLMULQDQ carry-less-multiply folding (slicing-by-8 fallback). Folding constants are DERIVED from the polynomial at startup in `CrcFolding` — the reflected-CLMUL exponent convention is D+width-1 (low half) / D+width-65 (high half) for fold distance D; don't "fix" these without re-running the CRC reference tests
- The match finder uses power-of-two masked chain slots and SIMD match comparison (`Vector256` 32-byte steps, 8-byte word fallback)
- The `Lzma2Encoder` carries the dictionary ACROSS chunks within a block (first chunk 0xE0, continuation chunks 0x80, state reset 0xA0/0xC0 after stored-uncompressed chunks). `LzmaEncoder.ResetState()` and `ResetDictionary()` are separate on purpose — dictionary resets happen once per XZ block, state resets whenever LZMA2 requires them
- The match finder's window is the full dictionary; its buffer slides by multiples of the power-of-two cyclic size and rebases the hash/chain tables so slot mapping stays valid — don't change the slide granularity without revisiting that invariant
- `LzmaEncoder.EncodeChunk` aborts (returns -1) when a chunk is expanding; the caller stores it uncompressed and must reset state before the next chunk (LZMA2 requires this anyway)
- The encoder uses lazy one-step match lookahead below `LzmaEncoderProperties.NiceLength` (presets ≤ 2 are greedy). The lookahead relies on a strict invariant: the match finder sits at `pos` with its hash inserted whenever a pending candidate carries over — see the comment in `EncodeChunk`
- `XzDecompressOptions.MaxOutputSize` must be enforced BEFORE output allocation (decompression-bomb protection); the checks live in `XzBlock` where claimed sizes are first known
- MT compression is a bounded pipeline (`_inFlight` in `XzCompressStream`): blocks must be completed/written strictly in order (oldest first) — XZ requires sequential block order and the index records must match
- The CRC `Fold` helper dispatches PCLMULQDQ (x86/x64) vs PMULL (ARM64) at JIT time; `CrcFolding.IsSupported` is the single gate — keep both paths mathematically identical
- **BT4 invariant (critical)**: `Lzma2Encoder` appends the WHOLE block to the match finder before encoding any chunk. The binary-tree finder's early subtree adoption assumes its length limit never grows for later insertions; per-chunk feeding shrinks/regrows the limit at every chunk tail and CORRUPTS the tree (false matches). Symbol lengths are capped at chunk boundaries at use time instead
- The optimal parser (`EncodeChunkOptimal`) is a forward DP: node[cur] must be final before `RelaxFrom(cur)` runs (all edges go forward). Node rep/state tracking mirrors the emission methods' updates exactly — if `EncodeRepMatch`/`EncodeMatch` rotation logic changes, `RotateReps`/state transitions in the parser must change with it
- Prices (`RangeCoder/Price.cs`) are 1/16-bit estimates from the reference LZMA table; they affect only ratio, never correctness

### Portable targets

The library ships `netstandard2.0` and `netstandard2.1` alongside
`net8.0`/`net9.0`/`net10.0`, so it runs where .NET 8 is not available. Four rules
keep those targets working.

- **Anything newer than the target goes through `Compatibility/`.** APIs added
  after netstandard2.1 — `Array.MaxLength`, `ObjectDisposedException.ThrowIf`,
  `SHA256.HashData`, the single-argument `Array.Clear`, generic `Enum.IsDefined`
  — and the ones netstandard2.0 also lacks — `Array.Fill`, `Math.Clamp` — are
  called via `Portable`, which forwards to the real framework method on .NET 8+
  and supplies an equivalent otherwise. `BitOperations`, `IsExternalInit`,
  `Index` and `Range` are declared in the framework's own namespaces for the
  builds that need them, so those call sites need no change.
  `MemoryMarshal.GetArrayDataReference` is reached through a per-file alias
  (`Compatibility/MemoryMarshal.cs`) so the decoder's hot loops stay identical.
- **Hardware intrinsics live behind `#if NET8_0_OR_GREATER`.** Neither
  netstandard target can reference `System.Runtime.Intrinsics`, so the CRC
  folding path and the `Vector256`/`Vector128` match comparison are not compiled
  there; `CrcFolding.IsSupported` is hard-false and everything falls through to
  slicing-by-8 and the 64-bit word compare.
- **Use `#if NETSTANDARD2_0`, not `#if NET8_0_OR_GREATER`, for Stream shape.**
  netstandard2.1 *does* have the span and memory overloads of `Stream` and
  `DisposeAsync`; only netstandard2.0 lacks them. Gating those on the wrong
  symbol silently turns the overrides into hiding members on netstandard2.1, so
  a `Stream`-typed caller would reach the base implementation instead of ours.
  The compiler warns (CS0114) — do not suppress it.
- **netstandard2.0 calls other streams through `StreamCompat`.** The library
  hands arbitrary caller-supplied streams a `Span`/`Memory`, which that target's
  `Stream` cannot take, so extension methods rent a pooled array and use the
  `byte[]` overloads. This costs a copy, and it is why the netstandard2.0 build
  is the slowest of the five.

Every `Portable` member is `AggressiveInlining` and picks the same framework
overload the code used before, so the .NET 8+ targets keep their original code:
apart from preprocessor directives, the only change to the hot files is
`Array.Fill` becoming `Portable.Fill`, which inlines back to `Array.Fill` there.

`LzmaNet.Tests.Portable20` and `LzmaNet.Tests.Portable21` compile the whole test
suite again against each netstandard asset — `SetTargetFramework` on their
project references is what forces that, and `PortableAssetTests` asserts the
exact framework name it loaded, so dropping the setting or pointing both at one
asset fails. Compiling a portable target proves nothing on its own; only those
runs exercise the shims.

One test, `DecompressStream_ReadAsync_UsesUnderlyingAsyncIo`, is excluded from
the netstandard2.0 run. That build cannot see `Stream.ReadAsync(Memory<byte>, …)`
and calls the `byte[]` overload, whose .NET default ends in a synchronous read —
visible only because the test host runs that asset on .NET 10. Real consumers of
it are on .NET Framework, where the array overload is itself the async path.

`LzmaNet.Tests.NetFx` runs a targeted suite on .NET Framework 4.7.2, which is
what the netstandard2.0 asset actually ships to — the Portable20 project runs
that same asset on .NET 10, a different BCL. It is **not** in the solution,
because net472 cannot run on the Linux and macOS CI jobs; the
`test (.NET Framework)` workflow job invokes it, and locally:

```shell
dotnet test LzmaNet.Tests.NetFx/LzmaNet.Tests.NetFx.csproj -c Release
```

It is a focused suite rather than the shared sources: those are written against
`Array.Fill`, `Array.MaxLength`, span `Stream.Write` and `Process.WaitForExitAsync`,
so compiling them for net472 would need a second compat layer running through
shared test code. Instead each test drives one shim through the public API.
Nothing needs installing — Windows ships the 4.8 runtime, and
`Microsoft.NETFramework.ReferenceAssemblies` supplies the reference assemblies,
so the project builds even where no targeting pack exists. TUnit needs one
shim of its own (`ModuleInitializerAttribute`, in `Shims.cs`).

Known limit, measured rather than assumed: breaking `BitOperations.TrailingZeroCount`
so the error survives the caller's `>> 3` fails two of these tests, but returning
half from `RoundUpToPowerOf2` does not fail any — preset dictionary sizes are
already powers of two, so that member is close to inert. Do not read a green run
as proof that every shim is exercised.

### Adding a new target

Roslyn stops reporting method-body errors while declaration-level errors are
outstanding, so the first build of a new target under-reports what is missing.
Fix the missing types and bad overrides, build again, and expect a second wave.

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
- Public types: `XzCompressor`, `XzCompressStream`, `XzDecompressStream`, `XzSeekableStream`, `XzCompressOptions`, `XzDecompressOptions`, `XzCheckType`, `XzFilterType`, `LzmaAloneCompressStream`, `LzmaAloneDecompressStream`, `LzmaException`, `LzmaDataErrorException`, `LzmaFormatException`, `LzmaMemoryLimitException`
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
