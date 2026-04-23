// SPDX-License-Identifier: 0BSD

// Ported from liblzma/simple/powerpc.c (Igor Pavlov, Lasse Collin)

namespace LzmaNet.Filters;

/// <summary>
/// BCJ filter for PowerPC (big endian) binaries.
/// Converts relative addresses in branch (B/BL) instructions to absolute.
/// Filter ID: 0x05.
/// </summary>
internal sealed class PowerPcFilter : IBcjFilter
{
    public int Encode(Span<byte> buffer, uint startPos) => Code(buffer, startPos, isEncoder: true);
    public int Decode(Span<byte> buffer, uint startPos) => Code(buffer, startPos, isEncoder: false);

    private static int Code(Span<byte> buffer, uint nowPos, bool isEncoder)
    {
        int size = buffer.Length & ~3;
        int i;
        for (i = 0; i < size; i += 4)
        {
            // PowerPC branch: opcode 0x48 (bits 31-26 = 010010), AA=0, LK=1
            if ((buffer[i] >> 2) == 0x12 && (buffer[i + 3] & 3) == 1)
            {
                uint src = (((uint)buffer[i] & 3) << 24)
                    | ((uint)buffer[i + 1] << 16)
                    | ((uint)buffer[i + 2] << 8)
                    | ((uint)buffer[i + 3] & ~3u);

                uint dest;
                if (isEncoder)
                    dest = nowPos + (uint)i + src;
                else
                    dest = src - (nowPos + (uint)i);

                buffer[i] = (byte)(0x48 | ((dest >> 24) & 0x03));
                buffer[i + 1] = (byte)(dest >> 16);
                buffer[i + 2] = (byte)(dest >> 8);
                buffer[i + 3] &= 0x03;
                buffer[i + 3] |= (byte)dest;
            }
        }
        return i;
    }
}
