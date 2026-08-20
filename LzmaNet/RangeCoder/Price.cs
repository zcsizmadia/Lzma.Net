// SPDX-License-Identifier: 0BSD

using System.Runtime.CompilerServices;

namespace LzmaNet.RangeCoder;

/// <summary>
/// Bit-price estimation for range-coded symbols, used by the optimal parser.
/// Prices are in 1/16-bit units, derived from the probability model exactly the
/// way the LZMA reference encoder does it.
/// </summary>
internal static class Price
{
    private const int kNumBitModelTotalBits = RangeDecoder.kNumBitModelTotalBits;
    private const uint kBitModelTotal = RangeDecoder.kBitModelTotal;
    private const int kNumMoveReducingBits = 4;
    private const int kCyclesBits = 4;

    /// <summary>Price granularity: 16 units per bit.</summary>
    public const int kNumBitPriceShiftBits = 4;

    private static readonly uint[] ProbPrices = BuildProbPrices();

    private static uint[] BuildProbPrices()
    {
        var prices = new uint[kBitModelTotal >> kNumMoveReducingBits];
        for (uint i = (1u << kNumMoveReducingBits) / 2; i < kBitModelTotal; i += 1u << kNumMoveReducingBits)
        {
            uint w = i;
            uint bitCount = 0;
            for (int j = 0; j < kCyclesBits; j++)
            {
                w *= w;
                bitCount <<= 1;
                while (w >= (1u << 16))
                {
                    w >>= 1;
                    bitCount++;
                }
            }
            prices[i >> kNumMoveReducingBits] =
                (uint)(kNumBitModelTotalBits << kCyclesBits) - 15 - bitCount;
        }
        return prices;
    }

    /// <summary>Price of encoding <paramref name="bit"/> with the given probability.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint GetPrice(uint prob, uint bit)
    {
        return ProbPrices[((prob ^ (uint)(-(int)bit)) & (kBitModelTotal - 1)) >> kNumMoveReducingBits];
    }

    /// <summary>Price of encoding a 0 bit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint GetPrice0(uint prob) => ProbPrices[prob >> kNumMoveReducingBits];

    /// <summary>Price of encoding a 1 bit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint GetPrice1(uint prob)
        => ProbPrices[(prob ^ (kBitModelTotal - 1)) >> kNumMoveReducingBits];

    /// <summary>Price of a bit-tree-coded symbol.</summary>
    public static uint GetBitTreePrice(ushort[] probs, int offset, int numBits, uint symbol)
    {
        uint price = 0;
        uint m = 1;
        for (int i = numBits - 1; i >= 0; i--)
        {
            uint bit = (symbol >> i) & 1;
            price += GetPrice(probs[offset + m], bit);
            m = (m << 1) | bit;
        }
        return price;
    }

    /// <summary>Price of a reverse-bit-tree-coded symbol.</summary>
    public static uint GetReverseBitTreePrice(ushort[] probs, int offset, int numBits, uint symbol)
    {
        uint price = 0;
        uint m = 1;
        for (int i = 0; i < numBits; i++)
        {
            uint bit = symbol & 1;
            symbol >>= 1;
            price += GetPrice(probs[offset + m], bit);
            m = (m << 1) | bit;
        }
        return price;
    }

    /// <summary>Price of directly coded (0.5 probability) bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint GetDirectBitsPrice(int numBits) => (uint)numBits << kNumBitPriceShiftBits;
}
