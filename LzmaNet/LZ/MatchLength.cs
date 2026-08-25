// SPDX-License-Identifier: 0BSD

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace LzmaNet.LZ;

/// <summary>
/// Byte-run comparison shared by the match finders. This is the innermost loop
/// of match finding — every candidate a finder considers is measured with it —
/// so it is worth vectorizing, and worth having exactly one copy of.
/// </summary>
internal static class MatchLength
{
    /// <summary>
    /// Length of the common prefix of the runs at <paramref name="a"/> and
    /// <paramref name="b"/>, up to <paramref name="limit"/> bytes.
    /// </summary>
    /// <remarks>
    /// Callers must guarantee both runs have <paramref name="limit"/> readable
    /// bytes: the vector and 64-bit paths read in blocks and rely on the finder's
    /// buffer being sized with the match-length slack that provides.
    /// </remarks>
    public static int Common(byte[] buffer, int a, int b, int limit)
    {
        int len = 0;
        ref byte bufRef = ref MemoryMarshal.GetArrayDataReference(buffer);

        if (Vector256.IsHardwareAccelerated)
        {
            while (len + 32 <= limit)
            {
                var va = Vector256.LoadUnsafe(ref bufRef, (nuint)(a + len));
                var vb = Vector256.LoadUnsafe(ref bufRef, (nuint)(b + len));
                uint neq = ~Vector256.Equals(va, vb).ExtractMostSignificantBits();
                if (neq != 0)
                    return len + BitOperations.TrailingZeroCount(neq);
                len += 32;
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            while (len + 16 <= limit)
            {
                var va = Vector128.LoadUnsafe(ref bufRef, (nuint)(a + len));
                var vb = Vector128.LoadUnsafe(ref bufRef, (nuint)(b + len));
                uint neq = ~Vector128.Equals(va, vb).ExtractMostSignificantBits() & 0xFFFF;
                if (neq != 0)
                    return len + BitOperations.TrailingZeroCount(neq);
                len += 16;
            }
        }

        if (BitConverter.IsLittleEndian)
        {
            while (len + 8 <= limit)
            {
                ulong x = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bufRef, a + len))
                        ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bufRef, b + len));
                if (x != 0)
                    return len + (BitOperations.TrailingZeroCount(x) >> 3);
                len += 8;
            }
        }

        while (len < limit && buffer[a + len] == buffer[b + len])
            len++;
        return len;
    }

    /// <summary>
    /// Extends a match already known to be at least <paramref name="len"/> long,
    /// resuming the comparison one byte before the known prefix ends.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Extend(byte[] buffer, int a, int b, int len, int limit)
        => len - 1 + Common(buffer, a + len - 1, b + len - 1, limit - (len - 1));
}
