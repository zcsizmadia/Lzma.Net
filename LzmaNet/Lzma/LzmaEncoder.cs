// SPDX-License-Identifier: 0BSD

using LzmaNet.LZ;
using LzmaNet.RangeCoder;

namespace LzmaNet.Lzma;

/// <summary>
/// LZMA1 encoder. Compresses data using LZ77 + range coding with adaptive probability models.
/// Uses hash chain match finding with lazy matching for encoding decisions.
/// </summary>
internal sealed class LzmaEncoder : IDisposable
{
    // Probability model arrays
    private readonly ushort[] _isMatch;
    private readonly ushort[] _isRep;
    private readonly ushort[] _isRepG0;
    private readonly ushort[] _isRepG1;
    private readonly ushort[] _isRepG2;
    private readonly ushort[] _isRep0Long;
    private readonly ushort[] _posSlotCoders;
    private readonly ushort[] _posSpecProbs;
    private readonly ushort[] _alignProbs;
    private readonly ushort[] _litProbs;
    private readonly ushort[] _matchLenProbs;
    private readonly ushort[] _repLenProbs;

    // Properties
    private readonly int _lc, _lp, _pb;
    private readonly int _posMask;
    private readonly int _litPosMask;

    // State
    private int _state;
    private int _rep0, _rep1, _rep2, _rep3;

    // Match finder
    private readonly HashChainMatchFinder _matchFinder;
    private readonly LzmaEncoderProperties _props;

    // Length coder offsets
    private const int kLenChoice = 0;
    private const int kLenChoice2 = 1;
    private const int kLenLow = 2;
    private int LenMid => kLenLow + (LzmaConstants.kNumPosStatesMax << LzmaConstants.kNumLowLenBits);
    private int LenHigh => LenMid + (LzmaConstants.kNumPosStatesMax << LzmaConstants.kNumMidLenBits);

    // Temp buffers for match finder results
    private readonly int[] _matchDistances = new int[LzmaConstants.kMatchMaxLen + 1];
    private readonly int[] _matchLengths = new int[LzmaConstants.kMatchMaxLen + 1];

    /// <summary>
    /// Initializes a new LZMA encoder with the given properties.
    /// </summary>
    public LzmaEncoder(LzmaEncoderProperties props)
    {
        _props = props;
        props.Validate();

        _lc = props.Lc;
        _lp = props.Lp;
        _pb = props.Pb;
        _posMask = (1 << _pb) - 1;
        _litPosMask = (1 << _lp) - 1;

        _isMatch = new ushort[LzmaConstants.kNumStates * LzmaConstants.kNumPosStatesMax];
        _isRep = new ushort[LzmaConstants.kNumStates];
        _isRepG0 = new ushort[LzmaConstants.kNumStates];
        _isRepG1 = new ushort[LzmaConstants.kNumStates];
        _isRepG2 = new ushort[LzmaConstants.kNumStates];
        _isRep0Long = new ushort[LzmaConstants.kNumStates * LzmaConstants.kNumPosStatesMax];
        _posSlotCoders = new ushort[LzmaConstants.kNumLenToPosStates * LzmaConstants.kNumPosSlots];
        _posSpecProbs = new ushort[LzmaConstants.kNumFullDistances - LzmaConstants.kEndPosModelIndex];
        _alignProbs = new ushort[LzmaConstants.kAlignTableSize];

        int numLitSubcoders = 1 << (_lc + _lp);
        _litProbs = new ushort[numLitSubcoders * LzmaConstants.kLitSubcoderSize];

        int lenProbs = 2 + (LzmaConstants.kNumPosStatesMax << LzmaConstants.kNumLowLenBits)
                         + (LzmaConstants.kNumPosStatesMax << LzmaConstants.kNumMidLenBits)
                         + (1 << LzmaConstants.kNumHighLenBits);
        _matchLenProbs = new ushort[lenProbs];
        _repLenProbs = new ushort[lenProbs];

        _matchFinder = new HashChainMatchFinder(props.DictionarySize, props.MatchMaxLen, props.CutValue);

        ResetState();
    }

    /// <summary>
    /// Resets all probability models and state.
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
        _matchFinder.Reset();
    }

    /// <summary>
    /// Encodes input data into LZMA compressed output (including the range coder init byte).
    /// This is used for LZMA2 chunk encoding.
    /// </summary>
    /// <param name="input">Uncompressed input data.</param>
    /// <param name="output">Stream to write compressed LZMA data to.</param>
    /// <returns>Number of bytes written to output.</returns>
    public long Encode(ReadOnlySpan<byte> input, Stream output)
    {
        var rc = new RangeEncoder(output);
        // The range encoder's cache mechanism (initialized with _cache=0, _cacheSize=1)
        // naturally outputs the 0x00 init byte during the first ShiftLow call.
        // Do NOT call WriteInitByte here - it would produce a duplicate 0x00 byte.

        _matchFinder.SetInput(input);
        int pos = 0;
        int inputLen = input.Length;

        while (pos < inputLen)
        {
            int available = _matchFinder.Available;
            int posState = pos & _posMask;
            if (available < 2)
            {
                // Encode remaining as literals
                rc.EncodeBit(ref _isMatch[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState], 0);
                EncodeLiteral(rc, input, input[pos], pos > 0 ? input[pos - 1] : (byte)0, pos);
                _matchFinder.MovePos();
                pos++;
                continue;
            }

            // Try to find matches
            int bestLen = 1;
            int bestDist = 0;
            bool isRep = false;
            int repIndex = -1;

            // Check rep matches first
            int maxLen = Math.Min(LzmaConstants.kMatchMaxLen, available);
            int rep0Len = GetRepMatchLen(input, pos, _rep0, maxLen);
            int rep1Len = GetRepMatchLen(input, pos, _rep1, maxLen);
            int rep2Len = GetRepMatchLen(input, pos, _rep2, maxLen);
            int rep3Len = GetRepMatchLen(input, pos, _rep3, maxLen);

            int bestRepLen = Math.Max(Math.Max(rep0Len, rep1Len), Math.Max(rep2Len, rep3Len));

            if (bestRepLen >= LzmaConstants.kMatchMinLen)
            {
                isRep = true;
                bestLen = bestRepLen;
                if (bestRepLen == rep0Len) { repIndex = 0; bestDist = _rep0; }
                else if (bestRepLen == rep1Len) { repIndex = 1; bestDist = _rep1; }
                else if (bestRepLen == rep2Len) { repIndex = 2; bestDist = _rep2; }
                else { repIndex = 3; bestDist = _rep3; }
            }

            // Find new matches
            int numMatches = _matchFinder.FindMatches(
                _matchDistances.AsSpan(), _matchLengths.AsSpan(),
                Math.Min(16, _matchDistances.Length));

            // Check if any new match is better than rep
            for (int i = 0; i < numMatches; i++)
            {
                if (_matchLengths[i] > bestLen ||
                    (_matchLengths[i] == bestLen && !isRep && _matchDistances[i] < bestDist))
                {
                    bestLen = _matchLengths[i];
                    bestDist = _matchDistances[i];
                    isRep = false;
                }
            }

            // Encode
            byte prevByte = pos > 0 ? input[pos - 1] : (byte)0;

            if (bestLen < LzmaConstants.kMatchMinLen || (bestLen == LzmaConstants.kMatchMinLen && !isRep))
            {
                // Literal
                rc.EncodeBit(ref _isMatch[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState], 0);
                EncodeLiteral(rc, input, input[pos], prevByte, pos);
                _matchFinder.MovePos();
                pos++;
            }
            else if (isRep)
            {
                // Rep match
                rc.EncodeBit(ref _isMatch[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState], 1);
                rc.EncodeBit(ref _isRep[_state], 1);
                EncodeRepMatch(rc, repIndex, bestLen, posState);
                _matchFinder.Skip(bestLen);
                pos += bestLen;
            }
            else
            {
                // Match
                rc.EncodeBit(ref _isMatch[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState], 1);
                rc.EncodeBit(ref _isRep[_state], 0);
                EncodeMatch(rc, bestDist, bestLen, posState);
                _matchFinder.Skip(bestLen);
                pos += bestLen;
            }
        }

        rc.FlushData();
        return rc.BytesWritten;
    }

    private int GetRepMatchLen(ReadOnlySpan<byte> input, int pos, int dist, int maxLen)
    {
        if (dist < 0 || pos - dist - 1 < 0)
            return 0;

        int srcPos = pos - dist - 1;
        int len = 0;
        while (len < maxLen && pos + len < input.Length && input[srcPos + len] == input[pos + len])
            len++;
        return len;
    }

    private void EncodeLiteral(RangeEncoder rc, ReadOnlySpan<byte> input, byte curByte, byte prevByte, int pos)
    {
        int litState = ((pos & _litPosMask) << _lc) + (prevByte >> (8 - _lc));
        int probsOffset = litState * LzmaConstants.kLitSubcoderSize;

        if (LzmaConstants.StateIsLiteral(_state))
        {
            EncodeNormalLiteral(rc, curByte, probsOffset);
        }
        else
        {
            // Matched literal: use byte at rep0 distance for context
            int matchPos = pos - _rep0 - 1;
            byte matchByte = matchPos >= 0 ? input[matchPos] : (byte)0;
            EncodeMatchedLiteral(rc, curByte, matchByte, probsOffset);
        }
        _state = LzmaConstants.StateUpdateLiteral(_state);
    }

    /// <summary>
    /// Encodes input data into LZMA compressed output for LZMA2.
    /// </summary>
    public long EncodeForLzma2(ReadOnlyMemory<byte> input, Stream output)
    {
        return Encode(input.Span, output);
    }

    private void EncodeNormalLiteral(RangeEncoder rc, byte curByte, int probsOffset)
    {
        uint symbol = 1;
        for (int i = 7; i >= 0; i--)
        {
            uint bit = (uint)(curByte >> i) & 1;
            rc.EncodeBit(ref _litProbs[probsOffset + symbol], bit);
            symbol = (symbol << 1) | bit;
        }
    }

    private void EncodeMatchedLiteral(RangeEncoder rc, byte curByte, byte matchByte, int probsOffset)
    {
        uint symbol = 1;
        bool matched = true;

        for (int i = 7; i >= 0; i--)
        {
            uint curBit = (uint)(curByte >> i) & 1;

            if (matched)
            {
                uint matchBit = (uint)(matchByte >> i) & 1;
                rc.EncodeBit(ref _litProbs[probsOffset + ((1 + matchBit) << 8) + symbol], curBit);
                symbol = (symbol << 1) | curBit;
                if (matchBit != curBit)
                    matched = false;
            }
            else
            {
                rc.EncodeBit(ref _litProbs[probsOffset + symbol], curBit);
                symbol = (symbol << 1) | curBit;
            }
        }
    }

    private void EncodeRepMatch(RangeEncoder rc, int repIndex, int len, int posState)
    {
        // Encode the rep index bits
        if (repIndex == 0)
        {
            rc.EncodeBit(ref _isRepG0[_state], 0);
            if (len == 1)
            {
                rc.EncodeBit(ref _isRep0Long[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState], 0);
                _state = LzmaConstants.StateUpdateShortRep(_state);
                return;
            }
            rc.EncodeBit(ref _isRep0Long[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState], 1);
        }
        else
        {
            rc.EncodeBit(ref _isRepG0[_state], 1);
            if (repIndex == 1)
            {
                rc.EncodeBit(ref _isRepG1[_state], 0);
            }
            else
            {
                rc.EncodeBit(ref _isRepG1[_state], 1);
                rc.EncodeBit(ref _isRepG2[_state], (uint)(repIndex == 3 ? 1 : 0));
            }
        }

        // Shuffle rep distances to match decoder behavior (do only once)
        if (repIndex > 0)
        {
            int dist;
            switch (repIndex)
            {
                case 1:
                    dist = _rep1;
                    _rep1 = _rep0;
                    break;
                case 2:
                    dist = _rep2;
                    _rep2 = _rep1;
                    _rep1 = _rep0;
                    break;
                default: // 3
                    dist = _rep3;
                    _rep3 = _rep2;
                    _rep2 = _rep1;
                    _rep1 = _rep0;
                    break;
            }
            _rep0 = dist;
        }

        EncodeLength(rc, _repLenProbs, len - LzmaConstants.kMatchMinLen, posState);
        _state = LzmaConstants.StateUpdateLongRep(_state);
    }

    private void EncodeMatch(RangeEncoder rc, int dist, int len, int posState)
    {
        _rep3 = _rep2;
        _rep2 = _rep1;
        _rep1 = _rep0;
        _rep0 = dist;

        EncodeLength(rc, _matchLenProbs, len - LzmaConstants.kMatchMinLen, posState);

        int lenToPosState = LzmaConstants.GetLenToPosState(len);
        int posSlot = GetPosSlot((uint)dist);

        rc.EncodeBitTree(_posSlotCoders, lenToPosState * LzmaConstants.kNumPosSlots,
                         LzmaConstants.kNumPosSlotBits, (uint)posSlot);

        if (posSlot >= LzmaConstants.kStartPosModelIndex)
        {
            int numDirectBits = (posSlot >> 1) - 1;
            uint baseVal = (uint)((2 | (posSlot & 1)) << numDirectBits);

            if (posSlot < LzmaConstants.kEndPosModelIndex)
            {
                int offset = (int)baseVal - posSlot - 1;
                rc.EncodeReverseBitTree(_posSpecProbs, offset, numDirectBits,
                                        (uint)dist - baseVal);
            }
            else
            {
                uint directPart = ((uint)dist - baseVal) >> LzmaConstants.kNumAlignBits;
                uint alignPart = (uint)dist & LzmaConstants.kAlignMask;
                rc.EncodeDirectBits(directPart, numDirectBits - LzmaConstants.kNumAlignBits);
                rc.EncodeReverseBitTree(_alignProbs, 0, LzmaConstants.kNumAlignBits, alignPart);
            }
        }

        _state = LzmaConstants.StateUpdateMatch(_state);
    }

    private void EncodeLength(RangeEncoder rc, ushort[] lenProbs, int len, int posState)
    {
        if (len < LzmaConstants.kNumLowLenSymbols)
        {
            rc.EncodeBit(ref lenProbs[kLenChoice], 0);
            rc.EncodeBitTree(lenProbs, kLenLow + (posState << LzmaConstants.kNumLowLenBits),
                            LzmaConstants.kNumLowLenBits, (uint)len);
        }
        else if (len < LzmaConstants.kNumLowLenSymbols + LzmaConstants.kNumMidLenSymbols)
        {
            rc.EncodeBit(ref lenProbs[kLenChoice], 1);
            rc.EncodeBit(ref lenProbs[kLenChoice2], 0);
            rc.EncodeBitTree(lenProbs, LenMid + (posState << LzmaConstants.kNumMidLenBits),
                            LzmaConstants.kNumMidLenBits, (uint)(len - LzmaConstants.kNumLowLenSymbols));
        }
        else
        {
            rc.EncodeBit(ref lenProbs[kLenChoice], 1);
            rc.EncodeBit(ref lenProbs[kLenChoice2], 1);
            rc.EncodeBitTree(lenProbs, LenHigh,
                            LzmaConstants.kNumHighLenBits,
                            (uint)(len - LzmaConstants.kNumLowLenSymbols - LzmaConstants.kNumMidLenSymbols));
        }
    }

    private static int GetPosSlot(uint dist)
    {
        if (dist < 4) return (int)dist;

#if NET5_0_OR_GREATER
        int bits = 31 - System.Numerics.BitOperations.LeadingZeroCount(dist);
#else
        int bits = Log2(dist);
#endif
        return (bits << 1) + (int)((dist >> (bits - 1)) & 1);
    }

#if !NET5_0_OR_GREATER
    private static int Log2(uint value)
    {
        int r = 0;
        while (value > 1) { value >>= 1; r++; }
        return r;
    }
#endif

    public void Dispose()
    {
        _matchFinder.Dispose();
    }
}
