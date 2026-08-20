// SPDX-License-Identifier: 0BSD

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace LzmaNet.Check;

/// <summary>
/// CRC64 using the polynomial from the ECMA-182 standard (0xC96C5795D7870F42 reflected).
/// Used by LZMA_CHECK_CRC64 in XZ containers.
/// Bulk data uses carry-less multiplication folding (PCLMULQDQ) where available,
/// falling back to slicing-by-8.
/// </summary>
internal static class Crc64
{
    private const ulong PolyNormal = 0x42F0E1EBA9EA3693; // non-reflected ECMA-182

    // Folding constants derived from the polynomial at startup (see CrcFolding).
    private static readonly Vector128<ulong> Fold512 = Vector128.Create(
        CrcFolding.XPowModP(512 + 64 - 1, PolyNormal, 64),
        CrcFolding.XPowModP(512 + 64 - 65, PolyNormal, 64));
    private static readonly Vector128<ulong> Fold128 = Vector128.Create(
        CrcFolding.XPowModP(128 + 64 - 1, PolyNormal, 64),
        CrcFolding.XPowModP(128 + 64 - 65, PolyNormal, 64));
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
        if (Pclmulqdq.IsSupported && data.Length >= 64)
            crc = UpdateClmul(data, crc);
        else
            crc = UpdateScalar(data, crc);
        return ~crc;
    }

    /// <summary>
    /// Table-only computation (slicing-by-8), exposed for tests and benchmarks.
    /// </summary>
    internal static ulong ComputeScalar(ReadOnlySpan<byte> data, ulong crc = 0)
    {
        return ~UpdateScalar(data, ~crc);
    }

    private static ulong UpdateScalar(ReadOnlySpan<byte> data, ulong crc)
    {
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

        return crc;
    }

    private static ulong UpdateClmul(ReadOnlySpan<byte> data, ulong crc)
    {
        ref byte src = ref MemoryMarshal.GetReference(data);
        int length = data.Length;
        int offset = 0;

        Vector128<ulong> a0 = Vector128.LoadUnsafe(ref src, 0).AsUInt64()
            ^ Vector128.CreateScalar(crc);
        Vector128<ulong> a1 = Vector128.LoadUnsafe(ref src, 16).AsUInt64();
        Vector128<ulong> a2 = Vector128.LoadUnsafe(ref src, 32).AsUInt64();
        Vector128<ulong> a3 = Vector128.LoadUnsafe(ref src, 48).AsUInt64();
        offset += 64;

        while (length - offset >= 64)
        {
            a0 = Fold(a0, Fold512, Vector128.LoadUnsafe(ref src, (nuint)offset).AsUInt64());
            a1 = Fold(a1, Fold512, Vector128.LoadUnsafe(ref src, (nuint)(offset + 16)).AsUInt64());
            a2 = Fold(a2, Fold512, Vector128.LoadUnsafe(ref src, (nuint)(offset + 32)).AsUInt64());
            a3 = Fold(a3, Fold512, Vector128.LoadUnsafe(ref src, (nuint)(offset + 48)).AsUInt64());
            offset += 64;
        }

        Vector128<ulong> acc = Fold(a0, Fold128, a1);
        acc = Fold(acc, Fold128, a2);
        acc = Fold(acc, Fold128, a3);

        while (length - offset >= 16)
        {
            acc = Fold(acc, Fold128, Vector128.LoadUnsafe(ref src, (nuint)offset).AsUInt64());
            offset += 16;
        }

        // Final reduction via the exact table computation over the accumulator
        // bytes (congruent mod P to the processed prefix), then the tail.
        Span<byte> accBytes = stackalloc byte[16];
        acc.AsByte().CopyTo(accBytes);
        crc = UpdateScalar(accBytes, 0);
        return UpdateScalar(data.Slice(offset), crc);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> Fold(Vector128<ulong> acc, Vector128<ulong> k, Vector128<ulong> data)
    {
        return Pclmulqdq.CarrylessMultiply(acc, k, 0x00)
             ^ Pclmulqdq.CarrylessMultiply(acc, k, 0x11)
             ^ data;
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
