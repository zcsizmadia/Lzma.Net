// SPDX-License-Identifier: 0BSD

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LzmaNet.Check;

/// <summary>
/// CRC64 using the polynomial from the ECMA-182 standard (0xC96C5795D7870F42 reflected).
/// Used by LZMA_CHECK_CRC64 in XZ containers.
/// Implemented with slicing-by-8 for high throughput on bulk data.
/// </summary>
internal static class Crc64
{
    // 8 tables of 256 entries, flattened: Table[k * 256 + v] is the CRC of
    // byte v followed by k zero bytes. Table[0..256) is the classic table.
    private static readonly ulong[] Table = CreateTable();

    private static uint[] Crc32Table => Crc32Table_Backing ??= CreateCrc32Table();
    private static uint[]? Crc32Table_Backing;

    private static ulong[] CreateTable()
    {
        const ulong Poly = 0xC96C5795D7870F42UL;
        var table = new ulong[8 * 256];
        for (uint i = 0; i < 256; i++)
        {
            ulong crc = i;
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 1) != 0)
                    crc = (crc >> 1) ^ Poly;
                else
                    crc >>= 1;
            }
            table[i] = crc;
        }

        for (int k = 1; k < 8; k++)
        {
            for (int i = 0; i < 256; i++)
            {
                ulong prev = table[(k - 1) * 256 + i];
                table[k * 256 + i] = (prev >> 8) ^ table[(byte)prev];
            }
        }
        return table;
    }

    private static uint[] CreateCrc32Table()
    {
        // This is the same IEEE CRC32 table used by the hash function in match finders
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
    /// Computes CRC64 over the given data, continuing from a previous CRC value.
    /// </summary>
    /// <param name="data">The input data.</param>
    /// <param name="crc">Previous CRC value (0 for initial calculation).</param>
    /// <returns>Updated CRC64 value.</returns>
    public static ulong Compute(ReadOnlySpan<byte> data, ulong crc = 0)
    {
        crc = ~crc;
        ref ulong t = ref MemoryMarshal.GetArrayDataReference(Table);
        int i = 0;

        // Slicing-by-8: process 8 input bytes per iteration.
        int blockEnd = data.Length - 7;
        while (i < blockEnd)
        {
            ulong x = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i)) ^ crc;
            crc = Unsafe.Add(ref t, 7 * 256 + (int)(x & 0xFF))
                ^ Unsafe.Add(ref t, 6 * 256 + (int)((x >> 8) & 0xFF))
                ^ Unsafe.Add(ref t, 5 * 256 + (int)((x >> 16) & 0xFF))
                ^ Unsafe.Add(ref t, 4 * 256 + (int)((x >> 24) & 0xFF))
                ^ Unsafe.Add(ref t, 3 * 256 + (int)((x >> 32) & 0xFF))
                ^ Unsafe.Add(ref t, 2 * 256 + (int)((x >> 40) & 0xFF))
                ^ Unsafe.Add(ref t, 1 * 256 + (int)((x >> 48) & 0xFF))
                ^ Unsafe.Add(ref t, (int)(x >> 56));
            i += 8;
        }

        for (; i < data.Length; i++)
            crc = Unsafe.Add(ref t, (int)(byte)(crc ^ data[i])) ^ (crc >> 8);

        return ~crc;
    }

    /// <summary>
    /// Computes CRC64 and writes it as 8 little-endian bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteLE(ReadOnlySpan<byte> data, Span<byte> output)
    {
        ulong crc = Compute(data);
        BinaryPrimitives.WriteUInt64LittleEndian(output, crc);
    }

    /// <summary>
    /// Verifies CRC64 stored as 8 little-endian bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> expected)
    {
        ulong computed = Compute(data);
        ulong stored = BinaryPrimitives.ReadUInt64LittleEndian(expected);
        return computed == stored;
    }

    /// <summary>
    /// Gets the CRC32 hash table used by LZ match finders for hashing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint GetCrc32HashValue(byte b) => Crc32Table[b];
}
