# LzmaNet API Reference

LzmaNet is a **native C# implementation** of XZ/LZMA2 compression with no native dependencies. All types are in the `LzmaNet` namespace.

---

## XzCompressor

```csharp
public static class XzCompressor
```

Static helper for one-shot compression and decompression of byte buffers. Internally wraps `XzCompressStream` / `XzDecompressStream`.

### Methods

#### Compress

```csharp
public static byte[] Compress(ReadOnlySpan<byte> data, XzCompressOptions? options = null)
```

Compresses `data` into XZ format and returns the compressed bytes.

| Parameter | Type | Description |
|-----------|------|-------------|
| `data` | `ReadOnlySpan<byte>` | The uncompressed data. |
| `options` | `XzCompressOptions?` | Compression options. `null` uses defaults (preset 6, CRC64, single-threaded). |

**Returns:** `byte[]` — the XZ compressed output.

**Example:**

```csharp
byte[] compressed = XzCompressor.Compress(data);
byte[] compressed = XzCompressor.Compress(data, new XzCompressOptions { Preset = 9 });
```

---

#### CompressAsync

```csharp
public static Task<byte[]> CompressAsync(
    ReadOnlyMemory<byte> data,
    XzCompressOptions? options = null,
    CancellationToken cancellationToken = default)
```

Asynchronously compresses `data` into XZ format. Uses `ReadOnlyMemory<byte>` instead of `ReadOnlySpan<byte>` since spans cannot cross `await` boundaries.

| Parameter | Type | Description |
|-----------|------|-------------|
| `data` | `ReadOnlyMemory<byte>` | The uncompressed data. |
| `options` | `XzCompressOptions?` | Compression options. `null` uses defaults. |
| `cancellationToken` | `CancellationToken` | Token to cancel the operation. |

**Returns:** `Task<byte[]>` — the XZ compressed output.

**Example:**

```csharp
byte[] compressed = await XzCompressor.CompressAsync(data);
byte[] compressed = await XzCompressor.CompressAsync(data, new XzCompressOptions { Preset = 9 });
```

---

#### Decompress (to byte array)

```csharp
public static byte[] Decompress(ReadOnlySpan<byte> compressedData)
public static byte[] Decompress(ReadOnlySpan<byte> compressedData, int threads)
public static byte[] Decompress(ReadOnlySpan<byte> compressedData, XzDecompressOptions? options)
```

Decompresses XZ data and returns the result as a new byte array.

The `threads` overload decodes XZ blocks in parallel. Parallelism applies per XZ
block, so it only helps for multi-block streams (e.g., produced with
`XzCompressOptions.Threads > 1` or a small `XzCompressOptions.BlockSize`);
single-block streams decode serially.

| Parameter | Type | Description |
|-----------|------|-------------|
| `compressedData` | `ReadOnlySpan<byte>` | The XZ compressed data. |
| `threads` | `int` | Decoder threads: `0` = all CPUs, `1` = single-threaded, `N` = up to N threads. |

**Returns:** `byte[]` — the decompressed data.

**Exceptions:**

| Exception | Condition |
|-----------|-----------|
| `LzmaFormatException` | Data is not in valid XZ format. |
| `LzmaDataErrorException` | Compressed data is corrupt or integrity check failed. |

**Example:**

```csharp
byte[] restored = XzCompressor.Decompress(compressed);
byte[] restored = XzCompressor.Decompress(compressed, threads: 0); // parallel block decode
```

---

#### DecompressAsync

```csharp
public static Task<byte[]> DecompressAsync(
    ReadOnlyMemory<byte> compressedData,
    CancellationToken cancellationToken = default)
```

Asynchronously decompresses XZ data and returns the result as a new byte array.

| Parameter | Type | Description |
|-----------|------|-------------|
| `compressedData` | `ReadOnlyMemory<byte>` | The XZ compressed data. |
| `cancellationToken` | `CancellationToken` | Token to cancel the operation. |

**Returns:** `Task<byte[]>` — the decompressed data.

**Exceptions:**

| Exception | Condition |
|-----------|-----------|
| `LzmaFormatException` | Data is not in valid XZ format. |
| `LzmaDataErrorException` | Compressed data is corrupt or integrity check failed. |

---

#### Decompress (into buffer)

```csharp
public static int Decompress(ReadOnlySpan<byte> compressedData, Span<byte> output)
```

Decompresses XZ data into a caller-provided buffer.

| Parameter | Type | Description |
|-----------|------|-------------|
| `compressedData` | `ReadOnlySpan<byte>` | The XZ compressed data. |
| `output` | `Span<byte>` | Destination buffer. |

**Returns:** `int` — number of decompressed bytes written.

**Exceptions:**

| Exception | Condition |
|-----------|-----------|
| `LzmaFormatException` | Data is not in valid XZ format. |
| `LzmaDataErrorException` | Compressed data is corrupt. |
| `ArgumentException` | `output` is too small for the decompressed data. |

---

#### MaxCompressedSize

```csharp
public static long MaxCompressedSize(long uncompressedSize)
```

Returns the worst-case compressed size for the given input size. Useful for pre-allocating buffers.

| Parameter | Type | Description |
|-----------|------|-------------|
| `uncompressedSize` | `long` | Size of the uncompressed data in bytes. |

**Returns:** `long` — maximum possible compressed size including XZ overhead.

---

## XzCompressStream

```csharp
public sealed class XzCompressStream : Stream
```

A **write-only** stream that compresses data into XZ format on the fly. Written bytes are LZMA2-compressed and emitted to the underlying output stream. Disposing the stream finalizes the XZ container (writes index and footer).

This is a native C# `Stream` implementation — no native code is invoked.

### Constructor

```csharp
public XzCompressStream(Stream stream, XzCompressOptions? options = null, bool leaveOpen = false)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `stream` | `Stream` | The output stream to write compressed data to. |
| `options` | `XzCompressOptions?` | Compression options. `null` uses defaults. |
| `leaveOpen` | `bool` | If `true`, the underlying stream is not closed on dispose. |

**Exceptions:**

| Exception | Condition |
|-----------|-----------|
| `ArgumentNullException` | `stream` is `null`. |
| `ArgumentOutOfRangeException` | Options contain invalid values. |

**Example:**

```csharp
using var output = File.Create("data.xz");
using (var xz = new XzCompressStream(output))
{
    xz.Write(data);
}
```

**Async example:**

```csharp
using var output = File.Create("data.xz");
await using (var xz = new XzCompressStream(output))
{
    await xz.WriteAsync(data);
}
```

**Multi-threaded compression:**

```csharp
var opts = new XzCompressOptions { Preset = 6, Threads = 4 };
using var output = File.Create("data.xz");
using (var xz = new XzCompressStream(output, opts))
{
    input.CopyTo(xz);
}
```

### Async Support

`XzCompressStream` implements `IAsyncDisposable` and supports the following async methods:

- `WriteAsync(ReadOnlyMemory<byte>, CancellationToken)` — writes and compresses data asynchronously
- `WriteAsync(byte[], int, int, CancellationToken)` — array-based overload
- `FlushAsync(CancellationToken)` — flushes pending blocks asynchronously
- `DisposeAsync()` — finalizes the XZ stream and disposes resources asynchronously

Use `await using` instead of `using` for async disposal:

```csharp
await using var xz = new XzCompressStream(output);
```

### Stream Properties

| Property | Value |
|----------|-------|
| `CanRead` | `false` |
| `CanWrite` | `true` |
| `CanSeek` | `false` |

---

## XzDecompressStream

```csharp
public sealed class XzDecompressStream : Stream
```

A **read-only** stream that decompresses XZ data on the fly. Reads from the underlying stream, decompresses one XZ block at a time, and returns decompressed bytes to the caller.

This is a native C# `Stream` implementation — no native code is invoked.

### Constructors

```csharp
public XzDecompressStream(Stream stream, bool leaveOpen = false)
public XzDecompressStream(Stream stream, int threads, bool leaveOpen = false)
public XzDecompressStream(Stream stream, XzDecompressOptions? options, bool leaveOpen = false)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `stream` | `Stream` | The stream containing XZ compressed data. |
| `threads` | `int` | Decoder threads: `0` = all CPUs, `1` = single-threaded (default), `N` = up to N threads. |
| `leaveOpen` | `bool` | If `true`, the underlying stream is not closed on dispose. |

When `threads > 1`, XZ blocks are read sequentially but decoded in parallel
(synchronous `Read` path). Parallelism applies per XZ block, so multi-block
streams are required to benefit; up to `threads` decoded blocks are buffered
in memory at a time, so peak memory use grows with the thread count and block
size.

**Exceptions:**

| Exception | Condition |
|-----------|-----------|
| `ArgumentNullException` | `stream` is `null`. |
| `ArgumentOutOfRangeException` | `threads` is negative. |

**Example:**

```csharp
using var input = File.OpenRead("data.xz");
using var xz = new XzDecompressStream(input);
using var output = File.Create("data.bin");
xz.CopyTo(output);
```

**Async example:**

```csharp
using var input = File.OpenRead("data.xz");
using var xz = new XzDecompressStream(input);
using var output = File.Create("data.bin");
await xz.CopyToAsync(output);
```

### Async Support

`XzDecompressStream` supports the following async methods. When `Threads > 1`,
the async read path decodes blocks in parallel just like the sync path (raw
blocks are read with async I/O; decoding runs on worker threads).

- `ReadAsync(Memory<byte>, CancellationToken)` — reads decompressed data asynchronously
- `ReadAsync(byte[], int, int, CancellationToken)` — array-based overload

### Concatenated Streams

`XzDecompressStream` supports reading **concatenated XZ streams** — multiple XZ streams appended back-to-back, optionally separated by null-byte padding (multiples of 4 bytes). This is compatible with `xz --keep` and `cat file1.xz file2.xz > combined.xz`.

### Stream Properties

| Property | Value |
|----------|-------|
| `CanRead` | `true` |
| `CanWrite` | `false` |
| `CanSeek` | `false` |

---

## XzCompressOptions

```csharp
public sealed class XzCompressOptions
```

Configuration object for XZ compression. All properties have sensible defaults matching `xz -6`.

### Properties

#### Preset

```csharp
public int Preset { get; set; } = 6;
```

Compression level from 0 (fastest, largest) to 9 (slowest, smallest). Controls dictionary size, match-finder choice, and parsing strategy: presets 0–2 are greedy, 3–6 use lazy matching (hash-chain finder), and 7–9 use the BT4 binary-tree finder with price-based optimal parsing for near-xz compression ratios at substantially lower speed.

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

---

#### Extreme

```csharp
public bool Extreme { get; set; } = false;
```

When `true`, the encoder spends significantly more CPU time searching for better matches, improving the compression ratio without increasing memory usage. Equivalent to `xz --extreme` / `xz -e`.

---

#### Threads

```csharp
public int Threads { get; set; } = 1;
```

Number of threads for parallel block compression:

- `0` — use all available CPUs (`Environment.ProcessorCount`)
- `1` — single-threaded (default)
- `N` — use exactly N threads

When using multiple threads, the input is split into blocks that are compressed in parallel. The output is a standard XZ stream fully compatible with single-threaded decoders.

---

#### CheckType

```csharp
public XzCheckType CheckType { get; set; } = XzCheckType.Crc64;
```

The integrity check stored in the XZ stream. See [`XzCheckType`](#xzchecktype).

---

#### DictionarySize

```csharp
public int? DictionarySize { get; set; } = null;
```

Override the preset's dictionary size (in bytes). Must be at least 4096 (4 KB). When `null`, determined automatically by `Preset`. Larger dictionaries improve compression of data with long-range repetitions but increase memory usage during both compression and decompression.

---

#### BlockSize

```csharp
public int? BlockSize { get; set; } = null;
```

XZ block size in bytes. Must be at least 4096 (4 KB). When `null`, defaults to `max(dictionarySize × 2, 1 MB)`. Smaller blocks reduce peak memory and enable parallel decompression and fine-grained random access (`XzSeekableStream`); larger blocks can improve compression ratio.

---

#### Filter

```csharp
public XzFilterType Filter { get; set; } = XzFilterType.None;
```

Optional BCJ/Delta pre-compression filter applied before LZMA2 (see [XzFilterType](#xzfiltertype)). BCJ filters substantially improve the compression ratio on machine code; use the variant matching the target architecture.

---

#### DeltaDistance

```csharp
public int DeltaDistance { get; set; } = 1;
```

Byte distance (1–256) for the `Delta` filter. Ignored for other filters.

---

#### Progress

```csharp
public IProgress<long>? Progress { get; set; }
```

Optional progress sink. Reports the cumulative number of uncompressed bytes
compressed, once per completed XZ block, from the writing thread.
(`XzDecompressOptions` has the equivalent property for decompression.)

---

### Static Properties

#### Default

```csharp
public static XzCompressOptions Default { get; }
```

Returns a new instance with all defaults — equivalent to `xz -6`.

---

### Methods

#### Validate

```csharp
public void Validate()
```

Validates all option values. Throws `ArgumentOutOfRangeException` if any are invalid:

- `Preset` must be 0–9
- `Threads` must be ≥ 0
- `DictionarySize` (if set) must be ≥ 4096
- `BlockSize` (if set) must be ≥ 4096
- `Filter` must be a defined `XzFilterType` value
- `DeltaDistance` must be 1–256 when `Filter` is `Delta`

---

## XzSeekableStream

```csharp
public sealed class XzSeekableStream : Stream
```

A **read-only, seekable** stream providing random access to XZ compressed data.
The XZ index is parsed once from the end of the file; a `Seek`/`Position` change
followed by `Read` decodes only the block containing the requested position (the
most recent block is cached). Random-access granularity is the XZ block size, so
files produced with a small `XzCompressOptions.BlockSize` seek most efficiently.
Concatenated streams (with padding) are supported. Not thread-safe.

### Constructor

```csharp
public XzSeekableStream(Stream stream, XzDecompressOptions? options = null, bool leaveOpen = false)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `stream` | `Stream` | A **readable, seekable** stream containing XZ data. |
| `options` | `XzDecompressOptions?` | `MaxOutputSize` applies per block. `null` = defaults. |
| `leaveOpen` | `bool` | If `true`, the underlying stream is not closed on dispose. |

**Exceptions:** `ArgumentNullException`, `ArgumentException` (not readable/seekable),
`LzmaFormatException`, `LzmaDataErrorException` (corrupt index/footer).

### Members

- `long Length` — total uncompressed size (from the index; no decoding needed)
- `long Position` / `Seek(long, SeekOrigin)` — position in **uncompressed** coordinates
- `int BlockCount` — number of XZ blocks (the granularity of random access)
- `Read(Span<byte>)` / `Read(byte[], int, int)` — decodes only the necessary block(s)

**Example:**

```csharp
using var xz = new XzSeekableStream(File.OpenRead("large.xz"));
xz.Position = 100_000_000;
byte[] slice = new byte[4096];
xz.ReadExactly(slice);
```

---

## XzDecompressOptions

```csharp
public sealed class XzDecompressOptions
```

Configuration for XZ decompression.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Threads` | `int` | `1` | `0` = all CPUs, `1` = single-threaded, `N` = up to N threads. Parallelism applies per XZ block. |
| `MaxOutputSize` | `long` | `long.MaxValue` | Maximum total decompressed bytes before `LzmaMemoryLimitException` is thrown. Enforced **before** any allocation — protects against decompression bombs. For `XzSeekableStream`, applies per block. |
| `Progress` | `IProgress<long>?` | `null` | Reports cumulative decompressed bytes, once per decoded block (or batch in parallel mode). |

Used by `XzCompressor.Decompress(data, options)`, `new XzDecompressStream(stream, options, leaveOpen)`, and `XzSeekableStream`.

---

## LzmaAloneCompressStream / LzmaAloneDecompressStream

```csharp
public sealed class LzmaAloneCompressStream : Stream    // write-only
public sealed class LzmaAloneDecompressStream : Stream  // read-only
```

Support for the **legacy `.lzma` ("LZMA-alone") format** — a 13-byte header
(properties, dictionary size, uncompressed size) followed by a single raw LZMA
stream. Compatible with 7-Zip and `xz --format=lzma`, including unknown-size
streams terminated by the end marker. Prefer the XZ format for new applications:
`.lzma` has no blocks, no integrity check, and no parallelism, so both streams
process the whole payload in memory.

```csharp
public LzmaAloneCompressStream(Stream stream, int preset = 6, bool leaveOpen = false)
public LzmaAloneDecompressStream(Stream stream, XzDecompressOptions? options = null, bool leaveOpen = false)
```

`XzDecompressOptions.MaxOutputSize` is honored (recommended for untrusted
input); `Threads` is ignored (the format has no blocks).

**Example:**

```csharp
using var input = File.OpenRead("archive.lzma");
using var dec = new LzmaAloneDecompressStream(input,
    new XzDecompressOptions { MaxOutputSize = 1L << 30 });
using var output = File.Create("archive.bin");
dec.CopyTo(output);
```

---

## XzFilterType

```csharp
public enum XzFilterType
```

Optional pre-compression filter applied before LZMA2 (set via `XzCompressOptions.Filter`).
BCJ filters convert relative branch addresses in machine code to absolute ones,
substantially improving the ratio on executables. All filter types are supported
for both encoding and decoding.

| Member | Filter |
|--------|--------|
| `None` | No filter (default). |
| `Delta` | Byte-wise delta with configurable distance (`XzCompressOptions.DeltaDistance`, 1–256). |
| `X86` | BCJ for x86/x64 machine code. |
| `PowerPc` | BCJ for PowerPC (big endian). |
| `Ia64` | BCJ for IA-64 (Itanium). |
| `Arm` | BCJ for ARM (32-bit). |
| `ArmThumb` | BCJ for ARM-Thumb. |
| `Sparc` | BCJ for SPARC. |
| `Arm64` | BCJ for ARM64 (AArch64). |
| `RiscV` | BCJ for RISC-V. |

---

## XzCheckType

```csharp
public enum XzCheckType
```

Integrity check type written into the XZ stream.

| Member | Value | Description |
|--------|-------|-------------|
| `None` | `0` | No integrity check. |
| `Crc32` | `1` | CRC32 (4 bytes). Fast but less robust. |
| `Crc64` | `4` | CRC64 (8 bytes). Good balance of speed and integrity. **Default.** |
| `Sha256` | `10` | SHA-256 (32 bytes). Strongest integrity check. |

---

## Exception Types

All exceptions inherit from `LzmaException`, which inherits from `System.Exception`.

### LzmaException

```csharp
public class LzmaException : Exception
```

Base exception for all LZMA/XZ errors.

### LzmaDataErrorException

```csharp
public class LzmaDataErrorException : LzmaException
```

Thrown when compressed data is corrupt or an integrity check fails.

### LzmaFormatException

```csharp
public class LzmaFormatException : LzmaException
```

Thrown when data is not in a recognized XZ/LZMA format (e.g., bad magic bytes, unsupported filter).

### LzmaMemoryLimitException

```csharp
public class LzmaMemoryLimitException : LzmaException
```

Thrown when decompression would exceed `XzDecompressOptions.MaxOutputSize`.
Typically indicates a decompression bomb or a corrupt size field; the limit is
checked before output buffers are allocated.
