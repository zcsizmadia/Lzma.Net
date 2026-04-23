// SPDX-License-Identifier: 0BSD

namespace LzmaNet.Filters;

/// <summary>
/// Interface for XZ simple (BCJ) and Delta filters that transform
/// byte data in-place to improve LZMA2 compression of executable code.
/// </summary>
internal interface IBcjFilter
{
    /// <summary>
    /// Applies the forward (encoding) transformation in-place.
    /// </summary>
    /// <param name="buffer">The buffer to transform.</param>
    /// <param name="startPos">The stream position of the first byte in the buffer.</param>
    /// <returns>The number of bytes that were processed (trailing bytes may be left unprocessed).</returns>
    int Encode(Span<byte> buffer, uint startPos);

    /// <summary>
    /// Applies the reverse (decoding) transformation in-place.
    /// </summary>
    /// <param name="buffer">The buffer to transform.</param>
    /// <param name="startPos">The stream position of the first byte in the buffer.</param>
    /// <returns>The number of bytes that were processed.</returns>
    int Decode(Span<byte> buffer, uint startPos);
}
