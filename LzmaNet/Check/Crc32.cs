// SPDX-License-Identifier: 0BSD

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LzmaNet.Check;

/// <summary>
/// CRC32 using the polynomial from the IEEE 802.3 standard (0xEDB88320 reflected).
/// Used by XZ stream headers, footers, block headers, and LZMA_CHECK_CRC32.
/// Implemented with slicing-by-8 for high throughput on bulk data.
/// </summary>
internal static class Crc32
{
    // 8 tables of 256 entries, flattened: Table[k * 256 + v] is the CRC of
    // byte v followed by k zero bytes. Table[0..256) is the classic table.
    private static readonly uint[] Table = CreateTable();

    private static uint[] CreateTable()
    {
        var table = new uint[8 * 256];
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

        for (int k = 1; k < 8; k++)
        {
            for (int i = 0; i < 256; i++)
            {
                uint prev = table[(k - 1) * 256 + i];
                table[k * 256 + i] = (prev >> 8) ^ table[(byte)prev];
            }
        }
        return table;
    }

    /// <summary>
    /// Computes CRC32 over the given data, continuing from a previous CRC value.
    /// </summary>
    /// <param name="data">The input data.</param>
    /// <param name="crc">Previous CRC value (0 for initial calculation).</param>
    /// <returns>Updated CRC32 value.</returns>
    public static uint Compute(ReadOnlySpan<byte> data, uint crc = 0)
    {
        crc = ~crc;
        ref uint t = ref MemoryMarshal.GetArrayDataReference(Table);
        int i = 0;

        // Slicing-by-8: process 8 input bytes per iteration.
        int blockEnd = data.Length - 7;
        while (i < blockEnd)
        {
            uint lo = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i)) ^ crc;
            uint hi = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i + 4));
            crc = Unsafe.Add(ref t, 7 * 256 + (int)(lo & 0xFF))
                ^ Unsafe.Add(ref t, 6 * 256 + (int)((lo >> 8) & 0xFF))
                ^ Unsafe.Add(ref t, 5 * 256 + (int)((lo >> 16) & 0xFF))
                ^ Unsafe.Add(ref t, 4 * 256 + (int)(lo >> 24))
                ^ Unsafe.Add(ref t, 3 * 256 + (int)(hi & 0xFF))
                ^ Unsafe.Add(ref t, 2 * 256 + (int)((hi >> 8) & 0xFF))
                ^ Unsafe.Add(ref t, 1 * 256 + (int)((hi >> 16) & 0xFF))
                ^ Unsafe.Add(ref t, (int)(hi >> 24));
            i += 8;
        }

        for (; i < data.Length; i++)
            crc = Unsafe.Add(ref t, (int)(byte)(crc ^ data[i])) ^ (crc >> 8);

        return ~crc;
    }

    /// <summary>
    /// Computes CRC32 and writes it as 4 little-endian bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteLE(ReadOnlySpan<byte> data, Span<byte> output)
    {
        uint crc = Compute(data);
        BinaryPrimitives.WriteUInt32LittleEndian(output, crc);
    }

    /// <summary>
    /// Verifies CRC32 stored as 4 little-endian bytes after the data.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> expected)
    {
        uint computed = Compute(data);
        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(expected);
        return computed == stored;
    }
}
