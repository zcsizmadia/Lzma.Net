// SPDX-License-Identifier: 0BSD

// Ported from liblzma/simple/ia64.c (Igor Pavlov, Lasse Collin)

namespace LzmaNet.Filters;

/// <summary>
/// BCJ filter for IA-64 (Itanium) binaries.
/// Converts relative addresses in branch instructions (B1/B2/B3 slots) to absolute.
/// Filter ID: 0x06.
/// </summary>
internal sealed class Ia64Filter : IBcjFilter
{
    private static ReadOnlySpan<uint> BranchTable =>
    [
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
        4, 4, 6, 6, 0, 0, 7, 7,
        4, 4, 0, 0, 4, 4, 0, 0
    ];

    public int Encode(Span<byte> buffer, uint startPos) => Code(buffer, startPos, isEncoder: true);
    public int Decode(Span<byte> buffer, uint startPos) => Code(buffer, startPos, isEncoder: false);

    private static int Code(Span<byte> buffer, uint nowPos, bool isEncoder)
    {
        int size = buffer.Length & ~15;
        int i;
        for (i = 0; i < size; i += 16)
        {
            uint instrTemplate = (uint)(buffer[i] & 0x1F);
            uint mask = BranchTable[(int)instrTemplate];
            uint bitPos = 5;

            for (int slot = 0; slot < 3; slot++, bitPos += 41)
            {
                if (((mask >> slot) & 1) == 0)
                    continue;

                int bytePos = (int)(bitPos >> 3);
                uint bitRes = bitPos & 0x7;
                ulong instruction = 0;

                for (int j = 0; j < 6; j++)
                    instruction += (ulong)buffer[i + j + bytePos] << (8 * j);

                ulong instNorm = instruction >> (int)bitRes;

                if (((instNorm >> 37) & 0xF) == 0x5
                    && ((instNorm >> 9) & 0x7) == 0)
                {
                    uint src = (uint)((instNorm >> 13) & 0xFFFFF);
                    src |= (uint)(((instNorm >> 36) & 1) << 20);
                    src <<= 4;

                    uint dest;
                    if (isEncoder)
                        dest = nowPos + (uint)i + src;
                    else
                        dest = src - (nowPos + (uint)i);

                    dest >>= 4;

                    instNorm &= ~((ulong)0x8FFFFF << 13);
                    instNorm |= (ulong)(dest & 0xFFFFF) << 13;
                    instNorm |= (ulong)(dest & 0x100000) << (36 - 20);

                    instruction &= (1u << (int)bitRes) - 1;
                    instruction |= instNorm << (int)bitRes;

                    for (int j = 0; j < 6; j++)
                        buffer[i + j + bytePos] = (byte)(instruction >> (8 * j));
                }
            }
        }
        return i;
    }
}
