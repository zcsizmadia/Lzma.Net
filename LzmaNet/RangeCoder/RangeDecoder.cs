// SPDX-License-Identifier: 0BSD

using System.Runtime.CompilerServices;

namespace LzmaNet.RangeCoder;

/// <summary>
/// LZMA range decoder. Decodes bits from a range-coded bitstream
/// using adaptive probability models with 11-bit precision.
/// </summary>
internal struct RangeDecoder
{
    internal const int kNumBitModelTotalBits = 11;
    internal const uint kBitModelTotal = 1u << kNumBitModelTotalBits;
    internal const int kNumMoveBits = 5;
    private const uint kTopValue = 1u << 24;

    private uint _range;
    private uint _code;
    private ReadOnlyMemory<byte> _buffer;
    private int _pos;

    /// <summary>Current position in the input buffer.</summary>
    public int Position => _pos;

    /// <summary>
    /// Initializes the range decoder from input data. Reads the initial 5 bytes.
    /// The first byte must be 0x00.
    /// </summary>
    public void Init(ReadOnlyMemory<byte> input, int offset)
    {
        _buffer = input;
        _pos = offset;
        var span = input.Span;

        if (span[_pos] != 0x00)
            throw new LzmaDataErrorException("Invalid range decoder initial byte.");

        _pos++;
        _code = 0;
        _range = 0xFFFFFFFF;
        for (int i = 0; i < 4; i++)
            _code = (_code << 8) | span[_pos++];
    }

    /// <summary>
    /// Initializes the range decoder reading from a span with an external position tracker.
    /// </summary>
    public void Init(ReadOnlySpan<byte> input, ref int offset)
    {
        _buffer = default;
        _pos = 0;

        if (input[offset] != 0x00)
            throw new LzmaDataErrorException("Invalid range decoder initial byte.");

        offset++;
        _code = 0;
        _range = 0xFFFFFFFF;
        for (int i = 0; i < 4; i++)
            _code = (_code << 8) | input[offset++];
    }

    /// <summary>
    /// Sets buffer for continued decoding after Init with span.
    /// </summary>
    public void SetBuffer(ReadOnlyMemory<byte> input, int pos)
    {
        _buffer = input;
        _pos = pos;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Normalize()
    {
        if (_range < kTopValue)
        {
            _range <<= 8;
            _code = (_code << 8) | _buffer.Span[_pos++];
        }
    }

    /// <summary>
    /// Decodes a single bit using an adaptive probability model.
    /// Probability represents P(bit = 0) with 11-bit precision.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DecodeBit(ref ushort prob)
    {
        uint bound = (_range >> kNumBitModelTotalBits) * prob;
        if (_code < bound)
        {
            _range = bound;
            prob += (ushort)((kBitModelTotal - prob) >> kNumMoveBits);
            Normalize();
            return 0;
        }
        else
        {
            _code -= bound;
            _range -= bound;
            prob -= (ushort)(prob >> kNumMoveBits);
            Normalize();
            return 1;
        }
    }

    /// <summary>
    /// Decodes bits using a fixed 0.5 probability (direct bits, no model adaptation).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DecodeDirectBits(int numBits)
    {
        uint result = 0;
        for (int i = numBits; i > 0; i--)
        {
            _range >>= 1;
            uint t = (_code - _range) >> 31; // 1 if code < range, 0 otherwise
            _code -= _range & (t - 1); // subtract range if t == 0
            result = (result << 1) | (1 - t);
            Normalize();
        }
        return result;
    }

    /// <summary>
    /// Decodes a bit tree of the given number of bits. Returns a value in [0, 2^numBits).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DecodeBitTree(ushort[] probs, int probsOffset, int numBits)
    {
        uint symbol = 1;
        for (int i = 0; i < numBits; i++)
            symbol = (symbol << 1) | DecodeBit(ref probs[probsOffset + symbol]);
        return symbol - (1u << numBits);
    }

    /// <summary>
    /// Decodes a reverse bit tree. Returns a value in [0, 2^numBits).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DecodeReverseBitTree(ushort[] probs, int probsOffset, int numBits)
    {
        uint symbol = 1;
        uint result = 0;
        for (int i = 0; i < numBits; i++)
        {
            uint bit = DecodeBit(ref probs[probsOffset + symbol]);
            symbol = (symbol << 1) | bit;
            result |= bit << i;
        }
        return result;
    }

    /// <summary>
    /// Checks if the decoder has finished correctly (code should be 0).
    /// </summary>
    public readonly bool IsFinished => _code == 0;

    /// <summary>
    /// Initializes an array of probability values to their default (0.5 probability).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InitProbs(ushort[] probs)
    {
        const ushort kProbInitValue = (ushort)(kBitModelTotal >> 1); // 1024
        Array.Fill(probs, kProbInitValue);
    }

    /// <summary>
    /// Initializes a segment of probability values to their default.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void InitProbs(ushort[] probs, int offset, int count)
    {
        const ushort kProbInitValue = (ushort)(kBitModelTotal >> 1);
        Array.Fill(probs, kProbInitValue, offset, count);
    }
}
