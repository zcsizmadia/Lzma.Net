// SPDX-License-Identifier: 0BSD

#if !NET8_0_OR_GREATER

using System.Runtime.CompilerServices;

namespace System.Numerics;

/// <summary>
/// The subset of <c>System.Numerics.BitOperations</c> this library uses, for
/// targets that predate it. Declared in the framework's own namespace so call
/// sites are identical on every target; the real type wins wherever it exists,
/// because this file is not compiled there.
/// </summary>
/// <remarks>
/// These are the portable software implementations. On .NET 8 and later the
/// framework maps the same calls to LZCNT/TZCNT where the hardware has them,
/// which is one of the reasons the portable target is slower.
/// </remarks>
internal static class BitOperations
{
    /// <summary>
    /// Number of leading zero bits in <paramref name="value"/>; 32 when it is zero.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LeadingZeroCount(uint value)
    {
        if (value == 0)
            return 32;

        int count = 0;
        if ((value & 0xFFFF0000u) == 0) { count += 16; value <<= 16; }
        if ((value & 0xFF000000u) == 0) { count += 8; value <<= 8; }
        if ((value & 0xF0000000u) == 0) { count += 4; value <<= 4; }
        if ((value & 0xC0000000u) == 0) { count += 2; value <<= 2; }
        if ((value & 0x80000000u) == 0) { count += 1; }
        return count;
    }

    /// <summary>
    /// Number of trailing zero bits in <paramref name="value"/>; 32 when it is zero.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int TrailingZeroCount(uint value)
    {
        if (value == 0)
            return 32;

        // de Bruijn sequence: isolating the lowest set bit and multiplying maps
        // each of the 32 possible positions to a distinct top-5-bit index.
        return TrailingZeroCountDeBruijn[(int)(((value & (uint)-(int)value) * 0x077CB531u) >> 27)];
    }

    /// <summary>
    /// Number of trailing zero bits in <paramref name="value"/>; 64 when it is zero.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int TrailingZeroCount(ulong value)
    {
        uint low = (uint)value;
        return low != 0 ? TrailingZeroCount(low) : 32 + TrailingZeroCount((uint)(value >> 32));
    }

    /// <summary>
    /// Smallest power of two greater than or equal to <paramref name="value"/>.
    /// Returns 1 for zero, and 0 when the result would not fit in 32 bits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint RoundUpToPowerOf2(uint value)
    {
        --value;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1;
    }

    private static readonly byte[] TrailingZeroCountDeBruijn =
    [
        00, 01, 28, 02, 29, 14, 24, 03, 30, 22, 20, 15, 25, 17, 04, 08,
        31, 27, 13, 23, 21, 19, 16, 07, 26, 12, 18, 06, 11, 05, 10, 09,
    ];
}

#endif
