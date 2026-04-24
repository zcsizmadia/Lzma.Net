// SPDX-License-Identifier: 0BSD

using System.Runtime.CompilerServices;

namespace LzmaNet.Check;

/// <summary>
/// CRC32 using the polynomial from the IEEE 802.3 standard (0xEDB88320 reflected).
/// Used by XZ stream headers, footers, block headers, and LZMA_CHECK_CRC32.
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 1) != 0)
                    crc = (crc >> 1) ^ 0xEDB88320u;
                else
                    crc >>= 1;
            }
            table[i] = crc;
        }
        return table;
    }

    /// <summary>
    /// Computes CRC32 over the given data, continuing from a previous CRC value.
    /// </summary>
    /// <param name="data">The input data.</param>
    /// <param name="crc">Previous CRC value (0 for initial calculation).</param>
    /// <returns>Updated CRC32 value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Compute(ReadOnlySpan<byte> data, uint crc = 0)
    {
        crc = ~crc;
        ref uint tableRef = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(Table);
        for (int i = 0; i < data.Length; i++)
        {
            crc = Unsafe.Add(ref tableRef, (int)(byte)(crc ^ data[i])) ^ (crc >> 8);
        }
        return ~crc;
    }

    /// <summary>
    /// Computes CRC32 and writes it as 4 little-endian bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteLE(ReadOnlySpan<byte> data, Span<byte> output)
    {
        uint crc = Compute(data);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(output, crc);
    }

    /// <summary>
    /// Verifies CRC32 stored as 4 little-endian bytes after the data.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> expected)
    {
        uint computed = Compute(data);
        uint stored = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(expected);
        return computed == stored;
    }
}
