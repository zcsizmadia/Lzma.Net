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

#### Decompress (to byte array)

```csharp
public static byte[] Decompress(ReadOnlySpan<byte> compressedData)
```

Decompresses XZ data and returns the result as a new byte array.

| Parameter | Type | Description |
|-----------|------|-------------|
| `compressedData` | `ReadOnlySpan<byte>` | The XZ compressed data. |

**Returns:** `byte[]` — the decompressed data.

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

**Multi-threaded compression:**

```csharp
var opts = new XzCompressOptions { Preset = 6, Threads = 4 };
using var output = File.Create("data.xz");
using (var xz = new XzCompressStream(output, opts))
{
    input.CopyTo(xz);
}
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

### Constructor

```csharp
public XzDecompressStream(Stream stream, bool leaveOpen = false)
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `stream` | `Stream` | The stream containing XZ compressed data. |
| `leaveOpen` | `bool` | If `true`, the underlying stream is not closed on dispose. |

**Exceptions:**

| Exception | Condition |
|-----------|-----------|
| `ArgumentNullException` | `stream` is `null`. |

**Example:**

```csharp
using var input = File.OpenRead("data.xz");
using var xz = new XzDecompressStream(input);
using var output = File.Create("data.bin");
xz.CopyTo(output);
```

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

Compression level from 0 (fastest, largest) to 9 (slowest, smallest). Controls dictionary size and match-finder effort.

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

XZ block size in bytes. Must be at least 4096 (4 KB). When `null`, defaults to `max(dictionarySize × 2, 1 MB)`. Smaller blocks reduce peak memory and allow parallel decompression; larger blocks can improve compression ratio.

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
