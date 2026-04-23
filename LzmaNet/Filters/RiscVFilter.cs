// SPDX-License-Identifier: 0BSD

// Ported from liblzma/simple/riscv.c (Lasse Collin, Jia Tan)

using System.Buffers.Binary;

namespace LzmaNet.Filters;

/// <summary>
/// BCJ filter for RISC-V binaries (32-bit and 64-bit, both endiannesses).
/// Converts JAL and AUIPC+inst2 pairs.
/// Filter ID: 0x0B.
/// </summary>
internal sealed class RiscVFilter : IBcjFilter
{
    public int Encode(Span<byte> buffer, uint startPos) => EncodeImpl(buffer, startPos);
    public int Decode(Span<byte> buffer, uint startPos) => DecodeImpl(buffer, startPos);

    private static int EncodeImpl(Span<byte> buffer, uint nowPos)
    {
        if (buffer.Length < 8)
            return 0;

        int size = buffer.Length - 8;
        int i;
        for (i = 0; i <= size; i += 2)
        {
            uint inst = buffer[i];

            if (inst == 0xEF)
            {
                // JAL
                uint b1 = buffer[i + 1];
                if ((b1 & 0x0D) != 0)
                    continue;

                uint b2 = buffer[i + 2];
                uint b3 = buffer[i + 3];
                uint pc = nowPos + (uint)i;

                uint addr = ((b1 & 0xF0) << 8)
                    | ((b2 & 0x0F) << 16)
                    | ((b2 & 0x10) << 7)
                    | ((b2 & 0xE0) >> 4)
                    | ((b3 & 0x7F) << 4)
                    | ((b3 & 0x80) << 13);

                addr += pc;

                buffer[i + 1] = (byte)((b1 & 0x0F) | ((addr >> 13) & 0xF0));
                buffer[i + 2] = (byte)(addr >> 9);
                buffer[i + 3] = (byte)(addr >> 1);

                i += 4 - 2;
            }
            else if ((inst & 0x7F) == 0x17)
            {
                // AUIPC
                inst |= (uint)buffer[i + 1] << 8;
                inst |= (uint)buffer[i + 2] << 16;
                inst |= (uint)buffer[i + 3] << 24;

                if ((inst & 0xE80) != 0)
                {
                    // rd != x0 && rd != x2
                    uint inst2 = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(i + 4));

                    if (NotAuipcPair(inst, inst2))
                    {
                        i += 6 - 2;
                        continue;
                    }

                    uint addr = inst & 0xFFFFF000;
                    addr += (inst2 >> 20) - ((inst2 >> 19) & 0x1000);
                    addr += nowPos + (uint)i;

                    inst = 0x17 | (2u << 7) | (inst2 << 12);
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(i), inst);
                    BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(i + 4), addr);
                }
                else
                {
                    // rd == x0 or x2 — fake decode for bijectivity
                    uint fakeRs1 = inst >> 27;

                    if (NotSpecialAuipc(inst, fakeRs1))
                    {
                        i += 4 - 2;
                        continue;
                    }

                    uint fakeAddr = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(i + 4));
                    uint fakeInst2 = (inst >> 12) | (fakeAddr << 20);
                    inst = 0x17 | (fakeRs1 << 7) | (fakeAddr & 0xFFFFF000);

                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(i), inst);
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(i + 4), fakeInst2);
                }

                i += 8 - 2;
            }
        }
        return i;
    }

    private static int DecodeImpl(Span<byte> buffer, uint nowPos)
    {
        if (buffer.Length < 8)
            return 0;

        int size = buffer.Length - 8;
        int i;
        for (i = 0; i <= size; i += 2)
        {
            uint inst = buffer[i];

            if (inst == 0xEF)
            {
                // JAL
                uint b1 = buffer[i + 1];
                if ((b1 & 0x0D) != 0)
                    continue;

                uint b2 = buffer[i + 2];
                uint b3 = buffer[i + 3];
                uint pc = nowPos + (uint)i;

                uint addr = ((b1 & 0xF0) << 13)
                    | (b2 << 9) | (b3 << 1);

                addr -= pc;

                buffer[i + 1] = (byte)((b1 & 0x0F) | ((addr >> 8) & 0xF0));
                buffer[i + 2] = (byte)(((addr >> 16) & 0x0F)
                    | ((addr >> 7) & 0x10)
                    | ((addr << 4) & 0xE0));
                buffer[i + 3] = (byte)(((addr >> 4) & 0x7F)
                    | ((addr >> 13) & 0x80));

                i += 4 - 2;
            }
            else if ((inst & 0x7F) == 0x17)
            {
                // AUIPC
                uint inst2;

                inst |= (uint)buffer[i + 1] << 8;
                inst |= (uint)buffer[i + 2] << 16;
                inst |= (uint)buffer[i + 3] << 24;

                if ((inst & 0xE80) != 0)
                {
                    // rd != x0 && rd != x2 — check for fake pair
                    inst2 = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(i + 4));

                    if (NotAuipcPair(inst, inst2))
                    {
                        i += 6 - 2;
                        continue;
                    }

                    // Fake decode
                    uint addr = inst & 0xFFFFF000;
                    addr += inst2 >> 20;

                    inst = 0x17 | (2u << 7) | (inst2 << 12);
                    inst2 = addr;
                }
                else
                {
                    // rd == x0 or x2 — real decode
                    uint inst2Rs1 = inst >> 27;

                    if (NotSpecialAuipc(inst, inst2Rs1))
                    {
                        i += 4 - 2;
                        continue;
                    }

                    uint addr = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(i + 4));
                    addr -= nowPos + (uint)i;

                    inst2 = (inst >> 12) | (addr << 20);
                    inst = 0x17 | (inst2Rs1 << 7)
                        | ((addr + 0x800) & 0xFFFFF000);
                }

                BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(i), inst);
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(i + 4), inst2);

                i += 8 - 2;
            }
        }
        return i;
    }

    private static bool NotAuipcPair(uint auipc, uint inst2)
        => (((auipc << 8) ^ (inst2 - 3)) & 0xF8003) != 0;

    private static bool NotSpecialAuipc(uint auipc, uint inst2Rs1)
        => (uint)((auipc - 0x3117) << 18) >= (inst2Rs1 & 0x1D);
}
