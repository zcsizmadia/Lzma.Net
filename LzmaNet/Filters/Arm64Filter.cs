// SPDX-License-Identifier: 0BSD

// Ported from liblzma/simple/arm64.c (Lasse Collin, Jia Tan, Igor Pavlov)

using System.Buffers.Binary;

namespace LzmaNet.Filters;

/// <summary>
/// BCJ filter for ARM64 (AArch64) binaries.
/// Converts relative addresses in BL and ADRP instructions to absolute.
/// Filter ID: 0x0A.
/// </summary>
internal sealed class Arm64Filter : IBcjFilter
{
    public int Encode(Span<byte> buffer, uint startPos) => Code(buffer, startPos, isEncoder: true);
    public int Decode(Span<byte> buffer, uint startPos) => Code(buffer, startPos, isEncoder: false);

    private static int Code(Span<byte> buffer, uint nowPos, bool isEncoder)
    {
        int size = buffer.Length & ~3;
        int i;
        for (i = 0; i < size; i += 4)
        {
            uint pc = nowPos + (uint)i;
            uint instr = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(i));

            if ((instr >> 26) == 0x25)
            {
                // BL instruction: convert full 26-bit immediate
                uint src = instr;
                instr = 0x94000000;

                pc >>= 2;
                if (!isEncoder)
                    pc = 0u - pc;

                instr |= (src + pc) & 0x03FFFFFF;
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(i), instr);
            }
            else if ((instr & 0x9F000000) == 0x90000000)
            {
                // ADRP instruction: convert with +/-512 MiB range check
                uint src = ((instr >> 29) & 3)
                    | ((instr >> 3) & 0x001FFFFC);

                // Range check: only convert if within +/-512 MiB
                if (((src + 0x00020000) & 0x001C0000) != 0)
                    continue;

                instr &= 0x9000001F;

                pc >>= 12;
                if (!isEncoder)
                    pc = 0u - pc;

                uint dest = src + pc;
                instr |= (dest & 3) << 29;
                instr |= (dest & 0x0003FFFC) << 3;
                instr |= (0u - (dest & 0x00020000)) & 0x00E00000;
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(i), instr);
            }
        }
        return i;
    }
}
