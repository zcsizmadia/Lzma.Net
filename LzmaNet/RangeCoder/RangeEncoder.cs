// SPDX-License-Identifier: 0BSD

using System.Runtime.CompilerServices;

namespace LzmaNet.RangeCoder;

/// <summary>
/// LZMA range encoder. Encodes bits into a range-coded bitstream
/// using adaptive probability models with 11-bit precision.
/// </summary>
internal sealed class RangeEncoder
{
    private const int kNumBitModelTotalBits = RangeDecoder.kNumBitModelTotalBits;
    private const uint kBitModelTotal = RangeDecoder.kBitModelTotal;
    private const int kNumMoveBits = RangeDecoder.kNumMoveBits;
    private const uint kTopValue = 1u << 24;

    private ulong _low;
    private uint _range;
    private uint _cacheSize;
    private byte _cache;
    private readonly Stream _output;
    private long _bytesWritten;

    /// <summary>Total bytes written to the output stream.</summary>
    public long BytesWritten => _bytesWritten;

    /// <summary>
    /// Initializes a new range encoder writing to the specified output stream.
    /// </summary>
    public RangeEncoder(Stream output)
    {
        _output = output;
        _low = 0;
        _range = 0xFFFFFFFF;
        _cacheSize = 1;
        _cache = 0;
        _bytesWritten = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ShiftLow()
    {
        uint low = (uint)_low;
        byte highByte = (byte)(_low >> 32);

        if (low < 0xFF000000u || highByte != 0)
        {
            byte temp = _cache;
            do
            {
                _output.WriteByte((byte)(temp + highByte));
                _bytesWritten++;
                temp = 0xFF;
            }
            while (--_cacheSize != 0);
            _cache = (byte)(low >> 24);
        }
        _cacheSize++;
        _low = (ulong)(low << 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Normalize()
    {
        if (_range < kTopValue)
        {
            _range <<= 8;
            ShiftLow();
        }
    }

    /// <summary>
    /// Encodes a single bit using an adaptive probability model.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EncodeBit(ref ushort prob, uint bit)
    {
        uint bound = (_range >> kNumBitModelTotalBits) * prob;
        if (bit == 0)
        {
            _range = bound;
            prob += (ushort)((kBitModelTotal - prob) >> kNumMoveBits);
        }
        else
        {
            _low += bound;
            _range -= bound;
            prob -= (ushort)(prob >> kNumMoveBits);
        }
        Normalize();
    }

    /// <summary>
    /// Encodes bits using a fixed 0.5 probability (direct bits).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EncodeDirectBits(uint value, int numBits)
    {
        for (int i = numBits - 1; i >= 0; i--)
        {
            _range >>= 1;
            uint bit = (value >> i) & 1;
            if (bit != 0)
                _low += _range;
            Normalize();
        }
    }

    /// <summary>
    /// Encodes a value using a bit tree of the given number of bits.
    /// Value must be in [0, 2^numBits).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EncodeBitTree(ushort[] probs, int probsOffset, int numBits, uint value)
    {
        uint symbol = 1;
        for (int i = numBits - 1; i >= 0; i--)
        {
            uint bit = (value >> i) & 1;
            EncodeBit(ref probs[probsOffset + symbol], bit);
            symbol = (symbol << 1) | bit;
        }
    }

    /// <summary>
    /// Encodes a value using a reverse bit tree.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EncodeReverseBitTree(ushort[] probs, int probsOffset, int numBits, uint value)
    {
        uint symbol = 1;
        for (int i = 0; i < numBits; i++)
        {
            uint bit = value & 1;
            EncodeBit(ref probs[probsOffset + symbol], bit);
            symbol = (symbol << 1) | bit;
            value >>= 1;
        }
    }

    /// <summary>
    /// Flushes the encoder, writing all remaining bytes.
    /// Must be called after all data is encoded.
    /// </summary>
    public void FlushData()
    {
        for (int i = 0; i < 5; i++)
            ShiftLow();
    }

    /// <summary>
    /// Writes a single byte directly (not range-coded) to the output stream.
    /// Used for the LZMA initial zero byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInitByte()
    {
        _output.WriteByte(0x00);
        _bytesWritten++;
    }

    /// <summary>
    /// Resets the encoder state for a new encoding session on the same stream.
    /// </summary>
    public void Reset()
    {
        _low = 0;
        _range = 0xFFFFFFFF;
        _cacheSize = 1;
        _cache = 0;
    }

    /// <summary>
    /// Gets the number of pending bytes (not yet flushed) in the encoder.
    /// </summary>
    public long PendingBytes => _cacheSize + 1;
}
