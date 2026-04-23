// SPDX-License-Identifier: 0BSD

using System.Buffers.Binary;
using LzmaNet.Check;

namespace LzmaNet.Xz;

/// <summary>
/// Reads and writes XZ stream headers and footers.
/// </summary>
internal static class XzHeader
{
    /// <summary>
    /// Reads and validates an XZ stream header.
    /// </summary>
    /// <param name="header">12-byte stream header.</param>
    /// <returns>The check type (0-15).</returns>
    public static int ReadStreamHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < XzConstants.StreamHeaderSize)
            throw new LzmaFormatException("XZ stream header too short.");

        // Verify magic bytes
        if (!header[..6].SequenceEqual(XzConstants.HeaderMagic))
            throw new LzmaFormatException("Invalid XZ magic bytes.");

        // Stream flags: bytes 6-7
        byte flag0 = header[6];
        byte flag1 = header[7];

        if (flag0 != 0x00)
            throw new LzmaFormatException("Unsupported XZ stream flags.");

        int checkType = flag1 & 0x0F;

        // Verify CRC32 of stream flags (bytes 6-7)
        if (!Crc32.Verify(header.Slice(6, 2), header.Slice(8, 4)))
            throw new LzmaDataErrorException("XZ stream header CRC32 mismatch.");

        return checkType;
    }

    /// <summary>
    /// Writes an XZ stream header to the output.
    /// </summary>
    /// <param name="output">Output span (must be at least 12 bytes).</param>
    /// <param name="checkType">Check type to use.</param>
    public static void WriteStreamHeader(Span<byte> output, int checkType)
    {
        // Magic bytes
        XzConstants.HeaderMagic.CopyTo(output);

        // Stream flags
        output[6] = 0x00;
        output[7] = (byte)(checkType & 0x0F);

        // CRC32 of stream flags
        Crc32.WriteLE(output.Slice(6, 2), output.Slice(8, 4));
    }

    /// <summary>
    /// Reads and validates an XZ stream footer.
    /// </summary>
    /// <param name="footer">12-byte stream footer.</param>
    /// <param name="expectedCheckType">Check type from stream header for verification.</param>
    /// <returns>Backward size (size of the Index field in bytes).</returns>
    public static long ReadStreamFooter(ReadOnlySpan<byte> footer, int expectedCheckType)
    {
        if (footer.Length < XzConstants.StreamFooterSize)
            throw new LzmaFormatException("XZ stream footer too short.");

        // Verify footer magic bytes (last 2 bytes)
        if (footer[10] != XzConstants.FooterMagic[0] || footer[11] != XzConstants.FooterMagic[1])
            throw new LzmaFormatException("Invalid XZ stream footer magic bytes.");

        // Verify CRC32 of backward size + stream flags (bytes 4-9)
        if (!Crc32.Verify(footer.Slice(4, 6), footer[..4]))
            throw new LzmaDataErrorException("XZ stream footer CRC32 mismatch.");

        // Backward size (bytes 4-7): stored as (real_size / 4 - 1)
        uint backwardSizeField = BinaryPrimitives.ReadUInt32LittleEndian(footer.Slice(4, 4));
        long backwardSize = ((long)backwardSizeField + 1) * 4;

        // Stream flags (bytes 8-9) — must match header
        if (footer[8] != 0x00)
            throw new LzmaFormatException("Unsupported XZ stream flags in footer.");

        int footerCheckType = footer[9] & 0x0F;
        if (footerCheckType != expectedCheckType)
            throw new LzmaDataErrorException("XZ stream header/footer check type mismatch.");

        return backwardSize;
    }

    /// <summary>
    /// Writes an XZ stream footer to the output.
    /// </summary>
    /// <param name="output">Output span (must be at least 12 bytes).</param>
    /// <param name="checkType">Check type.</param>
    /// <param name="indexSize">Size of the Index field in bytes (must be multiple of 4).</param>
    public static void WriteStreamFooter(Span<byte> output, int checkType, long indexSize)
    {
        // Backward size
        uint backwardSizeField = (uint)(indexSize / 4 - 1);
        BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(4, 4), backwardSizeField);

        // Stream flags
        output[8] = 0x00;
        output[9] = (byte)(checkType & 0x0F);

        // Footer magic
        output[10] = XzConstants.FooterMagic[0];
        output[11] = XzConstants.FooterMagic[1];

        // CRC32 of backward size + stream flags (bytes 4-9)
        Crc32.WriteLE(output.Slice(4, 6), output[..4]);
    }
}
