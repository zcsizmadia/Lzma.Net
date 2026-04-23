// SPDX-License-Identifier: 0BSD

using LzmaNet.RangeCoder;

namespace LzmaNet.Lzma;

/// <summary>
/// LZMA1 decoder. Decodes a range-coded LZMA bitstream into uncompressed data
/// using a sliding-window dictionary.
/// </summary>
internal sealed class LzmaDecoder
{
    // Probability model arrays
    private readonly ushort[] _isMatch;      // [state][posState]
    private readonly ushort[] _isRep;         // [state]
    private readonly ushort[] _isRepG0;       // [state]
    private readonly ushort[] _isRepG1;       // [state]
    private readonly ushort[] _isRepG2;       // [state]
    private readonly ushort[] _isRep0Long;    // [state][posState]
    private readonly ushort[] _posSlotCoders; // [lenToPosState][posSlot]
    private readonly ushort[] _posSpecProbs;  // position-specific bit trees for distance 4..127
    private readonly ushort[] _alignProbs;    // alignment bits (4 bits)
    private readonly ushort[] _litProbs;      // literal sub-coders

    // Length decoders (match length and rep length)
    private readonly ushort[] _matchLenProbs;
    private readonly ushort[] _repLenProbs;

    // Properties
    private int _lc, _lp, _pb;
    private int _posMask;
    private int _litPosMask;

    // State
    private int _state;
    private int _rep0, _rep1, _rep2, _rep3;

    // Range decoder
    private RangeDecoder _rc;

    // Layout offsets for length coder probs:
    // [0] = choice, [1] = choice2
    // [2..2+posStatesMax*8) = low coders
    // [2+posStatesMax*8..2+posStatesMax*8+posStatesMax*8) = mid coders
    // [2+2*posStatesMax*8..] = high coder (256 probs)
    private const int kLenChoice = 0;
    private const int kLenChoice2 = 1;
    private const int kLenLow = 2;

    private int LenMid => kLenLow + (LzmaConstants.kNumPosStatesMax << LzmaConstants.kNumLowLenBits);
    private int LenHigh => LenMid + (LzmaConstants.kNumPosStatesMax << LzmaConstants.kNumMidLenBits);

    /// <summary>
    /// Initializes a new LZMA decoder with the given properties.
    /// </summary>
    /// <param name="lc">Number of literal context bits (0-8).</param>
    /// <param name="lp">Number of literal position bits (0-4).</param>
    /// <param name="pb">Number of position bits (0-4).</param>
    public LzmaDecoder(int lc, int lp, int pb)
    {
        _lc = lc;
        _lp = lp;
        _pb = pb;
        _posMask = (1 << pb) - 1;
        _litPosMask = (1 << lp) - 1;

        int numPosStates = 1 << pb;

        _isMatch = new ushort[LzmaConstants.kNumStates * LzmaConstants.kNumPosStatesMax];
        _isRep = new ushort[LzmaConstants.kNumStates];
        _isRepG0 = new ushort[LzmaConstants.kNumStates];
        _isRepG1 = new ushort[LzmaConstants.kNumStates];
        _isRepG2 = new ushort[LzmaConstants.kNumStates];
        _isRep0Long = new ushort[LzmaConstants.kNumStates * LzmaConstants.kNumPosStatesMax];

        _posSlotCoders = new ushort[LzmaConstants.kNumLenToPosStates * LzmaConstants.kNumPosSlots];
        _posSpecProbs = new ushort[LzmaConstants.kNumFullDistances - LzmaConstants.kEndPosModelIndex];
        _alignProbs = new ushort[LzmaConstants.kAlignTableSize];

        int numLitSubcoders = 1 << (lc + lp);
        _litProbs = new ushort[numLitSubcoders * LzmaConstants.kLitSubcoderSize];

        int lenProbs = 2 + (LzmaConstants.kNumPosStatesMax << LzmaConstants.kNumLowLenBits)
                         + (LzmaConstants.kNumPosStatesMax << LzmaConstants.kNumMidLenBits)
                         + (1 << LzmaConstants.kNumHighLenBits);
        _matchLenProbs = new ushort[lenProbs];
        _repLenProbs = new ushort[lenProbs];

        ResetState();
    }

    /// <summary>
    /// Resets all probability models and state to initial values.
    /// </summary>
    public void ResetState()
    {
        _state = 0;
        _rep0 = _rep1 = _rep2 = _rep3 = 0;
        RangeDecoder.InitProbs(_isMatch);
        RangeDecoder.InitProbs(_isRep);
        RangeDecoder.InitProbs(_isRepG0);
        RangeDecoder.InitProbs(_isRepG1);
        RangeDecoder.InitProbs(_isRepG2);
        RangeDecoder.InitProbs(_isRep0Long);
        RangeDecoder.InitProbs(_posSlotCoders);
        RangeDecoder.InitProbs(_posSpecProbs);
        RangeDecoder.InitProbs(_alignProbs);
        RangeDecoder.InitProbs(_litProbs);
        RangeDecoder.InitProbs(_matchLenProbs);
        RangeDecoder.InitProbs(_repLenProbs);
    }

    /// <summary>
    /// Updates the properties without reallocating (only if lc+lp unchanged).
    /// If lc+lp changes, litProbs must be resized — use a new decoder instead.
    /// </summary>
    public void SetProperties(int lc, int lp, int pb)
    {
        _lc = lc;
        _lp = lp;
        _pb = pb;
        _posMask = (1 << pb) - 1;
        _litPosMask = (1 << lp) - 1;
    }

    /// <summary>
    /// Decodes LZMA data from the input buffer into the output dictionary.
    /// </summary>
    /// <param name="input">The compressed input data (range-coded LZMA stream).</param>
    /// <param name="inputOffset">Starting offset in the input; the 5-byte range coder
    /// init header must begin here.</param>
    /// <param name="window">The output dictionary window.</param>
    /// <param name="output">Output buffer to receive decompressed bytes.</param>
    /// <param name="outPos">Current write position in output; updated on return.</param>
    /// <param name="uncompressedSize">Number of uncompressed bytes to decode.</param>
    public void Decode(ReadOnlyMemory<byte> input, int inputOffset,
                       OutputWindow window, Span<byte> output, ref int outPos,
                       long uncompressedSize)
    {
        _rc.Init(input, inputOffset);
        long remaining = uncompressedSize;

        while (remaining > 0)
        {
            int posState = (int)(window.TotalPos & _posMask);

            if (_rc.DecodeBit(ref _isMatch[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState]) == 0)
            {
                // Literal
                byte prevByte = window.TotalPos > 0 ? window.GetByte(0) : (byte)0;
                byte litByte = DecodeLiteral(prevByte, window);
                window.PutByte(litByte);
                output[outPos++] = litByte;
                _state = LzmaConstants.StateUpdateLiteral(_state);
                remaining--;
            }
            else
            {
                int len;
                if (_rc.DecodeBit(ref _isRep[_state]) != 0)
                {
                    // Rep match
                    if (_rc.DecodeBit(ref _isRepG0[_state]) == 0)
                    {
                        // Rep0
                        if (_rc.DecodeBit(ref _isRep0Long[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState]) == 0)
                        {
                            // Short rep (single byte at rep0 distance)
                            if (!window.HasDistance(_rep0))
                                throw new LzmaDataErrorException("Invalid distance in short rep.");
                            byte b = window.GetByte(_rep0);
                            window.PutByte(b);
                            output[outPos++] = b;
                            _state = LzmaConstants.StateUpdateShortRep(_state);
                            remaining--;
                            continue;
                        }
                        // Long rep0
                    }
                    else
                    {
                        int dist;
                        if (_rc.DecodeBit(ref _isRepG1[_state]) == 0)
                        {
                            dist = _rep1;
                        }
                        else
                        {
                            if (_rc.DecodeBit(ref _isRepG2[_state]) == 0)
                            {
                                dist = _rep2;
                            }
                            else
                            {
                                dist = _rep3;
                                _rep3 = _rep2;
                            }
                            _rep2 = _rep1;
                        }
                        _rep1 = _rep0;
                        _rep0 = dist;
                    }

                    len = DecodeLength(_repLenProbs, posState);
                    _state = LzmaConstants.StateUpdateLongRep(_state);
                }
                else
                {
                    // Match
                    _rep3 = _rep2;
                    _rep2 = _rep1;
                    _rep1 = _rep0;

                    len = DecodeLength(_matchLenProbs, posState);
                    int distSlot = DecodeDistSlot(LzmaConstants.GetLenToPosState(len + LzmaConstants.kMatchMinLen));
                    _rep0 = DecodeDistance(distSlot);
                    _state = LzmaConstants.StateUpdateMatch(_state);
                }

                len += LzmaConstants.kMatchMinLen;

                if (!window.HasDistance(_rep0))
                    throw new LzmaDataErrorException("Invalid match distance.");

                window.CopyMatch(_rep0, len, output, ref outPos);
                remaining -= len;
            }
        }
    }

    /// <summary>
    /// Decodes LZMA data for LZMA2 usage (separate range coder init, known chunk sizes).
    /// The range coder must already be initialized.
    /// </summary>
    public void DecodeLzma2Chunk(ref RangeDecoder rc, OutputWindow window,
                                  Span<byte> output, ref int outPos,
                                  int uncompressedSize)
    {
        _rc = rc;
        int remaining = uncompressedSize;

        while (remaining > 0)
        {
            int posState = (int)(window.TotalPos & _posMask);

            if (_rc.DecodeBit(ref _isMatch[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState]) == 0)
            {
                byte prevByte = window.TotalPos > 0 ? window.GetByte(0) : (byte)0;
                byte litByte = DecodeLiteral(prevByte, window);
                window.PutByte(litByte);
                output[outPos++] = litByte;
                _state = LzmaConstants.StateUpdateLiteral(_state);
                remaining--;
            }
            else
            {
                int len;
                if (_rc.DecodeBit(ref _isRep[_state]) != 0)
                {
                    if (_rc.DecodeBit(ref _isRepG0[_state]) == 0)
                    {
                        if (_rc.DecodeBit(ref _isRep0Long[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState]) == 0)
                        {
                            if (!window.HasDistance(_rep0))
                                throw new LzmaDataErrorException("Invalid distance in short rep.");
                            byte b = window.GetByte(_rep0);
                            window.PutByte(b);
                            output[outPos++] = b;
                            _state = LzmaConstants.StateUpdateShortRep(_state);
                            remaining--;
                            continue;
                        }
                    }
                    else
                    {
                        int dist;
                        if (_rc.DecodeBit(ref _isRepG1[_state]) == 0)
                        {
                            dist = _rep1;
                        }
                        else
                        {
                            if (_rc.DecodeBit(ref _isRepG2[_state]) == 0)
                            {
                                dist = _rep2;
                            }
                            else
                            {
                                dist = _rep3;
                                _rep3 = _rep2;
                            }
                            _rep2 = _rep1;
                        }
                        _rep1 = _rep0;
                        _rep0 = dist;
                    }

                    len = DecodeLength(_repLenProbs, posState);
                    _state = LzmaConstants.StateUpdateLongRep(_state);
                }
                else
                {
                    _rep3 = _rep2;
                    _rep2 = _rep1;
                    _rep1 = _rep0;

                    len = DecodeLength(_matchLenProbs, posState);
                    int distSlot = DecodeDistSlot(LzmaConstants.GetLenToPosState(len + LzmaConstants.kMatchMinLen));
                    _rep0 = DecodeDistance(distSlot);
                    _state = LzmaConstants.StateUpdateMatch(_state);
                }

                len += LzmaConstants.kMatchMinLen;
                if (!window.HasDistance(_rep0))
                    throw new LzmaDataErrorException("Invalid match distance.");
                window.CopyMatch(_rep0, len, output, ref outPos);
                remaining -= len;
            }
        }

        rc = _rc;
    }

    private byte DecodeLiteral(byte prevByte, OutputWindow window)
    {
        int litState = (((int)window.TotalPos & _litPosMask) << _lc) + (prevByte >> (8 - _lc));
        int probsOffset = litState * LzmaConstants.kLitSubcoderSize;

        uint symbol = 1;

        if (!LzmaConstants.StateIsLiteral(_state))
        {
            // Matched literal: use match byte for context
            byte matchByte = window.GetByte(_rep0);

            do
            {
                uint matchBit = (uint)(matchByte >> 7) & 1;
                matchByte <<= 1;
                uint bit = _rc.DecodeBit(ref _litProbs[probsOffset + ((1 + matchBit) << 8) + symbol]);
                symbol = (symbol << 1) | bit;
                if (matchBit != bit)
                    break;
            } while (symbol < 0x100);
        }

        // Normal literal decoding (or finishing after match divergence)
        while (symbol < 0x100)
        {
            symbol = (symbol << 1) | _rc.DecodeBit(ref _litProbs[probsOffset + symbol]);
        }

        return (byte)(symbol & 0xFF);
    }

    private int DecodeLength(ushort[] lenProbs, int posState)
    {
        if (_rc.DecodeBit(ref lenProbs[kLenChoice]) == 0)
        {
            // Low
            return (int)_rc.DecodeBitTree(lenProbs, kLenLow + (posState << LzmaConstants.kNumLowLenBits),
                                          LzmaConstants.kNumLowLenBits);
        }
        if (_rc.DecodeBit(ref lenProbs[kLenChoice2]) == 0)
        {
            // Mid
            return LzmaConstants.kNumLowLenSymbols
                + (int)_rc.DecodeBitTree(lenProbs, LenMid + (posState << LzmaConstants.kNumMidLenBits),
                                         LzmaConstants.kNumMidLenBits);
        }
        // High
        return LzmaConstants.kNumLowLenSymbols + LzmaConstants.kNumMidLenSymbols
            + (int)_rc.DecodeBitTree(lenProbs, LenHigh, LzmaConstants.kNumHighLenBits);
    }

    private int DecodeDistSlot(int lenToPosState)
    {
        return (int)_rc.DecodeBitTree(_posSlotCoders, lenToPosState * LzmaConstants.kNumPosSlots,
                                      LzmaConstants.kNumPosSlotBits);
    }

    private int DecodeDistance(int distSlot)
    {
        if (distSlot < LzmaConstants.kStartPosModelIndex)
            return distSlot;

        int numDirectBits = (distSlot >> 1) - 1;
        uint dist = (uint)((2 | (distSlot & 1)) << numDirectBits);

        if (distSlot < LzmaConstants.kEndPosModelIndex)
        {
            // Use position-specific bit tree
            int offset = (int)dist - distSlot - 1;
            dist += _rc.DecodeReverseBitTree(_posSpecProbs, offset, numDirectBits);
        }
        else
        {
            // Direct bits + alignment bits
            dist += _rc.DecodeDirectBits(numDirectBits - LzmaConstants.kNumAlignBits)
                     << LzmaConstants.kNumAlignBits;
            dist += _rc.DecodeReverseBitTree(_alignProbs, 0, LzmaConstants.kNumAlignBits);
        }

        return (int)dist;
    }
}
