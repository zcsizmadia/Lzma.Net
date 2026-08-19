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
    /// Resets all probability models, LZMA state, and rep distances.
    /// Does NOT reset the match-finder dictionary — use <see cref="ResetDictionary"/>
    /// for that. LZMA2 state resets (control 0xA0/0xC0) keep the dictionary.
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
    /// Resets the match-finder dictionary. Call at the start of an independent
    /// encoding unit (an XZ block); LZMA2 chunks within a block share the dictionary.
    /// </summary>
    public void ResetDictionary()
    {
        _matchFinder.Reset();
    }

    /// <summary>
    /// Appends data to the match-finder window without encoding it yet.
    /// </summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        _matchFinder.SetInput(data);
    }

    /// <summary>
    /// Encodes input data as one complete, independent LZMA stream
    /// (including the range coder init byte). Resets state and dictionary.
    /// </summary>
    /// <param name="input">Uncompressed input data.</param>
    /// <param name="output">Stream to write compressed LZMA data to.</param>
    /// <returns>Number of bytes written to output.</returns>
    public long Encode(ReadOnlySpan<byte> input, Stream output)
    {
        ResetState();
        ResetDictionary();
        Append(input);
        return EncodeChunk(input, 0, input.Length, output);
    }

    /// <summary>
    /// Encodes one LZMA2 chunk of a larger block. The dictionary (match finder),
    /// probability models, and rep distances carry over from previous chunks, so
    /// matches may reference data from earlier chunks in the same block.
    /// The chunk's data must already have been supplied via <see cref="Append"/>.
    /// </summary>
    /// <param name="block">The full block being encoded (for look-back reads).</param>
    /// <param name="chunkStart">Position in <paramref name="block"/> where this chunk starts.</param>
    /// <param name="chunkLen">Number of bytes to encode.</param>
    /// <param name="output">Stream to write compressed LZMA data to (fresh range coder per chunk).</param>
    /// <param name="sizeLimit">Abort once this many compressed bytes have been produced
    /// (the caller will store the chunk uncompressed instead). The encoder's probability
    /// state is garbage after an abort — the caller must reset state before the next
    /// chunk, which LZMA2 requires after an uncompressed chunk anyway.</param>
    /// <returns>Number of bytes written to output, or -1 if <paramref name="sizeLimit"/>
    /// was exceeded (partial output was written and must be discarded).</returns>
    public long EncodeChunk(ReadOnlySpan<byte> block, int chunkStart, int chunkLen, Stream output,
                            long sizeLimit = long.MaxValue)
    {
        var rc = new RangeEncoder(output);
        // The range encoder's cache mechanism (initialized with _cache=0, _cacheSize=1)
        // naturally outputs the 0x00 init byte during the first ShiftLow call.
        // Do NOT call WriteInitByte here - it would produce a duplicate 0x00 byte.

        int pos = chunkStart;
        int chunkEnd = chunkStart + chunkLen;

        // With a size limit set, also abort once the chunk is demonstrably
        // expanding (output has caught up with input after a warm-up window):
        // incompressible chunks are then abandoned after ~16 KB instead of
        // being fully range-coded and thrown away.
        bool limited = sizeLimit != long.MaxValue;

        while (pos < chunkEnd)
        {
            long produced = rc.BytesWritten;
            if (produced >= sizeLimit
                || (limited && produced >= pos - chunkStart && pos - chunkStart >= 16384))
            {
                // Incompressible: give up early, but keep feeding the dictionary
                // so later chunks can still match against this data.
                _matchFinder.Skip(chunkEnd - pos);
                return -1;
            }

            int available = _matchFinder.Available;
            int posState = pos & _posMask;
            if (available < 2)
            {
                // Encode remaining as literals
                rc.EncodeBit(ref _isMatch[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState], 0);
                EncodeLiteral(rc, block, block[pos], pos > 0 ? block[pos - 1] : (byte)0, pos);
                _matchFinder.MovePos();
                pos++;
                continue;
            }

            // Try to find matches
            int bestLen = 1;
            int bestDist = 0;
            bool isRep = false;
            int repIndex = -1;

            // Check rep matches first. Lengths are capped at the chunk boundary:
            // a single symbol must not span two LZMA2 chunks.
            int maxLen = Math.Min(LzmaConstants.kMatchMaxLen, chunkEnd - pos);
            int rep0Len = GetRepMatchLen(block, pos, _rep0, maxLen);
            int rep1Len = GetRepMatchLen(block, pos, _rep1, maxLen);
            int rep2Len = GetRepMatchLen(block, pos, _rep2, maxLen);
            int rep3Len = GetRepMatchLen(block, pos, _rep3, maxLen);

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
            byte prevByte = pos > 0 ? block[pos - 1] : (byte)0;

            if (bestLen < LzmaConstants.kMatchMinLen || (bestLen == LzmaConstants.kMatchMinLen && !isRep))
            {
                // Literal
                rc.EncodeBit(ref _isMatch[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState], 0);
                EncodeLiteral(rc, block, block[pos], prevByte, pos);
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

    private static int GetRepMatchLen(ReadOnlySpan<byte> block, int pos, int dist, int maxLen)
    {
        if (dist < 0 || pos - dist - 1 < 0)
            return 0;

        // maxLen is already capped at the chunk boundary by the caller.
        int srcPos = pos - dist - 1;
        int len = 0;
        while (len < maxLen && block[srcPos + len] == block[pos + len])
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

        int bits = 31 - System.Numerics.BitOperations.LeadingZeroCount(dist);
        return (bits << 1) + (int)((dist >> (bits - 1)) & 1);
    }

    public void Dispose()
    {
        _matchFinder.Dispose();
    }
}
