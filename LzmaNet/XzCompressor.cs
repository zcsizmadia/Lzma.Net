// SPDX-License-Identifier: 0BSD

using System.Buffers;

using LzmaNet.Lzma;
using LzmaNet.Lzma2;
using LzmaNet.Xz;

namespace LzmaNet;

/// <summary>
/// Provides static methods for one-shot XZ compression and decompression
/// using <see cref="ReadOnlySpan{T}"/> and <see cref="Span{T}"/> for zero-copy operations.
/// </summary>
public static class XzCompressor
{
    /// <summary>
    /// Compresses data into XZ format using the specified options.
    /// </summary>
    /// <param name="data">The uncompressed data.</param>
    /// <param name="options">Compression options. When <c>null</c>, uses default settings (preset 6, CRC64, single-threaded).</param>
    /// <returns>A byte array containing the XZ compressed data.</returns>
    public static byte[] Compress(ReadOnlySpan<byte> data, XzCompressOptions? options = null)
    {
        using var output = new MemoryStream();
        using (var xz = new XzCompressStream(output, options, leaveOpen: true))
        {
            xz.Write(data);
        }
        return output.ToArray();
    }

    /// <summary>
    /// Asynchronously compresses data into XZ format using the specified options.
    /// </summary>
    /// <param name="data">The uncompressed data.</param>
    /// <param name="options">Compression options. When <c>null</c>, uses default settings (preset 6, CRC64, single-threaded).</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task containing a byte array of the XZ compressed data.</returns>
    public static async Task<byte[]> CompressAsync(ReadOnlyMemory<byte> data, XzCompressOptions? options = null, CancellationToken cancellationToken = default)
    {
        var output = new MemoryStream();
        var xz = new XzCompressStream(output, options, leaveOpen: true);
        await using (xz.ConfigureAwait(false))
        {
            await xz.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    /// <summary>
    /// Decompresses XZ formatted data and returns the uncompressed bytes.
    /// </summary>
    /// <param name="compressedData">The XZ compressed data.</param>
    /// <returns>A byte array containing the decompressed data.</returns>
    /// <exception cref="LzmaFormatException">The data is not in valid XZ format.</exception>
    /// <exception cref="LzmaDataErrorException">The compressed data is corrupt.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> compressedData)
    {
        return Decompress(compressedData, threads: 1);
    }

    /// <summary>
    /// Decompresses XZ formatted data using multiple threads and returns the uncompressed bytes.
    /// Parallelism applies per XZ block, so it only helps for multi-block streams
    /// (e.g., produced with <see cref="XzCompressOptions.Threads"/> &gt; 1 or a small
    /// <see cref="XzCompressOptions.BlockSize"/>). Single-block streams decode serially.
    /// </summary>
    /// <param name="compressedData">The XZ compressed data.</param>
    /// <param name="threads">Number of decoder threads: 0 = all CPUs, 1 = single-threaded.</param>
    /// <returns>A byte array containing the decompressed data.</returns>
    /// <exception cref="LzmaFormatException">The data is not in valid XZ format.</exception>
    /// <exception cref="LzmaDataErrorException">The compressed data is corrupt.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> compressedData, int threads)
    {
        return Decompress(compressedData, new XzDecompressOptions { Threads = threads });
    }

    /// <summary>
    /// Decompresses XZ formatted data using the specified options
    /// (thread count and output size limit) and returns the uncompressed bytes.
    /// </summary>
    /// <param name="compressedData">The XZ compressed data.</param>
    /// <param name="options">Decompression options. When <c>null</c>, uses defaults.</param>
    /// <returns>A byte array containing the decompressed data.</returns>
    /// <exception cref="LzmaFormatException">The data is not in valid XZ format.</exception>
    /// <exception cref="LzmaDataErrorException">The compressed data is corrupt.</exception>
    /// <exception cref="LzmaMemoryLimitException">Output would exceed
    /// <see cref="XzDecompressOptions.MaxOutputSize"/>.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> compressedData, XzDecompressOptions? options)
    {
        byte[] inputArray = compressedData.ToArray();
        // publiclyVisible: true keeps TryGetBuffer working so block decoding can
        // slice compressed data directly from this buffer instead of copying it.
        using var input = new MemoryStream(inputArray, 0, inputArray.Length,
            writable: false, publiclyVisible: true);
        using var xz = new XzDecompressStream(input, options, leaveOpen: true);
        using var output = new MemoryStream();
        xz.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    /// Asynchronously decompresses XZ formatted data and returns the uncompressed bytes.
    /// </summary>
    /// <param name="compressedData">The XZ compressed data.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task containing a byte array of the decompressed data.</returns>
    /// <exception cref="LzmaFormatException">The data is not in valid XZ format.</exception>
    /// <exception cref="LzmaDataErrorException">The compressed data is corrupt.</exception>
    public static async Task<byte[]> DecompressAsync(ReadOnlyMemory<byte> compressedData, CancellationToken cancellationToken = default)
    {
        byte[] inputArray = compressedData.ToArray();
        var input = new MemoryStream(inputArray, 0, inputArray.Length,
            writable: false, publiclyVisible: true);
        await using var xz = new XzDecompressStream(input, leaveOpen: true);
        var output = new MemoryStream();
        await xz.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    /// <summary>
    /// Decompresses XZ formatted data into the provided buffer.
    /// </summary>
    /// <param name="compressedData">The XZ compressed data.</param>
    /// <param name="output">The buffer to receive decompressed data.</param>
    /// <returns>The number of decompressed bytes written to <paramref name="output"/>.</returns>
    /// <exception cref="LzmaFormatException">The data is not in valid XZ format.</exception>
    /// <exception cref="LzmaDataErrorException">The compressed data is corrupt.</exception>
    /// <exception cref="ArgumentException"><paramref name="output"/> is too small for the decompressed data.</exception>
    public static int Decompress(ReadOnlySpan<byte> compressedData, Span<byte> output)
    {
        byte[] decompressed = Decompress(compressedData);
        if (decompressed.Length > output.Length)
            throw new ArgumentException("Output buffer is too small for the decompressed data.", nameof(output));
        decompressed.AsSpan().CopyTo(output);
        return decompressed.Length;
    }

    /// <summary>
    /// Calculates the maximum compressed size for the given uncompressed size.
    /// This can be used to pre-allocate output buffers.
    /// </summary>
    /// <param name="uncompressedSize">Size of the uncompressed data.</param>
    /// <returns>Maximum possible compressed size in XZ format.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="uncompressedSize"/> is negative or too large to represent the result.
    /// </exception>
    public static long MaxCompressedSize(long uncompressedSize)
    {
        if (uncompressedSize < 0)
            throw new ArgumentOutOfRangeException(nameof(uncompressedSize),
                "Uncompressed size cannot be negative.");

        // Overhead: stream header (12) + block headers (~20) + index (~20) + footer (12) + expansion
        // LZMA worst case is about input + input/64 + 16
        long overhead = uncompressedSize / 64 + 128;
        if (uncompressedSize > long.MaxValue - overhead)
            throw new ArgumentOutOfRangeException(nameof(uncompressedSize),
                "Uncompressed size is too large.");
        return uncompressedSize + overhead;
    }
}
