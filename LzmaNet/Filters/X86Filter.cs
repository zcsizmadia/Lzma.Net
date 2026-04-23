// SPDX-License-Identifier: 0BSD

// Ported from liblzma/simple/x86.c (Igor Pavlov, Lasse Collin)

namespace LzmaNet.Filters;

/// <summary>
/// BCJ filter for x86 (32-bit and 64-bit) binaries.
/// Converts relative addresses in CALL (E8) and JMP (E9) instructions to absolute.
/// Filter ID: 0x04. Properties: optional 4-byte start offset (default 0).
/// </summary>
internal sealed class X86Filter : IBcjFilter
{
    private uint _prevMask;
    private uint _prevPos;

    public X86Filter()
    {
        _prevMask = 0;
        _prevPos = unchecked((uint)-5);
    }

    public int Encode(Span<byte> buffer, uint startPos) => Code(buffer, startPos, isEncoder: true);
    public int Decode(Span<byte> buffer, uint startPos) => Code(buffer, startPos, isEncoder: false);

    private int Code(Span<byte> buffer, uint nowPos, bool isEncoder)
    {
        uint prevMask = _prevMask;
        uint prevPos = _prevPos;

        if (buffer.Length < 5)
            return 0;

        if (nowPos - prevPos > 5)
            prevPos = nowPos - 5;

        int limit = buffer.Length - 5;
        int bufferPos = 0;

        while (bufferPos <= limit)
        {
            byte b = buffer[bufferPos];
            if (b != 0xE8 && b != 0xE9)
            {
                bufferPos++;
                continue;
            }

            uint offset = nowPos + (uint)bufferPos - prevPos;
            prevPos = nowPos + (uint)bufferPos;

            if (offset > 5)
            {
                prevMask = 0;
            }
            else
            {
                for (uint i = 0; i < offset; i++)
                {
                    prevMask &= 0x77;
                    prevMask <<= 1;
                }
            }

            b = buffer[bufferPos + 4];

            if (Test86MSByte(b) && (prevMask >> 1) <= 4 && (prevMask >> 1) != 3)
            {
                uint src = ((uint)b << 24)
                    | ((uint)buffer[bufferPos + 3] << 16)
                    | ((uint)buffer[bufferPos + 2] << 8)
                    | buffer[bufferPos + 1];

                uint dest;
                while (true)
                {
                    if (isEncoder)
                        dest = src + (nowPos + (uint)bufferPos + 5);
                    else
                        dest = src - (nowPos + (uint)bufferPos + 5);

                    if (prevMask == 0)
                        break;

                    uint idx = MaskToBitNumber[(int)(prevMask >> 1)];
                    b = (byte)(dest >> (int)(24 - (int)idx * 8));

                    if (!Test86MSByte(b))
                        break;

                    src = dest ^ ((1U << (int)(32 - idx * 8)) - 1);
                }

                buffer[bufferPos + 4] = (byte)(~(((dest >> 24) & 1) - 1));
                buffer[bufferPos + 3] = (byte)(dest >> 16);
                buffer[bufferPos + 2] = (byte)(dest >> 8);
                buffer[bufferPos + 1] = (byte)dest;
                bufferPos += 5;
                prevMask = 0;
            }
            else
            {
                bufferPos++;
                prevMask |= 1;
                if (Test86MSByte(b))
                    prevMask |= 0x10;
            }
        }

        _prevMask = prevMask;
        _prevPos = prevPos;

        return bufferPos;
    }

    private static bool Test86MSByte(byte b) => b == 0 || b == 0xFF;

    private static ReadOnlySpan<uint> MaskToBitNumber => [0, 1, 2, 2, 3];
}
