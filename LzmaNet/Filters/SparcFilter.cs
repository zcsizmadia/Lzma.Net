// SPDX-License-Identifier: 0BSD

// Ported from liblzma/simple/sparc.c (Igor Pavlov, Lasse Collin)

namespace LzmaNet.Filters;

/// <summary>
/// BCJ filter for SPARC binaries.
/// Converts relative addresses in CALL instructions to absolute.
/// Filter ID: 0x09.
/// </summary>
internal sealed class SparcFilter : IBcjFilter
{
    public int Encode(Span<byte> buffer, uint startPos) => Code(buffer, startPos, isEncoder: true);
    public int Decode(Span<byte> buffer, uint startPos) => Code(buffer, startPos, isEncoder: false);

    private static int Code(Span<byte> buffer, uint nowPos, bool isEncoder)
    {
        int size = buffer.Length & ~3;
        int i;
        for (i = 0; i < size; i += 4)
        {
            if ((buffer[i] == 0x40 && (buffer[i + 1] & 0xC0) == 0x00)
                || (buffer[i] == 0x7F && (buffer[i + 1] & 0xC0) == 0xC0))
            {
                uint src = ((uint)buffer[i] << 24)
                    | ((uint)buffer[i + 1] << 16)
                    | ((uint)buffer[i + 2] << 8)
                    | buffer[i + 3];
                src <<= 2;

                uint dest;
                if (isEncoder)
                    dest = nowPos + (uint)i + src;
                else
                    dest = src - (nowPos + (uint)i);

                dest >>= 2;

                dest = (((0u - ((dest >> 22) & 1)) << 22) & 0x3FFFFFFF)
                    | (dest & 0x3FFFFF)
                    | 0x40000000;

                buffer[i] = (byte)(dest >> 24);
                buffer[i + 1] = (byte)(dest >> 16);
                buffer[i + 2] = (byte)(dest >> 8);
                buffer[i + 3] = (byte)dest;
            }
        }
        return i;
    }
}
