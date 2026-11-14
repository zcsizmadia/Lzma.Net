// SPDX-License-Identifier: 0BSD

using LzmaNet.Compatibility;
using LzmaNet.LZ;
using LzmaNet.RangeCoder;

using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.InteropServices;
#else
// netstandard has no MemoryMarshal.GetArrayDataReference; see
// Compatibility/MemoryMarshal.cs.
using MemoryMarshal = LzmaNet.Compatibility.MemoryMarshal;
#endif

namespace LzmaNet.Lzma;

/// <summary>
/// LZMA1 encoder. Compresses data using LZ77 + range coding with adaptive probability models.
/// Presets 0-3 parse with liblzma's fast mode over a hash chain (EncodeChunkFast),
/// 4-9 with price-based optimal parsing over a binary tree (EncodeChunkOptimal).
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

    /// <summary>
    /// The rep-distance window as a value, so emission applies the same
    /// transitions the optimal parser predicts. See <see cref="RepDistances"/>.
    /// </summary>
    private RepDistances Reps
    {
        get => new RepDistances(_rep0, _rep1, _rep2, _rep3);
        set
        {
            _rep0 = value.Rep0;
            _rep1 = value.Rep1;
            _rep2 = value.Rep2;
            _rep3 = value.Rep3;
        }
    }

    // Match finder
    private readonly IMatchFinder _matchFinder;
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

        _matchFinder = props.UseBinaryTree
            ? new BinaryTreeMatchFinder(props.DictionarySize, props.MatchMaxLen, props.CutValue, props.NiceLength)
            : new HashChainMatchFinder(props.DictionarySize, props.MatchMaxLen, props.CutValue,
                props.NiceLength);

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
        return _props.OptimalParse
            ? EncodeChunkOptimal(block, chunkStart, chunkLen, output, sizeLimit)
            : EncodeChunkFast(block, chunkStart, chunkLen, output, sizeLimit);
    }

    // Match lists for the fast parser's one-step lookahead (the current list
    // lives in _matchDistances/_matchLengths; the two pairs swap roles).
    private int[]? _fastNextDist;
    private int[]? _fastNextLen;

    /// <summary>
    /// liblzma's fast-mode parser (<c>lzma_lzma_optimum_fast</c>), used for
    /// presets 0-3: no pricing, but rep matches are preferred when nearly as
    /// long as the main match, a one-byte-shorter match is preferred when its
    /// distance is much smaller, short far matches are dropped, and a one-step
    /// lookahead defers to the next position when it has a clearly better match.
    /// </summary>
    /// <remarks>
    /// Invariant at the top of the loop: when <c>readAhead</c> is set, the match
    /// finder sits at <c>pos</c> with its hash inserted and the current lists
    /// hold that position's matches (found during the previous lookahead).
    /// Otherwise the finder sits at <c>pos</c> without having searched it.
    /// </remarks>
    private long EncodeChunkFast(ReadOnlySpan<byte> block, int chunkStart, int chunkLen,
                                 Stream output, long sizeLimit)
    {
        var rc = new RangeEncoder(output);
        int pos = chunkStart;
        int chunkEnd = chunkStart + chunkLen;
        bool limited = sizeLimit != long.MaxValue;
        int niceLen = Math.Min(_props.NiceLength, LzmaConstants.kMatchMaxLen);

        _fastNextDist ??= new int[_matchDistances.Length];
        _fastNextLen ??= new int[_matchLengths.Length];
        int[] curDist = _matchDistances, curLen = _matchLengths;
        int[] nextDist = _fastNextDist, nextLen = _fastNextLen;
        int count = 0;
        bool readAhead = false;

        while (pos < chunkEnd)
        {
            if (ShouldAbandonChunk(rc.BytesWritten, sizeLimit, limited, pos, chunkStart))
            {
                _matchFinder.Skip(chunkEnd - pos);
                return -1;
            }

            int posState = pos & _posMask;
            // A symbol must not cross the chunk boundary (liblzma's buf_avail).
            int avail = Math.Min(chunkEnd - pos, LzmaConstants.kMatchMaxLen);

            if (!readAhead)
            {
                if (avail < 2 || _matchFinder.Available < 2)
                {
                    EmitLiteralAt(rc, block, ref pos, posState);
                    continue;
                }
                count = _matchFinder.FindMatches(curDist, curLen, curDist.Length);
            }
            readAhead = false;

            count = CapMatches(curLen, count, avail);
            int lenMain = count > 0 ? curLen[count - 1] : 0;

            // Rep matches: a long one is taken at once, otherwise remember the longest.
            int repLen = 0, repIndex = 0;
            bool done = false;
            for (int i = 0; i < 4; i++)
            {
                int dist = RepAt(i);
                int src = pos - dist - 1;
                if (dist < 0 || src < 0 || block[src] != block[pos] || block[src + 1] != block[pos + 1])
                    continue;
                int len = LZ.MatchLength.Common(block, src, pos, avail);
                if (len >= niceLen)
                {
                    EmitRep(rc, i, len, posState);
                    _matchFinder.Skip(len);
                    pos += len;
                    done = true;
                    break;
                }
                if (len > repLen)
                {
                    repIndex = i;
                    repLen = len;
                }
            }
            if (done)
                continue;

            if (lenMain >= niceLen)
            {
                EmitMatch(rc, curDist[count - 1], lenMain, posState);
                _matchFinder.Skip(lenMain);
                pos += lenMain;
                continue;
            }

            int backMain = 0;
            if (lenMain >= 2)
            {
                backMain = curDist[count - 1];
                // Prefer a match one byte shorter when its distance is much smaller.
                while (count > 1 && lenMain == curLen[count - 2] + 1)
                {
                    if (!ChangePair(curDist[count - 2], backMain))
                        break;
                    count--;
                    lenMain = curLen[count - 1];
                    backMain = curDist[count - 1];
                }
                if (lenMain == 2 && backMain >= 0x80)
                    lenMain = 1;
            }

            if (repLen >= 2
                && (repLen + 1 >= lenMain
                    || (repLen + 2 >= lenMain && backMain > (1 << 9))
                    || (repLen + 3 >= lenMain && backMain > (1 << 15))))
            {
                EmitRep(rc, repIndex, repLen, posState);
                _matchFinder.Skip(repLen);
                pos += repLen;
                continue;
            }

            if (lenMain < 2 || avail <= 2)
            {
                EmitLiteralAt(rc, block, ref pos, posState);
                continue;
            }

            // Look one position ahead; a clearly better match there means a
            // literal now (its matches carry over to the next iteration).
            _matchFinder.MovePos();
            int nextCount = CapMatches(nextLen,
                _matchFinder.FindMatches(nextDist, nextLen, nextDist.Length),
                Math.Min(chunkEnd - pos - 1, LzmaConstants.kMatchMaxLen));

            bool literal = false;
            if (nextCount > 0)
            {
                int newLen = nextLen[nextCount - 1];
                int newDist = nextDist[nextCount - 1];
                literal = (newLen >= lenMain && newDist < backMain)
                    || (newLen == lenMain + 1 && !ChangePair(backMain, newDist))
                    || newLen > lenMain + 1
                    || (newLen + 1 >= lenMain && lenMain >= 3 && ChangePair(newDist, backMain));
            }

            if (!literal)
            {
                // A rep match at the next position covering most of this match
                // also makes a literal the better choice.
                int limit = Math.Max(2, lenMain - 1);
                for (int i = 0; i < 4 && !literal; i++)
                {
                    int dist = RepAt(i);
                    int src = pos + 1 - dist - 1;
                    if (dist >= 0 && src >= 0
                        && block.Slice(src, limit).SequenceEqual(block.Slice(pos + 1, limit)))
                        literal = true;
                }
            }

            if (literal)
            {
                rc.EncodeBit(ref _isMatch[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState], 0);
                EncodeLiteral(rc, block, block[pos], pos > 0 ? block[pos - 1] : (byte)0, pos);
                pos++;
                (curDist, nextDist) = (nextDist, curDist);
                (curLen, nextLen) = (nextLen, curLen);
                count = nextCount;
                readAhead = true;
                continue;
            }

            EmitMatch(rc, backMain, lenMain, posState);
            _matchFinder.Skip(lenMain - 1); // the lookahead already moved one position
            pos += lenMain;
        }

        rc.FlushData();
        return rc.BytesWritten;
    }

    /// <summary>
    /// Whether <paramref name="bigDist"/> is so much larger than
    /// <paramref name="smallDist"/> that one byte of match length is worth
    /// giving up for the smaller one (liblzma's <c>change_pair</c>).
    /// </summary>
    private static bool ChangePair(int smallDist, int bigDist) => (bigDist >> 7) > smallDist;

    /// <summary>
    /// Clamps a match list (increasing lengths) to <paramref name="avail"/>:
    /// the first match reaching it is shortened to it and the rest dropped.
    /// </summary>
    private static int CapMatches(int[] lengths, int count, int avail)
    {
        for (int i = 0; i < count; i++)
        {
            if (lengths[i] >= avail)
            {
                lengths[i] = avail;
                return i + 1;
            }
        }
        return count;
    }

    private int RepAt(int index) => index switch
    {
        0 => _rep0,
        1 => _rep1,
        2 => _rep2,
        _ => _rep3,
    };

    private void EmitRep(RangeEncoder rc, int repIndex, int len, int posState)
    {
        rc.EncodeBit(ref _isMatch[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState], 1);
        rc.EncodeBit(ref _isRep[_state], 1);
        EncodeRepMatch(rc, repIndex, len, posState);
    }

    private void EmitMatch(RangeEncoder rc, int dist, int len, int posState)
    {
        rc.EncodeBit(ref _isMatch[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState], 1);
        rc.EncodeBit(ref _isRep[_state], 0);
        EncodeMatch(rc, dist, len, posState);
    }

    private static int GetRepMatchLen(ReadOnlySpan<byte> block, int pos, int dist, int maxLen)
    {
        if (dist < 0 || pos - dist - 1 < 0)
            return 0;

        // maxLen is already capped at the chunk boundary by the caller, so the
        // vectorized compare stays inside the block.
        int srcPos = pos - dist - 1;
        return LZ.MatchLength.Common(block, srcPos, pos, maxLen);
    }

    /// <summary>
    /// Whether the chunk should be abandoned as incompressible: the size limit is
    /// reached, or output has caught up with input after a warm-up window. The
    /// caller stores the chunk uncompressed instead, so the dictionary must still
    /// be fed past the abandoned region.
    /// </summary>
    private bool ShouldAbandonChunk(long produced, long sizeLimit, bool limited, int pos, int chunkStart)
        => produced >= sizeLimit
           || (limited && produced >= pos - chunkStart && pos - chunkStart >= 16384);

    /// <summary>
    /// Emits the byte at <paramref name="pos"/> as a literal and advances one
    /// position. Used where no match can be formed — fewer than two bytes remain,
    /// or the parse chose a literal.
    /// </summary>
    private void EmitLiteralAt(RangeEncoder rc, ReadOnlySpan<byte> block, ref int pos, int posState)
    {
        rc.EncodeBit(ref _isMatch[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState], 0);
        EncodeLiteral(rc, block, block[pos], pos > 0 ? block[pos - 1] : (byte)0, pos);
        _matchFinder.MovePos();
        pos++;
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

        Reps = Reps.AfterRepMatch(repIndex);

        EncodeLength(rc, _repLenProbs, len - LzmaConstants.kMatchMinLen, posState);
        _state = LzmaConstants.StateUpdateLongRep(_state);
    }

    private void EncodeMatch(RangeEncoder rc, int dist, int len, int posState)
    {
        Reps = Reps.AfterMatch(dist);

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

    // ── Optimal parser ───────────────────────────────────────────────
    // Price-based dynamic programming over a bounded window: every position in
    // the window is a DP node holding the cheapest known way to reach it (in
    // estimated range-coder bits) together with the LZMA state and rep
    // distances that path produces. Transitions are single symbols: literal,
    // short rep, rep match (any length), or normal match (any length, using
    // the nearest distance the match finder reported for that length).
    // Prices are estimates against the probabilities at window start (the
    // standard approximation); parse choices never affect decodability, only
    // compressed size.

    private const int kNumOpts = 1 << 10;

    // Highest node index reset to unreached in the current window; nodes past
    // it still hold the previous window's values and must not be read.
    private int _optResetEnd;

    /// <summary>
    /// Makes nodes up to <paramref name="last"/> valid (unreached) before an
    /// edge may relax into them. Every node at or below lenEnd has been
    /// reached by some edge, so it is always within the reset range.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureOptReset(OptNode[] opt, int last)
    {
        if (last > _optResetEnd)
        {
            for (int i = _optResetEnd + 1; i <= last; i++)
                opt[i].Price = kInfinityPrice;
            _optResetEnd = last;
        }
    }
    private const uint kInfinityPrice = uint.MaxValue;

    private struct OptNode
    {
        public uint Price;
        public int PosPrev;
        public int BackPrev; // -1 literal; 0..3 rep index; >= 4 match with dist = BackPrev - 4
        public int State;
        public int Rep0, Rep1, Rep2, Rep3;
    }

    private OptNode[]? _opt;
    private int[]? _optMatchDist;  // [kNumOpts * (kMatchMaxLen + 1)] candidates per window offset
    private int[]? _optMatchLen;
    private int[]? _optMatchCount;
    private int[]? _optOpsLen;     // backtracked ops
    private int[]? _optOpsBack;

    // Length prices, keyed by (coder, posState, length). Nothing is emitted
    // between the RelaxFrom calls of one dynamic-programming window, so the
    // length-coder probabilities — and therefore these prices — are fixed for
    // its duration. Each price is computed once per window instead of once per
    // edge, which is where the parser spent most of its time.
    //
    // Prices are filled per row: a (coder, posState) row holds the prices of
    // lengths 2..N, and _lenRowFilled records how far it is valid in the
    // current window. A relaxation asks for the whole length range it is about
    // to walk once, then reads the row directly — the inner loops carry no
    // per-length validity check.
    //
    // Distance prices are cached the same way, and for the same reason: the
    // slot, footer and align probabilities cannot change during a window. A
    // distance's price is slot price (per lenToPosState and slot) + footer
    // (reverse bit tree for dist < kNumFullDistances, otherwise direct bits +
    // align bits), each piece cached on first use.
    private const int kLenPriceCoders = 2;   // 0 = match lengths, 1 = rep lengths
    private const int kLenPriceMatch = 0;
    private const int kLenPriceRep = 1;
    private const int kLenRows = kLenPriceCoders * LzmaConstants.kNumPosStatesMax;
    private uint[]? _lenPrice;               // [row * kNumLenSymbols + (len - 2)]
    private int[]? _lenRowFilled;            // lengths 2.._lenRowFilled[row] are valid...
    private uint[]? _lenRowStamp;            // ...when the row's stamp is the current generation

    private uint[]? _slotPrice;              // [lenToPosState * kNumPosSlots + slot]
    private uint[]? _slotPriceStamp;
    private uint[]? _footerPrice;            // [dist], dist < kNumFullDistances
    private uint[]? _footerPriceStamp;
    private uint[]? _alignPrice;             // [dist & kAlignMask]
    private uint[]? _alignPriceStamp;

    private uint _priceGeneration = 1;       // 0 is the "never filled" stamp

    private void EnsureOptBuffers()
    {
        if (_opt != null)
            return;
        _opt = new OptNode[kNumOpts];
        _optMatchDist = new int[kNumOpts * (LzmaConstants.kMatchMaxLen + 1)];
        _optMatchLen = new int[kNumOpts * (LzmaConstants.kMatchMaxLen + 1)];
        _optMatchCount = new int[kNumOpts];
        _optOpsLen = new int[kNumOpts];
        _optOpsBack = new int[kNumOpts];

        _lenPrice = new uint[kLenRows * LzmaConstants.kNumLenSymbols];
        _lenRowFilled = new int[kLenRows];
        _lenRowStamp = new uint[kLenRows];

        _slotPrice = new uint[LzmaConstants.kPosSlotCoderSize];
        _slotPriceStamp = new uint[LzmaConstants.kPosSlotCoderSize];
        _footerPrice = new uint[LzmaConstants.kNumFullDistances];
        _footerPriceStamp = new uint[LzmaConstants.kNumFullDistances];
        _alignPrice = new uint[LzmaConstants.kAlignTableSize];
        _alignPriceStamp = new uint[LzmaConstants.kAlignTableSize];
    }

    /// <summary>
    /// Starts a new pricing window, invalidating every cached price without
    /// touching the tables.
    /// </summary>
    private void BeginPricingWindow()
    {
        if (++_priceGeneration == 0)
        {
            // Wrapped: stamps would compare equal to the new generation.
            Portable.Clear(_lenRowStamp!);
            Portable.Clear(_slotPriceStamp!);
            Portable.Clear(_footerPriceStamp!);
            Portable.Clear(_alignPriceStamp!);
            _priceGeneration = 1;
        }
    }

    /// <summary>
    /// Makes lengths 2..<paramref name="maxLen"/> of a length-price row valid
    /// for the current window and returns the row's base index in
    /// <see cref="_lenPrice"/> (offset by -2, so <c>base + len</c> indexes it).
    /// </summary>
    private int EnsureLenPrices(ushort[] lenProbs, int coder, int posState, int maxLen)
    {
        int row = (coder << LzmaConstants.kNumPosStatesBitsMax) + posState;
        int rowBase = row * LzmaConstants.kNumLenSymbols - LzmaConstants.kMatchMinLen;
        int filled = _lenRowStamp![row] == _priceGeneration
            ? _lenRowFilled![row]
            : LzmaConstants.kMatchMinLen - 1;

        if (filled < maxLen)
        {
            uint[] prices = _lenPrice!;
            for (int len = filled + 1; len <= maxLen; len++)
                prices[rowBase + len] = PriceLen(lenProbs, posState, len);
            _lenRowFilled![row] = maxLen;
            _lenRowStamp[row] = _priceGeneration;
        }
        return rowBase;
    }

    /// <summary>
    /// Price of distance <paramref name="dist"/> (slot + footer) for a match
    /// whose length maps to <paramref name="lenToPosState"/>, from the current
    /// window's caches.
    /// </summary>
    private uint PriceDistCached(int lenToPosState, int dist)
    {
        uint gen = _priceGeneration;
        int slot = GetPosSlot((uint)dist);

        int s = lenToPosState * LzmaConstants.kNumPosSlots + slot;
        if (_slotPriceStamp![s] != gen)
        {
            _slotPrice![s] = Price.GetBitTreePrice(_posSlotCoders,
                lenToPosState * LzmaConstants.kNumPosSlots, LzmaConstants.kNumPosSlotBits, (uint)slot);
            _slotPriceStamp[s] = gen;
        }
        uint price = _slotPrice![s];

        if (dist < LzmaConstants.kNumFullDistances)
        {
            if (_footerPriceStamp![dist] != gen)
            {
                _footerPrice![dist] = PriceDistFooter(dist);
                _footerPriceStamp[dist] = gen;
            }
            return price + _footerPrice![dist];
        }

        int a = dist & (int)LzmaConstants.kAlignMask;
        if (_alignPriceStamp![a] != gen)
        {
            _alignPrice![a] = Price.GetReverseBitTreePrice(_alignProbs, 0,
                LzmaConstants.kNumAlignBits, (uint)a);
            _alignPriceStamp[a] = gen;
        }
        int numDirectBits = (slot >> 1) - 1;
        return price
             + Price.GetDirectBitsPrice(numDirectBits - LzmaConstants.kNumAlignBits)
             + _alignPrice![a];
    }

    private int GatherMatches(int cur)
    {
        int stride = LzmaConstants.kMatchMaxLen + 1;
        int n = _matchFinder.FindMatches(
            _optMatchDist.AsSpan(cur * stride, stride),
            _optMatchLen.AsSpan(cur * stride, stride),
            stride);
        _optMatchCount![cur] = n;
        return n;
    }

    private long EncodeChunkOptimal(ReadOnlySpan<byte> block, int chunkStart, int chunkLen,
                                    Stream output, long sizeLimit)
    {
        var rc = new RangeEncoder(output);
        int pos = chunkStart;
        int chunkEnd = chunkStart + chunkLen;
        bool limited = sizeLimit != long.MaxValue;
        int niceLen = Math.Min(_props.NiceLength, LzmaConstants.kMatchMaxLen);
        EnsureOptBuffers();
        var opt = _opt!;
        int stride = LzmaConstants.kMatchMaxLen + 1;

        while (pos < chunkEnd)
        {
            if (ShouldAbandonChunk(rc.BytesWritten, sizeLimit, limited, pos, chunkStart))
            {
                _matchFinder.Skip(chunkEnd - pos);
                return -1;
            }

            int posState = pos & _posMask;

            if (_matchFinder.Available < 2)
            {
                EmitLiteralAt(rc, block, ref pos, posState);
                continue;
            }

            // Gather candidates at pos (window offset 0). The finder inserts the
            // position but does not advance.
            int numMatches = GatherMatches(0);
            int maxAtPos = Math.Min(LzmaConstants.kMatchMaxLen, chunkEnd - pos);

            int bestLen = 1, bestDist = 0, bestRepIndex = -1;
            bool bestIsRep = false;
            EvaluateRepCandidates(block, pos, maxAtPos, Reps,
                ref bestLen, ref bestDist, ref bestIsRep, ref bestRepIndex);
            for (int i = 0; i < numMatches; i++)
            {
                int len = Math.Min(_optMatchLen![i], maxAtPos);
                if (len > bestLen)
                {
                    bestLen = len;
                    bestDist = _optMatchDist![i];
                    bestIsRep = false;
                }
            }

            if (bestLen < LzmaConstants.kMatchMinLen)
            {
                rc.EncodeBit(ref _isMatch[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState], 0);
                EncodeLiteral(rc, block, block[pos], pos > 0 ? block[pos - 1] : (byte)0, pos);
                _matchFinder.MovePos();
                pos++;
                continue;
            }

            if (bestLen >= niceLen || bestLen == chunkEnd - pos)
            {
                // Long enough (or fills the chunk) — take it without a parse.
                EmitOp(rc, block, ref pos, bestIsRep ? bestRepIndex : bestDist + 4, bestLen,
                    literal: false);
                _matchFinder.Skip(bestLen);
                continue;
            }

            BeginPricingWindow();

            // ---- Dynamic program over the window ----
            int cap = Math.Min(kNumOpts - 1, chunkEnd - pos);
            // Nodes are reset to "unreached" only as far as edges reach (see
            // EnsureOptReset), not across the whole window up front: windows
            // are often a few dozen positions long, and clearing all 1023 nodes
            // for each one was a measurable share of encoding at nice_len 16.
            _optResetEnd = 0;
            opt[0] = new OptNode
            {
                Price = 0,
                PosPrev = -1,
                BackPrev = -2,
                State = _state,
                Rep0 = _rep0,
                Rep1 = _rep1,
                Rep2 = _rep2,
                Rep3 = _rep3,
            };

            int lenEnd = 0;
            RelaxFrom(block, pos, 0, cap, ref lenEnd);

            int lastRead = 0; // highest window offset the finder has visited
            for (int cur = 1; cur < lenEnd; cur++)
            {
                _matchFinder.MovePos();
                lastRead = cur;

                int n = 0;
                if (_matchFinder.Available >= 2)
                    n = GatherMatches(cur);
                else
                    _optMatchCount![cur] = 0;

                // Nice-length shortcut: a long match truncates the parse.
                if (n > 0)
                {
                    int longest = _optMatchLen![cur * stride + n - 1];
                    if (longest >= niceLen)
                    {
                        RelaxFrom(block, pos, cur, cap, ref lenEnd);
                        int t = Math.Min(cur + longest, cap);
                        lenEnd = t;
                        break;
                    }
                }

                RelaxFrom(block, pos, cur, cap, ref lenEnd);
            }

            // ---- Backtrack and emit ----
            int opsCount = 0;
            int node = lenEnd;
            while (node > 0)
            {
                _optOpsLen![opsCount] = node - opt[node].PosPrev;
                _optOpsBack![opsCount] = opt[node].BackPrev;
                opsCount++;
                node = opt[node].PosPrev;
            }

            for (int i = opsCount - 1; i >= 0; i--)
            {
                int back = _optOpsBack![i];
                int len = _optOpsLen![i];
                EmitOp(rc, block, ref pos, back == -1 ? -1 : back, len, literal: back == -1);
            }

            // Sync the finder: positions pos0..pos0+lastRead were visited; the
            // path consumed lenEnd bytes total.
            _matchFinder.Skip(lenEnd - lastRead);
        }

        rc.FlushData();
        return rc.BytesWritten;
    }

    /// <summary>Emits one parsed operation, updating encoder state and reps.</summary>
    private void EmitOp(RangeEncoder rc, ReadOnlySpan<byte> block, ref int pos,
                        int back, int len, bool literal)
    {
        int posState = pos & _posMask;
        if (literal)
        {
            rc.EncodeBit(ref _isMatch[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState], 0);
            EncodeLiteral(rc, block, block[pos], pos > 0 ? block[pos - 1] : (byte)0, pos);
            pos++;
            return;
        }

        rc.EncodeBit(ref _isMatch[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState], 1);
        if (back < 4)
        {
            rc.EncodeBit(ref _isRep[_state], 1);
            EncodeRepMatch(rc, back, len, posState);
        }
        else
        {
            rc.EncodeBit(ref _isRep[_state], 0);
            EncodeMatch(rc, back - 4, len, posState);
        }
        pos += len;
    }

    private void EvaluateRepCandidates(ReadOnlySpan<byte> block, int absPos, int maxLen,
        RepDistances reps,
        ref int bestLen, ref int bestDist, ref bool bestIsRep, ref int bestRepIndex)
    {
        for (int i = 0; i < 4; i++)
        {
            int len = GetRepMatchLen(block, absPos, reps[i], maxLen);
            if (len >= LzmaConstants.kMatchMinLen && len > bestLen)
            {
                bestLen = len;
                bestDist = reps[i];
                bestIsRep = true;
                bestRepIndex = i;
            }
        }
    }

    /// <summary>
    /// Relaxes all outgoing edges of DP node <paramref name="cur"/>.
    /// </summary>
    private void RelaxFrom(ReadOnlySpan<byte> block, int pos0, int cur, int cap, ref int lenEnd)
    {
        var opt = _opt!;
        ref OptNode from = ref opt[cur];
        if (from.Price == kInfinityPrice)
            return;

        // Relaxation records only the price and the edge (PosPrev, BackPrev);
        // the state and reps a node starts from follow from its final best edge,
        // so they are derived here, once, when the parse reaches the node. Every
        // edge points forward and nodes are reached in order, so the predecessor
        // is final by now — this is what liblzma does, and it keeps the
        // relaxation loops to three stores per improved node.
        if (cur > 0)
            DeriveNodeState(opt, ref from, cur);

        int absPos = pos0 + cur;
        int posState = absPos & _posMask;
        int state = from.State;
        uint cp = from.Price;

        uint isMatchProb = _isMatch[(state << LzmaConstants.kNumPosStatesBitsMax) + posState];
        uint price0 = cp + Price.GetPrice0(isMatchProb);
        uint priceMatchBit = cp + Price.GetPrice1(isMatchProb);

        // Literal
        if (cur + 1 <= cap)
        {
            EnsureOptReset(opt, cur + 1); // also covers the short rep below
            uint litPrice = price0 + PriceLiteral(block, absPos, state, from.Rep0);
            ref OptNode to = ref opt[cur + 1];
            if (litPrice < to.Price)
            {
                to.Price = litPrice;
                to.PosPrev = cur;
                to.BackPrev = -1;
                if (cur + 1 > lenEnd) lenEnd = cur + 1;
            }
        }

        uint priceRep = priceMatchBit + Price.GetPrice1(_isRep[state]);

        // Short rep (rep0, length 1)
        if (cur + 1 <= cap && from.Rep0 >= 0 && from.Rep0 < absPos
            && block[absPos - from.Rep0 - 1] == block[absPos])
        {
            uint p = priceRep
                + Price.GetPrice0(_isRepG0[state])
                + Price.GetPrice0(_isRep0Long[(state << LzmaConstants.kNumPosStatesBitsMax) + posState]);
            ref OptNode to = ref opt[cur + 1];
            if (p < to.Price)
            {
                to.Price = p;
                to.PosPrev = cur;
                to.BackPrev = 0; // rep0, len 1 == short rep
                if (cur + 1 > lenEnd) lenEnd = cur + 1;
            }
        }

        int maxLen = Math.Min(LzmaConstants.kMatchMaxLen, cap - cur);
        if (maxLen < LzmaConstants.kMatchMinLen)
            return;

        // Rep matches (all lengths)
        var reps = new RepDistances(from.Rep0, from.Rep1, from.Rep2, from.Rep3);
        // Both inner loops index only cur + len <= cap < kNumOpts and length rows
        // filled up to the length they walk, so they skip the bounds checks.
        ref uint lenPriceRef = ref MemoryMarshal.GetArrayDataReference(_lenPrice!);
        ref OptNode optRef = ref MemoryMarshal.GetArrayDataReference(opt);
        for (int i = 0; i < 4; i++)
        {
            // Two-byte pre-check (as liblzma does) before the full compare:
            // most reps do not match here at all.
            int repDist = reps[i];
            int src = absPos - repDist - 1;
            if (repDist < 0 || src < 0
                || block[src] != block[absPos] || block[src + 1] != block[absPos + 1])
                continue;

            int repLen = GetRepMatchLen(block, absPos, repDist, maxLen);
            if (repLen < LzmaConstants.kMatchMinLen)
                continue;

            EnsureOptReset(opt, cur + repLen);
            uint prefix = priceRep + PriceRepIndexBits(i, state, posState);
            int rowBase = EnsureLenPrices(_repLenProbs, kLenPriceRep, posState, repLen);

            for (int len = LzmaConstants.kMatchMinLen; len <= repLen; len++)
            {
                uint p = prefix + Unsafe.Add(ref lenPriceRef, rowBase + len);
                ref OptNode to = ref Unsafe.Add(ref optRef, cur + len);
                if (p < to.Price)
                {
                    to.Price = p;
                    to.PosPrev = cur;
                    to.BackPrev = i;
                    if (cur + len > lenEnd) lenEnd = cur + len;
                }
            }
        }

        // Normal matches (all lengths; nearest distance per length tier)
        int count = _optMatchCount![cur];
        if (count == 0)
            return;

        uint priceMatch = priceMatchBit + Price.GetPrice0(_isRep[state]);
        int strideBase = cur * (LzmaConstants.kMatchMaxLen + 1);
        int startLen = LzmaConstants.kMatchMinLen;

        // Fill the match-length prices once for the longest length any
        // candidate will price.
        int longest = 0;
        for (int k = 0; k < count; k++)
            longest = Math.Max(longest, _optMatchLen![strideBase + k]);
        longest = Math.Min(longest, maxLen);
        EnsureOptReset(opt, cur + longest);
        int matchRowBase = longest >= startLen
            ? EnsureLenPrices(_matchLenProbs, kLenPriceMatch, posState, longest)
            : 0;

        for (int k = 0; k < count; k++)
        {
            int candLen = Math.Min(_optMatchLen![strideBase + k], maxLen);
            int dist = _optMatchDist![strideBase + k];
            if (candLen < startLen)
                continue;

            // The distance price depends on the length only through
            // lenToPosState, which is constant from length 5 up.
            uint distPriceLong = PriceDistCached(LzmaConstants.kNumLenToPosStates - 1, dist);

            for (int len = startLen; len <= candLen; len++)
            {
                uint distPrice = len < LzmaConstants.kMatchMinLen + LzmaConstants.kNumLenToPosStates - 1
                    ? PriceDistCached(LzmaConstants.GetLenToPosState(len), dist)
                    : distPriceLong;

                uint p = priceMatch + Unsafe.Add(ref lenPriceRef, matchRowBase + len) + distPrice;
                ref OptNode to = ref Unsafe.Add(ref optRef, cur + len);
                if (p < to.Price)
                {
                    to.Price = p;
                    to.PosPrev = cur;
                    to.BackPrev = dist + 4;
                    if (cur + len > lenEnd) lenEnd = cur + len;
                }
            }
            startLen = candLen + 1;
        }
    }

    /// <summary>
    /// Sets a reached node's LZMA state and rep distances from its best edge
    /// (see <see cref="RelaxFrom"/>).
    /// </summary>
    private static void DeriveNodeState(OptNode[] opt, ref OptNode node, int cur)
    {
        ref OptNode prev = ref opt[node.PosPrev];
        int back = node.BackPrev;
        var reps = new RepDistances(prev.Rep0, prev.Rep1, prev.Rep2, prev.Rep3);
        if (back == -1)
        {
            node.State = LzmaConstants.StateUpdateLiteral(prev.State);
        }
        else if (back < 4)
        {
            if (cur - node.PosPrev == 1)
            {
                node.State = LzmaConstants.StateUpdateShortRep(prev.State); // rep0, length 1
            }
            else
            {
                node.State = LzmaConstants.StateUpdateLongRep(prev.State);
                reps = reps.AfterRepMatch(back);
            }
        }
        else
        {
            node.State = LzmaConstants.StateUpdateMatch(prev.State);
            reps = reps.AfterMatch(back - 4);
        }
        node.Rep0 = reps.Rep0;
        node.Rep1 = reps.Rep1;
        node.Rep2 = reps.Rep2;
        node.Rep3 = reps.Rep3;
    }

    // ── Price helpers ────────────────────────────────────────────────

    private uint PriceLiteral(ReadOnlySpan<byte> block, int absPos, int state, int rep0)
    {
        byte prevByte = absPos > 0 ? block[absPos - 1] : (byte)0;
        int litState = ((absPos & _litPosMask) << _lc) + (prevByte >> (8 - _lc));
        int offset = litState * LzmaConstants.kLitSubcoderSize;
        byte symbol = block[absPos];

        uint price = 0;
        uint m = 1;
        if (!LzmaConstants.StateIsLiteral(state) && rep0 >= 0 && rep0 < absPos)
        {
            byte matchByte = block[absPos - rep0 - 1];
            for (int i = 7; i >= 0; i--)
            {
                uint matchBit = (uint)(matchByte >> i) & 1;
                uint bit = (uint)(symbol >> i) & 1;
                price += Price.GetPrice(_litProbs[offset + ((1 + matchBit) << 8) + m], bit);
                m = (m << 1) | bit;
                if (matchBit != bit)
                {
                    i--;
                    for (; i >= 0; i--)
                    {
                        bit = (uint)(symbol >> i) & 1;
                        price += Price.GetPrice(_litProbs[offset + m], bit);
                        m = (m << 1) | bit;
                    }
                    return price;
                }
            }
            return price;
        }

        for (int i = 7; i >= 0; i--)
        {
            uint bit = (uint)(symbol >> i) & 1;
            price += Price.GetPrice(_litProbs[offset + m], bit);
            m = (m << 1) | bit;
        }
        return price;
    }

    private uint PriceLen(ushort[] lenProbs, int posState, int len)
    {
        int l = len - LzmaConstants.kMatchMinLen;
        if (l < LzmaConstants.kNumLowLenSymbols)
        {
            return Price.GetPrice0(lenProbs[kLenChoice])
                 + Price.GetBitTreePrice(lenProbs, kLenLow + (posState << LzmaConstants.kNumLowLenBits),
                     LzmaConstants.kNumLowLenBits, (uint)l);
        }
        if (l < LzmaConstants.kNumLowLenSymbols + LzmaConstants.kNumMidLenSymbols)
        {
            return Price.GetPrice1(lenProbs[kLenChoice])
                 + Price.GetPrice0(lenProbs[kLenChoice2])
                 + Price.GetBitTreePrice(lenProbs, LenMid + (posState << LzmaConstants.kNumMidLenBits),
                     LzmaConstants.kNumMidLenBits,
                     (uint)(l - LzmaConstants.kNumLowLenSymbols));
        }
        return Price.GetPrice1(lenProbs[kLenChoice])
             + Price.GetPrice1(lenProbs[kLenChoice2])
             + Price.GetBitTreePrice(lenProbs, LenHigh, LzmaConstants.kNumHighLenBits,
                 (uint)(l - LzmaConstants.kNumLowLenSymbols - LzmaConstants.kNumMidLenSymbols));
    }

    private uint PriceRepIndexBits(int repIndex, int state, int posState)
    {
        if (repIndex == 0)
        {
            return Price.GetPrice0(_isRepG0[state])
                 + Price.GetPrice1(_isRep0Long[(state << LzmaConstants.kNumPosStatesBitsMax) + posState]);
        }
        uint price = Price.GetPrice1(_isRepG0[state]);
        if (repIndex == 1)
            return price + Price.GetPrice0(_isRepG1[state]);
        price += Price.GetPrice1(_isRepG1[state]);
        return price + Price.GetPrice(_isRepG2[state], (uint)(repIndex == 3 ? 1 : 0));
    }

    private uint PriceDistFooter(int dist)
    {
        int slot = GetPosSlot((uint)dist);
        if (slot < LzmaConstants.kStartPosModelIndex)
            return 0;

        int numDirectBits = (slot >> 1) - 1;
        uint baseVal = (uint)((2 | (slot & 1)) << numDirectBits);
        if (slot < LzmaConstants.kEndPosModelIndex)
        {
            return Price.GetReverseBitTreePrice(_posSpecProbs, (int)baseVal - slot - 1,
                numDirectBits, (uint)dist - baseVal);
        }
        return Price.GetDirectBitsPrice(numDirectBits - LzmaConstants.kNumAlignBits)
             + Price.GetReverseBitTreePrice(_alignProbs, 0, LzmaConstants.kNumAlignBits,
                 (uint)dist & LzmaConstants.kAlignMask);
    }

    public void Dispose()
    {
        _matchFinder.Dispose();
    }
}
