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
            ? new BinaryTreeMatchFinder(props.DictionarySize, props.MatchMaxLen, props.CutValue)
            : new HashChainMatchFinder(props.DictionarySize, props.MatchMaxLen, props.CutValue);

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
        if (_props.OptimalParse)
            return EncodeChunkOptimal(block, chunkStart, chunkLen, output, sizeLimit);

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

        int niceLen = Math.Min(_props.NiceLength, LzmaConstants.kMatchMaxLen);

        // Lazy matching: when a match candidate is found at pos, the next
        // position is also evaluated; if it has a strictly longer match, a
        // literal is emitted instead and the pos+1 candidate carries over to
        // the next iteration (naturally chaining into multi-step laziness).
        // Invariant at the top of the loop when pendingValid: the match finder
        // sits at pos with its hash already inserted by FindMatches.
        bool pendingValid = false;
        int pendingLen = 0, pendingDist = 0, pendingRepIndex = -1;
        bool pendingIsRep = false;

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

            int posState = pos & _posMask;
            int bestLen, bestDist, repIndex;
            bool isRep;

            if (pendingValid)
            {
                bestLen = pendingLen;
                bestDist = pendingDist;
                isRep = pendingIsRep;
                repIndex = pendingRepIndex;
                pendingValid = false;
            }
            else
            {
                if (_matchFinder.Available < 2)
                {
                    // Encode remaining as literals
                    rc.EncodeBit(ref _isMatch[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState], 0);
                    EncodeLiteral(rc, block, block[pos], pos > 0 ? block[pos - 1] : (byte)0, pos);
                    _matchFinder.MovePos();
                    pos++;
                    continue;
                }

                EvaluatePosition(block, pos, chunkEnd, out bestLen, out bestDist, out isRep, out repIndex);
            }

            if (bestLen < LzmaConstants.kMatchMinLen || (bestLen == LzmaConstants.kMatchMinLen && !isRep))
            {
                // Literal
                rc.EncodeBit(ref _isMatch[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState], 0);
                EncodeLiteral(rc, block, block[pos], pos > 0 ? block[pos - 1] : (byte)0, pos);
                _matchFinder.MovePos();
                pos++;
                continue;
            }

            // Lazy one-step lookahead: matches at or beyond niceLen are taken
            // immediately; short ones are only kept if pos+1 has nothing longer.
            bool deferred = false;
            bool lookedAhead = false;
            if (bestLen < niceLen && chunkEnd - pos >= 2)
            {
                _matchFinder.MovePos(); // to pos+1 (hash at pos already inserted)
                lookedAhead = true;

                if (_matchFinder.Available >= 2)
                {
                    EvaluatePosition(block, pos + 1, chunkEnd,
                        out int nextLen, out int nextDist, out bool nextIsRep, out int nextRepIndex);
                    if (nextLen > bestLen)
                    {
                        // Emit a literal at pos; carry the pos+1 candidate over.
                        rc.EncodeBit(ref _isMatch[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState], 0);
                        EncodeLiteral(rc, block, block[pos], pos > 0 ? block[pos - 1] : (byte)0, pos);
                        pos++;
                        pendingLen = nextLen;
                        pendingDist = nextDist;
                        pendingIsRep = nextIsRep;
                        pendingRepIndex = nextRepIndex;
                        pendingValid = true;
                        deferred = true;
                    }
                }
            }

            if (deferred)
                continue;

            // Emit the match/rep at pos. If lookahead advanced the finder to
            // pos+1 (with its hash inserted when Available allowed), skip one
            // position less.
            int skip = lookedAhead ? bestLen - 1 : bestLen;
            if (isRep)
            {
                rc.EncodeBit(ref _isMatch[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState], 1);
                rc.EncodeBit(ref _isRep[_state], 1);
                EncodeRepMatch(rc, repIndex, bestLen, posState);
            }
            else
            {
                rc.EncodeBit(ref _isMatch[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState], 1);
                rc.EncodeBit(ref _isRep[_state], 0);
                EncodeMatch(rc, bestDist, bestLen, posState);
            }
            _matchFinder.Skip(skip);
            pos += bestLen;
        }

        rc.FlushData();
        return rc.BytesWritten;
    }

    /// <summary>
    /// Evaluates the best match candidate (rep or normal) at the given position.
    /// Runs FindMatches, which inserts the position into the hash/chain tables.
    /// Lengths are capped at the chunk boundary: a single symbol must not span
    /// two LZMA2 chunks.
    /// </summary>
    private void EvaluatePosition(ReadOnlySpan<byte> block, int pos, int chunkEnd,
        out int bestLen, out int bestDist, out bool isRep, out int repIndex)
    {
        bestLen = 1;
        bestDist = 0;
        isRep = false;
        repIndex = -1;

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

        int numMatches = _matchFinder.FindMatches(
            _matchDistances.AsSpan(), _matchLengths.AsSpan(),
            Math.Min(16, _matchDistances.Length));

        for (int i = 0; i < numMatches; i++)
        {
            // The finder may see past the chunk boundary (its window is fed the
            // whole block); a symbol must not, so clamp the usable length.
            int len = Math.Min(_matchLengths[i], maxLen);
            if (len > bestLen ||
                (len == bestLen && !isRep && _matchDistances[i] < bestDist))
            {
                bestLen = len;
                bestDist = _matchDistances[i];
                isRep = false;
            }
        }
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
            long producedBytes = rc.BytesWritten;
            if (producedBytes >= sizeLimit
                || (limited && producedBytes >= pos - chunkStart && pos - chunkStart >= 16384))
            {
                _matchFinder.Skip(chunkEnd - pos);
                return -1;
            }

            int posState = pos & _posMask;

            if (_matchFinder.Available < 2)
            {
                rc.EncodeBit(ref _isMatch[(_state << LzmaConstants.kNumPosStatesBitsMax) + posState], 0);
                EncodeLiteral(rc, block, block[pos], pos > 0 ? block[pos - 1] : (byte)0, pos);
                _matchFinder.MovePos();
                pos++;
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

            // ---- Dynamic program over the window ----
            int cap = Math.Min(kNumOpts - 1, chunkEnd - pos);
            for (int i = 1; i <= cap; i++)
                opt[i].Price = kInfinityPrice;
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
            uint litPrice = price0 + PriceLiteral(block, absPos, state, from.Rep0);
            ref OptNode to = ref opt[cur + 1];
            if (litPrice < to.Price)
            {
                to.Price = litPrice;
                to.PosPrev = cur;
                to.BackPrev = -1;
                to.State = LzmaConstants.StateUpdateLiteral(state);
                to.Rep0 = from.Rep0;
                to.Rep1 = from.Rep1;
                to.Rep2 = from.Rep2;
                to.Rep3 = from.Rep3;
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
                to.State = LzmaConstants.StateUpdateShortRep(state);
                to.Rep0 = from.Rep0;
                to.Rep1 = from.Rep1;
                to.Rep2 = from.Rep2;
                to.Rep3 = from.Rep3;
                if (cur + 1 > lenEnd) lenEnd = cur + 1;
            }
        }

        int maxLen = Math.Min(LzmaConstants.kMatchMaxLen, cap - cur);
        if (maxLen < LzmaConstants.kMatchMinLen)
            return;

        // Rep matches (all lengths)
        var reps = new RepDistances(from.Rep0, from.Rep1, from.Rep2, from.Rep3);
        for (int i = 0; i < 4; i++)
        {
            int repLen = GetRepMatchLen(block, absPos, reps[i], maxLen);
            if (repLen < LzmaConstants.kMatchMinLen)
                continue;

            uint prefix = priceRep + PriceRepIndexBits(i, state, posState);
            int newState = LzmaConstants.StateUpdateLongRep(state);
            var afterRep = reps.AfterRepMatch(i);

            for (int len = LzmaConstants.kMatchMinLen; len <= repLen; len++)
            {
                uint p = prefix + PriceLen(_repLenProbs, posState, len);
                ref OptNode to = ref opt[cur + len];
                if (p < to.Price)
                {
                    to.Price = p;
                    to.PosPrev = cur;
                    to.BackPrev = i;
                    to.State = newState;
                    to.Rep0 = afterRep.Rep0;
                    to.Rep1 = afterRep.Rep1;
                    to.Rep2 = afterRep.Rep2;
                    to.Rep3 = afterRep.Rep3;
                    if (cur + len > lenEnd) lenEnd = cur + len;
                }
            }
        }

        // Normal matches (all lengths; nearest distance per length tier)
        int count = _optMatchCount![cur];
        if (count == 0)
            return;

        uint priceMatch = priceMatchBit + Price.GetPrice0(_isRep[state]);
        int newStateMatch = LzmaConstants.StateUpdateMatch(state);
        int strideBase = cur * (LzmaConstants.kMatchMaxLen + 1);
        int startLen = LzmaConstants.kMatchMinLen;
        Span<uint> slotPrice = stackalloc uint[LzmaConstants.kNumLenToPosStates];

        for (int k = 0; k < count; k++)
        {
            int candLen = Math.Min(_optMatchLen![strideBase + k], maxLen);
            int dist = _optMatchDist![strideBase + k];
            if (candLen < startLen)
                continue;

            // Distance footer price is length-independent; the slot-tree price
            // depends only on lenToPosState (4 values), cached lazily.
            uint footer = PriceDistFooter(dist);
            var afterMatch = reps.AfterMatch(dist);
            slotPrice.Fill(kInfinityPrice);

            for (int len = startLen; len <= candLen; len++)
            {
                int lps = LzmaConstants.GetLenToPosState(len);
                if (slotPrice[lps] == kInfinityPrice)
                    slotPrice[lps] = PriceDistSlot(lps, dist);

                uint p = priceMatch + PriceLen(_matchLenProbs, posState, len)
                       + slotPrice[lps] + footer;
                ref OptNode to = ref opt[cur + len];
                if (p < to.Price)
                {
                    to.Price = p;
                    to.PosPrev = cur;
                    to.BackPrev = dist + 4;
                    to.State = newStateMatch;
                    to.Rep0 = afterMatch.Rep0;
                    to.Rep1 = afterMatch.Rep1;
                    to.Rep2 = afterMatch.Rep2;
                    to.Rep3 = afterMatch.Rep3;
                    if (cur + len > lenEnd) lenEnd = cur + len;
                }
            }
            startLen = candLen + 1;
        }
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

    private uint PriceDistSlot(int lenToPosState, int dist)
    {
        int slot = GetPosSlot((uint)dist);
        return Price.GetBitTreePrice(_posSlotCoders, lenToPosState * LzmaConstants.kNumPosSlots,
            LzmaConstants.kNumPosSlotBits, (uint)slot);
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
