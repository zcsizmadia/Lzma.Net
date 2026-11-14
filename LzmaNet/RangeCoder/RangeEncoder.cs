// SPDX-License-Identifier: 0BSD

using System.Runtime.CompilerServices;

namespace LzmaNet.RangeCoder;

/// <summary>
/// LZMA range encoder. Encodes bits into a range-coded bitstream
/// using adaptive probability models with 11-bit precision.
/// Output bytes are buffered internally to avoid a virtual
/// <see cref="Stream.WriteByte"/> call per compressed byte.
/// </summary>
internal sealed class RangeEncoder
{
    private const int kNumBitModelTotalBits = RangeDecoder.kNumBitModelTotalBits;
    private const uint kBitModelTotal = RangeDecoder.kBitModelTotal;
    private const int kNumMoveBits = RangeDecoder.kNumMoveBits;
    private const uint kTopValue = 1u << 24;
    private const int kOutBufferSize = 4096;

    private ulong _low;
    private uint _range;
    private uint _cacheSize;
    private byte _cache;
    private readonly Stream _output;
    private readonly byte[] _outBuffer = new byte[kOutBufferSize];
    private int _outPos;
    private long _bytesWritten;

    /// <summary>Total bytes written (including bytes still in the internal buffer).</summary>
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
    private void WriteByteBuffered(byte b)
    {
        if (_outPos == kOutBufferSize)
            FlushBuffer();
        _outBuffer[_outPos++] = b;
        _bytesWritten++;
    }

    private void FlushBuffer()
    {
        if (_outPos > 0)
        {
            _output.Write(_outBuffer, 0, _outPos);
            _outPos = 0;
        }
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
                WriteByteBuffered((byte)(temp + highByte));
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
        // Branch-free: literal, length and distance bits are close to random,
        // so a branch on the bit mispredicts often. mask is all ones for bit 1.
        // (Callers passing a constant bit get the folded single-path code.)
        uint p = prob;
        uint bound = (_range >> kNumBitModelTotalBits) * p;
        uint mask = 0u - bit;
        _low += bound & mask;
        uint rest = _range - bound;
        _range = bound ^ ((bound ^ rest) & mask);
        // bit 0: p += (2048 - p) >> 5; bit 1: p -= p >> 5 — as one expression
        // (see LzmaDecoder.TreeBit).
        prob = (ushort)(p - ((p + (~mask & kBitModelOffset)) >> kNumMoveBits));
        Normalize();
    }

    /// <summary>-kBitModelTotal with the low kNumMoveBits bits set, as a wrapping uint32.</summary>
    private const uint kBitModelOffset = unchecked((1u << kNumMoveBits) - 1 - kBitModelTotal);

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
    /// Flushes the encoder, writing all remaining bytes to the output stream.
    /// Must be called after all data is encoded.
    /// </summary>
    public void FlushData()
    {
        for (int i = 0; i < 5; i++)
            ShiftLow();
        FlushBuffer();
    }

    /// <summary>
    /// Writes a single byte directly (not range-coded) to the output.
    /// Used for the LZMA initial zero byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInitByte()
    {
        WriteByteBuffered(0x00);
    }

    /// <summary>
    /// Resets the encoder state for a new encoding session on the same stream.
    /// Any buffered bytes from a previous session are flushed first.
    /// </summary>
    public void Reset()
    {
        FlushBuffer();
        _low = 0;
        _range = 0xFFFFFFFF;
        _cacheSize = 1;
        _cache = 0;
    }

    /// <summary>
    /// Gets the number of pending bytes (not yet produced by the coder) in the encoder.
    /// </summary>
    public long PendingBytes => _cacheSize + 1;
}
