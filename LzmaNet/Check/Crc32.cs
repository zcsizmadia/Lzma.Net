// SPDX-License-Identifier: 0BSD

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace LzmaNet.Check;

/// <summary>
/// CRC32 using the polynomial from the IEEE 802.3 standard (0xEDB88320 reflected).
/// Used by XZ stream headers, footers, block headers, and LZMA_CHECK_CRC32.
/// Bulk data uses carry-less multiplication folding (PCLMULQDQ) where available,
/// falling back to slicing-by-8.
/// </summary>
internal static class Crc32
{
    private const ulong PolyNormal = 0x04C11DB7; // non-reflected form of 0xEDB88320

    // 8 tables of 256 entries, flattened: Table[k * 256 + v] is the CRC of
    // byte v followed by k zero bytes. Table[0..256) is the classic table.
    private static readonly uint[] Table = CreateTable();

    // Folding constants for the carry-less-multiply path, derived from the
    // polynomial at startup (see CrcFolding). Element 0 folds the low half of a
    // 128-bit accumulator, element 1 the high half. Exponent pairs follow the
    // standard reflected-CRC folding scheme for fold distances of 512 bits
    // (4 accumulators, 64-byte stride) and 128 bits (combine/single stride).
    private static readonly Vector128<ulong> Fold512 = Vector128.Create(
        CrcFolding.XPowModP(512 + 32 - 1, PolyNormal, 32),
        CrcFolding.XPowModP(512 + 32 - 65, PolyNormal, 32));
    private static readonly Vector128<ulong> Fold128 = Vector128.Create(
        CrcFolding.XPowModP(128 + 32 - 1, PolyNormal, 32),
        CrcFolding.XPowModP(128 + 32 - 65, PolyNormal, 32));

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
        if (Pclmulqdq.IsSupported && data.Length >= 64)
            crc = UpdateClmul(data, crc);
        else
            crc = UpdateScalar(data, crc);
        return ~crc;
    }

    /// <summary>
    /// Table-only computation (slicing-by-8), exposed for tests and benchmarks.
    /// </summary>
    internal static uint ComputeScalar(ReadOnlySpan<byte> data, uint crc = 0)
    {
        return ~UpdateScalar(data, ~crc);
    }

    private static uint UpdateScalar(ReadOnlySpan<byte> data, uint crc)
    {
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

        return crc;
    }

    private static uint UpdateClmul(ReadOnlySpan<byte> data, uint crc)
    {
        ref byte src = ref MemoryMarshal.GetReference(data);
        int length = data.Length;
        int offset = 0;

        // Load the first 64 bytes into four accumulators; the incoming state is
        // XORed into the first bytes of the stream, same as the table loop does.
        Vector128<ulong> a0 = Vector128.LoadUnsafe(ref src, 0).AsUInt64()
            ^ Vector128.CreateScalar(crc).AsUInt64();
        Vector128<ulong> a1 = Vector128.LoadUnsafe(ref src, 16).AsUInt64();
        Vector128<ulong> a2 = Vector128.LoadUnsafe(ref src, 32).AsUInt64();
        Vector128<ulong> a3 = Vector128.LoadUnsafe(ref src, 48).AsUInt64();
        offset += 64;

        // Fold 64 bytes per iteration.
        while (length - offset >= 64)
        {
            a0 = Fold(a0, Fold512, Vector128.LoadUnsafe(ref src, (nuint)offset).AsUInt64());
            a1 = Fold(a1, Fold512, Vector128.LoadUnsafe(ref src, (nuint)(offset + 16)).AsUInt64());
            a2 = Fold(a2, Fold512, Vector128.LoadUnsafe(ref src, (nuint)(offset + 32)).AsUInt64());
            a3 = Fold(a3, Fold512, Vector128.LoadUnsafe(ref src, (nuint)(offset + 48)).AsUInt64());
            offset += 64;
        }

        // Combine the four accumulators (each 128 bits apart).
        Vector128<ulong> acc = Fold(a0, Fold128, a1);
        acc = Fold(acc, Fold128, a2);
        acc = Fold(acc, Fold128, a3);

        // Fold any remaining whole 16-byte blocks.
        while (length - offset >= 16)
        {
            acc = Fold(acc, Fold128, Vector128.LoadUnsafe(ref src, (nuint)offset).AsUInt64());
            offset += 16;
        }

        // Final reduction: the accumulator is congruent (mod P) to the processed
        // prefix, so running the exact table computation over its 16 bytes yields
        // the true CRC state; then finish the sub-16-byte tail the same way.
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

/// <summary>
/// Shared helper for deriving carry-less-multiply CRC folding constants from a
/// polynomial. Constants are computed at startup instead of being hard-coded,
/// so they are correct by construction for any width/polynomial.
/// </summary>
internal static class CrcFolding
{
    /// <summary>
    /// Returns the bit-reflected value of (x^exponent mod P) for a CRC of the
    /// given width, used as an unshifted operand for reflected-domain
    /// carry-less multiplication. For a fold distance of D bits, the low half
    /// of a 128-bit accumulator folds with exponent D + width - 1 and the high
    /// half with D + width - 65 (validated against a bit-wise reference for
    /// both CRC-32 and CRC-64; the -1 absorbs the 127-bit product alignment of
    /// reflected CLMUL).
    /// </summary>
    public static ulong XPowModP(int exponent, ulong polyNormal, int width)
    {
        ulong mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
        ulong r = 1; // x^0
        for (int i = 0; i < exponent; i++)
        {
            bool carry = ((r >> (width - 1)) & 1) != 0;
            r = (r << 1) & mask;
            if (carry)
                r ^= polyNormal;
        }

        // Bit-reflect the width-bit result.
        ulong reflected = 0;
        for (int i = 0; i < width; i++)
        {
            reflected = (reflected << 1) | (r & 1);
            r >>= 1;
        }
        return reflected;
    }
}
