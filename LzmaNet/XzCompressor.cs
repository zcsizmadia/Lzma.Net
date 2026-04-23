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
    /// Decompresses XZ formatted data and returns the uncompressed bytes.
    /// </summary>
    /// <param name="compressedData">The XZ compressed data.</param>
    /// <returns>A byte array containing the decompressed data.</returns>
    /// <exception cref="LzmaFormatException">The data is not in valid XZ format.</exception>
    /// <exception cref="LzmaDataErrorException">The compressed data is corrupt.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> compressedData)
    {
        using var input = new MemoryStream(compressedData.ToArray());
        using var xz = new XzDecompressStream(input, leaveOpen: true);
        using var output = new MemoryStream();
        xz.CopyTo(output);
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
    public static long MaxCompressedSize(long uncompressedSize)
    {
        // Overhead: stream header (12) + block headers (~20) + index (~20) + footer (12) + expansion
        // LZMA worst case is about input + input/64 + 16
        return uncompressedSize + uncompressedSize / 64 + 128;
    }
}
