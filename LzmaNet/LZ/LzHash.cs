// SPDX-License-Identifier: 0BSD

using System.Runtime.CompilerServices;

namespace LzmaNet.LZ;

/// <summary>
/// Hashing shared by the match finders: the CRC-32 byte table and the 2/3/4-byte
/// hash values derived from it. Both finders index the same three tables
/// (2-byte heads, 3-byte heads, and the main 4-byte table) laid out in one array,
/// so the slot arithmetic belongs in one place rather than being repeated at
/// every insertion site.
/// </summary>
internal static class LzHash
{
    public const int kHash2Size = 1 << 10;
    public const int kHash3Size = 1 << 16;

    /// <summary>Offset of the 4-byte hash region, past the 2- and 3-byte heads.</summary>
    public const int kFixHashSize = kHash2Size + kHash3Size;

    /// <summary>
    /// Classic IEEE CRC-32 byte table (polynomial 0xEDB88320), used here only as
    /// a byte-mixing function for hashing, not as a checksum.
    /// </summary>
    public static readonly uint[] CrcTable = CreateCrcTable();

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    /// <summary>
    /// Computes the three hash slots for the four bytes at
    /// <paramref name="cur"/>. <paramref name="hashMask"/> sizes the 4-byte
    /// table; the 2- and 3-byte tables have fixed sizes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Compute(byte[] buffer, int cur, int hashMask,
        out uint h2, out uint h3, out uint h4)
    {
        uint[] crc = CrcTable;
        uint hash2Val = crc[buffer[cur]] ^ buffer[cur + 1];
        uint hash3Val = hash2Val ^ (crc[buffer[cur + 2]] << 5);
        uint hash4Val = hash3Val ^ (crc[buffer[cur + 3]] << 13);

        h2 = hash2Val & (kHash2Size - 1);
        h3 = kHash2Size + (hash3Val & (kHash3Size - 1));
        h4 = kFixHashSize + (hash4Val & (uint)hashMask);
    }

    /// <summary>
    /// Hash-table entry count for a 4-byte table addressed by
    /// <paramref name="hashMask"/>, including the fixed 2- and 3-byte regions.
    /// </summary>
    public static int TableSize(int hashMask) => kFixHashSize + hashMask + 1;

    /// <summary>
    /// Bits of 4-byte hash to keep for the given dictionary size, matching the
    /// reference encoder's sizing.
    /// </summary>
    public static int HashBits(int dictSize) =>
        dictSize < (1 << 16) ? 16 : dictSize < (1 << 20) ? 18 : 20;
}
