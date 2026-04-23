// SPDX-License-Identifier: 0BSD

// Ported from liblzma/simple/armthumb.c (Igor Pavlov, Lasse Collin)

namespace LzmaNet.Filters;

/// <summary>
/// BCJ filter for ARM-Thumb binaries.
/// Converts relative addresses in BL (Thumb-2) instructions to absolute.
/// Filter ID: 0x08.
/// </summary>
internal sealed class ArmThumbFilter : IBcjFilter
{
    public int Encode(Span<byte> buffer, uint startPos) => Code(buffer, startPos, isEncoder: true);
    public int Decode(Span<byte> buffer, uint startPos) => Code(buffer, startPos, isEncoder: false);

    private static int Code(Span<byte> buffer, uint nowPos, bool isEncoder)
    {
        if (buffer.Length < 4)
            return 0;

        int size = buffer.Length - 4;
        int i;
        for (i = 0; i <= size; i += 2)
        {
            if ((buffer[i + 1] & 0xF8) == 0xF0 && (buffer[i + 3] & 0xF8) == 0xF8)
            {
                uint src = (((uint)buffer[i + 1] & 7) << 19)
                    | ((uint)buffer[i] << 11)
                    | (((uint)buffer[i + 3] & 7) << 8)
                    | buffer[i + 2];
                src <<= 1;

                uint dest;
                if (isEncoder)
                    dest = nowPos + (uint)i + 4 + src;
                else
                    dest = src - (nowPos + (uint)i + 4);

                dest >>= 1;
                buffer[i + 1] = (byte)(0xF0 | ((dest >> 19) & 0x7));
                buffer[i] = (byte)(dest >> 11);
                buffer[i + 3] = (byte)(0xF8 | ((dest >> 8) & 0x7));
                buffer[i + 2] = (byte)dest;
                i += 2;
            }
        }
        return i;
    }
}
