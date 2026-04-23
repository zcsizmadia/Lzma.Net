// SPDX-License-Identifier: 0BSD

// Ported from liblzma/simple/arm.c (Igor Pavlov, Lasse Collin)

namespace LzmaNet.Filters;

/// <summary>
/// BCJ filter for 32-bit ARM binaries.
/// Converts relative addresses in BL instructions to absolute.
/// Filter ID: 0x07.
/// </summary>
internal sealed class ArmFilter : IBcjFilter
{
    public int Encode(Span<byte> buffer, uint startPos) => Code(buffer, startPos, isEncoder: true);
    public int Decode(Span<byte> buffer, uint startPos) => Code(buffer, startPos, isEncoder: false);

    private static int Code(Span<byte> buffer, uint nowPos, bool isEncoder)
    {
        int size = buffer.Length & ~3;
        int i;
        for (i = 0; i < size; i += 4)
        {
            if (buffer[i + 3] == 0xEB)
            {
                uint src = ((uint)buffer[i + 2] << 16)
                    | ((uint)buffer[i + 1] << 8)
                    | buffer[i];
                src <<= 2;

                uint dest;
                if (isEncoder)
                    dest = nowPos + (uint)i + 8 + src;
                else
                    dest = src - (nowPos + (uint)i + 8);

                dest >>= 2;
                buffer[i + 2] = (byte)(dest >> 16);
                buffer[i + 1] = (byte)(dest >> 8);
                buffer[i] = (byte)dest;
            }
        }
        return i;
    }
}
