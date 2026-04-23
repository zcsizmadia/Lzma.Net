// SPDX-License-Identifier: 0BSD

using System.Runtime.CompilerServices;

namespace LzmaNet.Check;

/// <summary>
/// CRC64 using the polynomial from the ECMA-182 standard (0xC96C5795D7870F42 reflected).
/// Used by LZMA_CHECK_CRC64 in XZ containers.
/// </summary>
internal static class Crc64
{
    private static readonly ulong[] Table = CreateTable();

    private static uint[] Crc32Table => Crc32Table_Backing ??= CreateCrc32Table();
    private static uint[]? Crc32Table_Backing;

    private static ulong[] CreateTable()
    {
        const ulong Poly = 0xC96C5795D7870F42UL;
        var table = new ulong[256];
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Compute(ReadOnlySpan<byte> data, ulong crc = 0)
    {
        crc = ~crc;
        ref ulong tableRef = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(Table);
        for (int i = 0; i < data.Length; i++)
        {
            crc = Unsafe.Add(ref tableRef, (int)(byte)(crc ^ data[i])) ^ (crc >> 8);
        }
        return ~crc;
    }

    /// <summary>
    /// Computes CRC64 and writes it as 8 little-endian bytes.
    /// </summary>
    public static void WriteLE(ReadOnlySpan<byte> data, Span<byte> output)
    {
        ulong crc = Compute(data);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(output, crc);
    }

    /// <summary>
    /// Verifies CRC64 stored as 8 little-endian bytes.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> expected)
    {
        ulong computed = Compute(data);
        ulong stored = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(expected);
        return computed == stored;
    }

    /// <summary>
    /// Gets the CRC32 hash table used by LZ match finders for hashing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint GetCrc32HashValue(byte b) => Crc32Table[b];
}
