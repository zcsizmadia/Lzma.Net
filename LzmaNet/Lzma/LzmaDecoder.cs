// SPDX-License-Identifier: 0BSD

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using LzmaNet.RangeCoder;

#if !NET8_0_OR_GREATER
// netstandard2.1 has no MemoryMarshal.GetArrayDataReference; see
// Compatibility/MemoryMarshal.cs.
using MemoryMarshal = LzmaNet.Compatibility.MemoryMarshal;
#endif

namespace LzmaNet.Lzma;

/// <summary>
/// LZMA1 decoder. Decodes a range-coded LZMA bitstream into uncompressed data.
/// The output buffer itself serves as the sliding-window dictionary: every block
/// starts with a dictionary reset and the caller provides a buffer that holds the
/// entire decoded block, so match look-back reads directly from already-decoded
/// output. This halves memory write bandwidth versus a separate dictionary buffer
/// and removes all circular-buffer arithmetic from the hot loop.
/// </summary>
internal sealed class LzmaDecoder
{
    /// <summary>
    /// Every probability model in one array, at the fixed offsets below (literal
    /// coders last, since their count depends on lc + lp).
    /// </summary>
    /// <remarks>
    /// One array means one base reference in the decode loop. With a separate
    /// array per model the loop held twelve base references live alongside the
    /// range coder, LZMA state and rep distances — more than x64 has registers —
    /// and the JIT spilled and reloaded them inside the bit-tree loops. liblzma
    /// keeps its models in one struct for the same reason.
    /// </remarks>
    private readonly ushort[] _probs;

    private const int kIsMatch = 0;                                                          // [state][posState]
    private const int kIsRep = kIsMatch + LzmaConstants.kNumStates * LzmaConstants.kNumPosStatesMax; // [state]
    private const int kIsRepG0 = kIsRep + LzmaConstants.kNumStates;                          // [state]
    private const int kIsRepG1 = kIsRepG0 + LzmaConstants.kNumStates;                        // [state]
    private const int kIsRepG2 = kIsRepG1 + LzmaConstants.kNumStates;                        // [state]
    private const int kIsRep0Long = kIsRepG2 + LzmaConstants.kNumStates;                     // [state][posState]
    private const int kPosSlot = kIsRep0Long + LzmaConstants.kNumStates * LzmaConstants.kNumPosStatesMax; // [lenToPosState][posSlot]
    private const int kPosSpec = kPosSlot + LzmaConstants.kNumLenToPosStates * LzmaConstants.kNumPosSlots; // distances 4..127
    private const int kAlign = kPosSpec + LzmaConstants.kNumFullDistances - LzmaConstants.kEndPosModelIndex;
    private const int kMatchLen = kAlign + LzmaConstants.kAlignTableSize;
    private const int kLenCoderProbs = 2 + (LzmaConstants.kNumPosStatesMax << LzmaConstants.kNumLowLenBits)
                                         + (LzmaConstants.kNumPosStatesMax << LzmaConstants.kNumMidLenBits)
                                         + (1 << LzmaConstants.kNumHighLenBits);
    private const int kRepLen = kMatchLen + kLenCoderProbs;
    private const int kLiteral = kRepLen + kLenCoderProbs;                                   // literal sub-coders

    // Properties
    private int _lc, _lp, _pb;
    private int _posMask;
    private int _litPosMask;

    // State
    private int _state;
    private int _rep0, _rep1, _rep2, _rep3;

    // Layout offsets for length coder probs:
    // [0] = choice, [1] = choice2
    // [2..2+posStatesMax*8) = low coders
    // [2+posStatesMax*8..2+posStatesMax*8+posStatesMax*8) = mid coders
    // [2+2*posStatesMax*8..] = high coder (256 probs)
    private const int kLenChoice = 0;
    private const int kLenChoice2 = 1;
    private const int kLenLow = 2;

    private const int kLenMid = kLenLow + (LzmaConstants.kNumPosStatesMax << LzmaConstants.kNumLowLenBits);
    private const int kLenHigh = kLenMid + (LzmaConstants.kNumPosStatesMax << LzmaConstants.kNumMidLenBits);

    /// <summary>Number of literal context bits + literal position bits used to size
    /// the literal coders in <see cref="_probs"/>; a decoder can only be reused for the same lc+lp sum.</summary>
    public int LcLp => _lc + _lp;

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

        int numLitSubcoders = 1 << (lc + lp);
        _probs = new ushort[kLiteral + numLitSubcoders * LzmaConstants.kLitSubcoderSize];

        ResetState();
    }

    /// <summary>
    /// Resets all probability models and state to initial values.
    /// </summary>
    public void ResetState()
    {
        _state = 0;
        _rep0 = _rep1 = _rep2 = _rep3 = 0;
        RangeDecoder.InitProbs(_probs);
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
    /// Decodes LZMA data from the input buffer into the output buffer.
    /// The output buffer serves as the dictionary window.
    /// </summary>
    /// <param name="input">The compressed input data (range-coded LZMA stream).</param>
    /// <param name="inputOffset">Starting offset in the input; the 5-byte range coder
    /// init header must begin here.</param>
    /// <param name="output">Output buffer to receive decompressed bytes.</param>
    /// <param name="outPos">Current write position in output; updated on return.</param>
    /// <param name="uncompressedSize">Number of uncompressed bytes to decode.</param>
    public void Decode(ReadOnlyMemory<byte> input, int inputOffset,
                       Span<byte> output, ref int outPos,
                       long uncompressedSize)
    {
        if (uncompressedSize < 0 || uncompressedSize > output.Length - outPos)
            throw new LzmaDataErrorException("Output buffer is too small for the LZMA data.");

        var rc = new RangeDecoder();
        rc.Init(input.Span, inputOffset);
        DecodeChunk(ref rc, output, ref outPos, 0, (int)uncompressedSize);
    }

    /// <summary>
    /// Decodes one LZMA chunk (with an already-initialized range coder) directly into
    /// the output buffer, which also serves as the dictionary window.
    /// </summary>
    /// <param name="rc">Initialized range decoder positioned at the chunk's coded data.</param>
    /// <param name="output">Output buffer; bytes [dictStart, outPos) form the current dictionary.</param>
    /// <param name="outPos">Current write position in output; advanced by
    /// <paramref name="uncompressedSize"/> on success.</param>
    /// <param name="dictStart">Position in output where the current dictionary began
    /// (the last dictionary-reset point).</param>
    /// <param name="uncompressedSize">Exact number of bytes this chunk decodes to.</param>
    public void DecodeChunk(ref RangeDecoder rc, Span<byte> output, ref int outPos,
                            int dictStart, int uncompressedSize)
    {
        DecodeCore(ref rc, output, ref outPos, dictStart, uncompressedSize,
            exactSize: true, allowEndMarker: false);
    }

    /// <summary>
    /// Decodes until the LZMA end marker (distance 0xFFFFFFFF) or until at least
    /// <paramref name="softTarget"/> more bytes have been decoded, whichever comes
    /// first. Used for the legacy .lzma format with an unknown uncompressed size.
    /// The output buffer must have at least <see cref="LzmaConstants.kMatchMaxLen"/>
    /// bytes of slack beyond <paramref name="softTarget"/>, because the final match
    /// may overshoot the soft target.
    /// </summary>
    /// <returns>True when the end marker was reached.</returns>
    public bool DecodeWithEndMarker(ref RangeDecoder rc, Span<byte> output, ref int outPos,
                                    int dictStart, int softTarget)
    {
        return DecodeCore(ref rc, output, ref outPos, dictStart, softTarget,
            exactSize: false, allowEndMarker: true);
    }

    /// <summary>
    /// Range-coder state lifted out of <see cref="RangeDecoder"/> for the hot loop.
    /// </summary>
    /// <remarks>
    /// <see cref="RangeDecoder"/> is passed by reference, so its fields live in
    /// memory and every bit decoded through it loads and stores
    /// <c>range</c>/<c>code</c>/position. Copying them into this small local struct, and
    /// inlining every bit, bit-tree, length and distance decode, lets the JIT
    /// promote the fields to registers for the whole loop — the same discipline
    /// liblzma's <c>rc_*</c> macros get from C locals. The input is passed alongside
    /// as a <c>ref byte</c> with <see cref="Limit"/> as its length, so a
    /// normalization is one compare and one load.
    /// </remarks>
    private struct Rc
    {
        public uint Range;
        public uint Code;
        public int Pos;
        public int Limit;
    }

    private const uint kTopValue = 1u << 24;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Bit(ref Rc rc, ref byte input, ref ushort prob)
    {
        // Each branch normalizes and returns a constant, so after inlining the
        // JIT threads the caller's test of the result into this compare instead
        // of materializing the bit and branching on it a second time.
        uint p = prob;
        uint bound = (rc.Range >> RangeDecoder.kNumBitModelTotalBits) * p;
        if (rc.Code < bound)
        {
            rc.Range = bound;
            prob = (ushort)(p + ((RangeDecoder.kBitModelTotal - p) >> RangeDecoder.kNumMoveBits));
            Normalize(ref rc, ref input);
            return 0;
        }
        rc.Range -= bound;
        rc.Code -= bound;
        prob = (ushort)(p - (p >> RangeDecoder.kNumMoveBits));
        Normalize(ref rc, ref input);
        return 1;
    }

    /// <summary>
    /// <see cref="Bit"/> without a data-dependent branch, for bits whose value
    /// the CPU cannot predict: the inside of bit trees (literal bits, lengths,
    /// distance slots). The decoded bit becomes a mask that selects the new
    /// range, code and probability arithmetically, as liblzma's x86-64 range
    /// decoder does with conditional moves. Decision bits (is-match, is-rep,
    /// ...) keep <see cref="Bit"/>: the code branches on them anyway.
    /// </summary>
    /// <remarks>
    /// <c>p</c> is the value of <c>prob</c>, loaded by the caller ahead of time
    /// (see <see cref="BitTree"/>).
    /// </remarks>
    /// <returns>All ones when the decoded bit is 0, zero when it is 1; the bit
    /// itself is the return value plus one.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint TreeBit(ref Rc rc, ref byte input, ref ushort prob, uint p)
    {
        // Selects are done with a mask rather than ternaries: the JIT does not
        // if-convert inside loops, so ternaries would compile back to branches.
        uint bound = (rc.Range >> RangeDecoder.kNumBitModelTotalBits) * p;
        // mask0 is all ones when the bit is 0 (code < bound), else zero. The
        // sign of a 64-bit subtraction gives it in two instructions (sub, sar);
        // a compare would need cmp, setae, movzx and neg on the critical path
        // that every following bit waits for.
        uint mask0 = (uint)(((long)rc.Code - bound) >> 63);
        // bit 0: range = bound;         code unchanged
        // bit 1: range = range - bound; code -= bound
        uint rb = rc.Range - bound;
        rc.Range = rb ^ ((rb ^ bound) & mask0);
        rc.Code -= bound & ~mask0;
        // liblzma's single-expression update: bit 1 is p -= p >> 5; bit 0 is
        // p += (2048 - p) >> 5, which equals p -= (p + kBitModelOffset) >> 5 in
        // wrapping 32-bit arithmetic (the shifted value's high bits cancel in
        // the 16-bit store).
        prob = (ushort)(p - ((p + (mask0 & kBitModelOffset)) >> RangeDecoder.kNumMoveBits));
        Normalize(ref rc, ref input);
        return mask0;
    }

    /// <summary>
    /// -kBitModelTotal with the low kNumMoveBits bits set, as a wrapping uint32
    /// (liblzma's RC_BIT_MODEL_OFFSET); see <see cref="TreeBit"/>.
    /// </summary>
    private const uint kBitModelOffset = unchecked((1u << RangeDecoder.kNumMoveBits) - 1 - RangeDecoder.kBitModelTotal);

    /// <summary>
    /// Loads the probabilities at <paramref name="index"/> and
    /// <paramref name="index"/> + 1 — the two children of a bit-tree node — in
    /// one read, for <see cref="SelectChild"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint LoadPair(ref ushort probs, uint index)
    {
        uint pair = Unsafe.ReadUnaligned<uint>(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref probs, (nint)index)));
        return BitConverter.IsLittleEndian ? pair : (pair >> 16) | (pair << 16);
    }

    /// <summary>
    /// Picks the child probability from a <see cref="LoadPair"/> result: the
    /// first (left, bit 0) when <paramref name="mask0"/> is all ones.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SelectChild(uint pair, uint mask0)
        => (pair >> (int)(~mask0 & 16)) & 0xFFFF;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Normalize(ref Rc rc, ref byte input)
    {
        if (rc.Range < kTopValue)
        {
            if ((uint)rc.Pos >= (uint)rc.Limit)
                ThrowTruncated();
            rc.Range <<= 8;
            rc.Code = (rc.Code << 8) | Unsafe.Add(ref input, rc.Pos++);
        }
    }

    /// <summary>
    /// Decodes a bit tree: returns the symbol with its leading 1 still set, in
    /// [<paramref name="limit"/>, 2 * <paramref name="limit"/>).
    /// </summary>
    /// <remarks>
    /// Each step loads both children of the current node before its bit is
    /// decoded and then selects one by the bit, as liblzma's x86-64 bit-tree
    /// decoder does. Otherwise the next probability's load (a few cycles of L1
    /// latency) would wait for the bit, then delay the next multiply, on every
    /// bit. The last step's children lie past the tree and their values are
    /// unused; the reads stay inside <see cref="_probs"/>, because every tree
    /// is followed by other models except the literal coders, whose 8-bit tree
    /// (children up to 0x1FF) sits inside a 0x300-entry coder.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint BitTree(ref Rc rc, ref byte input, ref ushort probs, uint limit)
    {
        uint symbol = 1;
        uint p = Unsafe.Add(ref probs, 1);
        do
        {
            uint pair = LoadPair(ref probs, symbol << 1);
            uint mask0 = TreeBit(ref rc, ref input, ref Unsafe.Add(ref probs, (nint)symbol), p);
            symbol = (symbol << 1) + 1 + mask0;
            p = SelectChild(pair, mask0);
        } while (symbol < limit);
        return symbol;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReverseBitTree(ref Rc rc, ref byte input, ref ushort probs, int numBits)
    {
        uint symbol = 1;
        uint result = 0;
        uint p = Unsafe.Add(ref probs, 1);
        for (int i = 0; i < numBits; i++)
        {
            uint pair = LoadPair(ref probs, symbol << 1);
            uint bit = TreeBit(ref rc, ref input, ref Unsafe.Add(ref probs, (nint)symbol), p) + 1;
            symbol = (symbol << 1) | bit;
            result |= bit << i;
            p = SelectChild(pair, 0u - (bit ^ 1));
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint DirectBits(ref Rc rc, ref byte input, int numBits)
    {
        uint result = 0;
        for (int i = numBits; i > 0; i--)
        {
            rc.Range >>= 1;
            uint t = (rc.Code - rc.Range) >> 31; // 1 if code < range, 0 otherwise
            rc.Code -= rc.Range & (t - 1);       // subtract range if t == 0
            result = (result << 1) | (1 - t);
            Normalize(ref rc, ref input);
        }
        return result;
    }

    // Throw-only and deliberately not NoInlining: the JIT then treats the call
    // sites as no-return and keeps the range-coder state in registers across them.
    private static void ThrowTruncated()
        => throw new LzmaDataErrorException("Unexpected end of range-coded data.");

    private bool DecodeCore(ref RangeDecoder rangeDecoder, Span<byte> output, ref int outPos,
                            int dictStart, int uncompressedSize,
                            bool exactSize, bool allowEndMarker)
    {
        int slack = exactSize ? 0 : LzmaConstants.kMatchMaxLen;
        if ((uint)dictStart > (uint)outPos
            || uncompressedSize < 0
            || uncompressedSize > output.Length - outPos - slack)
        {
            throw new LzmaDataErrorException("Output buffer is too small for the LZMA data.");
        }

        // Register budget. x64 has 16 general registers and this loop wants more
        // than that, so the JIT must keep something on the stack. Only the
        // values touched on every bit are locals: the range coder, the LZMA
        // state, rep0, the position and the three base references. rep1..rep3
        // live in their fields and are read and written there directly (only
        // rep matches and new matches touch them), and the lc/lp/pb masks are
        // read from fields per symbol. Positions are relative to the window
        // start, so dictStart is not live either.
        int state = _state;
        int rep0 = _rep0;

        // A matched literal at the start of a continuation chunk consumes rep0 before
        // any match validates it; reject a stale rep0 that reaches outside the window.
        if (state >= 7 && (rep0 < 0 || rep0 >= outPos - dictStart))
            throw new LzmaDataErrorException("Invalid rep distance at chunk start.");

        // The window is win[0, p), with win = output[dictStart..]; writes stay
        // below end (plus the end-marker slack checked above). Every read at
        // p - dist - 1 is preceded by a distance check against p — for a matched
        // literal, rep0 was validated by the match that set it or by the
        // chunk-start check above — so the unchecked accesses stay inside the
        // span.
        ref byte win = ref Unsafe.Add(ref MemoryMarshal.GetReference(output), dictStart);
        int p = outPos - dictStart;
        int end = p + uncompressedSize;

        ReadOnlySpan<byte> inputSpan = rangeDecoder.Buffer;
        ref byte input = ref MemoryMarshal.GetReference(inputSpan);
        var rc = new Rc
        {
            Range = rangeDecoder.Range,
            Code = rangeDecoder.Code,
            Pos = rangeDecoder.Position,
            Limit = inputSpan.Length,
        };

        ref ushort probs = ref MemoryMarshal.GetArrayDataReference(_probs);

        while (p < end)
        {
            int posState = p & _posMask;

            if (Bit(ref rc, ref input, ref Unsafe.Add(ref probs, kIsMatch +
                    (state << LzmaConstants.kNumPosStatesBitsMax) + posState)) == 0)
            {
                // Literal
                uint prevByte = p > 0 ? Unsafe.Add(ref win, p - 1) : 0u;
                int lc = _lc;
                int litState = ((p & _litPosMask) << lc) + (int)(prevByte >> (8 - lc));
                ref ushort litSub = ref Unsafe.Add(ref probs, kLiteral + litState * LzmaConstants.kLitSubcoderSize);

                uint symbol;
                if (state >= 7) // !StateIsLiteral: matched literal, use match byte for context
                {
                    // Branchless matched literal (LZMA SDK form). While the
                    // decoded bits agree with the match byte, offs is 0x100 and
                    // the probability index is 0x100/0x200 + symbol (by match
                    // bit); at the first disagreement offs drops to 0 and the
                    // index becomes the plain literal coder's symbol. Same
                    // probabilities as the branchy two-phase loop, without its
                    // unpredictable early exit.
                    //
                    // Unlike BitTree, the next probability is not preloaded:
                    // its two candidates are not adjacent, and the extra
                    // index arithmetic spilled registers in this loop
                    // (measured ~1% slower overall).
                    uint matchByte = Unsafe.Add(ref win, p - rep0 - 1);
                    uint offs = 0x100;
                    symbol = 1;
                    do
                    {
                        matchByte <<= 1;
                        uint matchBit = matchByte & offs;
                        ref ushort prob = ref Unsafe.Add(ref litSub, (nint)(offs + matchBit + symbol));
                        uint mask0 = TreeBit(ref rc, ref input, ref prob, prob);
                        symbol = (symbol << 1) + 1 + mask0;
                        offs &= matchBit ^ mask0;
                    } while (symbol < 0x100);
                }
                else
                {
                    symbol = BitTree(ref rc, ref input, ref litSub, 0x100);
                }

                Unsafe.Add(ref win, p++) = (byte)symbol;
                state = LzmaConstants.StateUpdateLiteral(state);
                continue;
            }

            int len;
            if (Bit(ref rc, ref input, ref Unsafe.Add(ref probs, kIsRep + state)) != 0)
            {
                // Rep match
                if (Bit(ref rc, ref input, ref Unsafe.Add(ref probs, kIsRepG0 + state)) == 0)
                {
                    // Rep0
                    if (Bit(ref rc, ref input, ref Unsafe.Add(ref probs, kIsRep0Long +
                            (state << LzmaConstants.kNumPosStatesBitsMax) + posState)) == 0)
                    {
                        // Short rep (single byte at rep0 distance)
                        if (rep0 >= p)
                            throw new LzmaDataErrorException("Invalid distance in short rep.");
                        Unsafe.Add(ref win, p) = Unsafe.Add(ref win, p - rep0 - 1);
                        p++;
                        state = LzmaConstants.StateUpdateShortRep(state);
                        continue;
                    }
                    // Long rep0
                }
                else
                {
                    int dist;
                    if (Bit(ref rc, ref input, ref Unsafe.Add(ref probs, kIsRepG1 + state)) == 0)
                    {
                        dist = _rep1;
                    }
                    else
                    {
                        if (Bit(ref rc, ref input, ref Unsafe.Add(ref probs, kIsRepG2 + state)) == 0)
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
                    _rep1 = rep0;
                    rep0 = dist;
                }

                len = DecodeLength(ref rc, ref input, ref Unsafe.Add(ref probs, kRepLen), posState);
                state = LzmaConstants.StateUpdateLongRep(state);
            }
            else
            {
                // Match
                _rep3 = _rep2;
                _rep2 = _rep1;
                _rep1 = rep0;

                len = DecodeLength(ref rc, ref input, ref Unsafe.Add(ref probs, kMatchLen), posState);
                int distSlot = (int)BitTree(ref rc, ref input,
                    ref Unsafe.Add(ref probs, kPosSlot +
                        LzmaConstants.GetLenToPosState(len + LzmaConstants.kMatchMinLen)
                            * LzmaConstants.kNumPosSlots),
                    1u << LzmaConstants.kNumPosSlotBits) - (1 << LzmaConstants.kNumPosSlotBits);
                rep0 = DecodeDistance(ref rc, ref input, ref probs, distSlot);
                state = LzmaConstants.StateUpdateMatch(state);
            }

            len += LzmaConstants.kMatchMinLen;

            if (allowEndMarker && rep0 == -1)
            {
                // Distance 0xFFFFFFFF marks the end of a .lzma payload.
                rangeDecoder.SetState(rc.Range, rc.Code, rc.Pos);
                outPos = dictStart + p;
                _state = state;
                _rep0 = rep0;
                return true;
            }

            if (exactSize && len > end - p)
                throw new LzmaDataErrorException("LZMA match exceeds chunk boundary.");
            if (rep0 < 0 || rep0 >= p)
                throw new LzmaDataErrorException("Invalid match distance.");

            // The fast copy may write up to 7 bytes past the match; allow it
            // only where those bytes are still inside this call's output
            // region, which later symbols overwrite. Near the end of the
            // region, and without an exact size (.lzma end-marker decoding,
            // whose region end is soft), take the exact copy.
            if (exactSize && len + 8 <= end - p)
                CopyMatchFast(ref win, p, rep0, len);
            else
                CopyMatch(output, dictStart + p, rep0, len);
            p += len;
        }

        rangeDecoder.SetState(rc.Range, rc.Code, rc.Pos);
        outPos = dictStart + p;
        _state = state;
        _rep0 = rep0;
        return false;
    }

    /// <summary>
    /// Copies a match of <paramref name="len"/> bytes at distance
    /// <paramref name="dist"/> (0-based), writing up to 7 bytes past its end.
    /// </summary>
    /// <remarks>
    /// Most LZMA matches are a few bytes long, where a span copy's call and
    /// length dispatch cost more than the copy itself. With the source at least
    /// 8 bytes behind the destination, each 8-byte word is fully written before
    /// any later word reads it, so word copies reproduce the byte-by-byte result.
    /// Closer (overlapping) sources are copied byte by byte or filled. The caller
    /// guarantees the overshoot lies inside the region being decoded.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyMatchFast(ref byte outRef, int pos, int dist, int len)
    {
        int src = pos - dist - 1;
        if (dist >= 7)
        {
            int i = 0;
            do
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref outRef, pos + i),
                    Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref outRef, src + i)));
                i += 8;
            } while (i < len);
        }
        else if (dist == 0)
        {
            // Run-length: repeat the previous byte
            Unsafe.InitBlockUnaligned(ref Unsafe.Add(ref outRef, pos), Unsafe.Add(ref outRef, src), (uint)len);
        }
        else
        {
            for (int i = 0; i < len; i++)
                Unsafe.Add(ref outRef, pos + i) = Unsafe.Add(ref outRef, src + i);
        }
    }

    /// <summary>
    /// Copies a match of <paramref name="len"/> bytes at distance <paramref name="dist"/>
    /// (0-based: 0 = previous byte) inside the output buffer, handling overlap.
    /// </summary>
    private static void CopyMatch(Span<byte> output, int pos, int dist, int len)
    {
        int src = pos - dist - 1;

        if (dist == 0)
        {
            // Run-length: repeat the previous byte
            output.Slice(pos, len).Fill(output[src]);
        }
        else if (dist + 1 >= len)
        {
            // Non-overlapping: single bulk copy
            output.Slice(src, len).CopyTo(output.Slice(pos, len));
        }
        else
        {
            // Overlapping: the destination is the periodic extension of the
            // dist+1 bytes at [src, pos). Copy from the fixed source start so
            // the period phase is preserved; each completed copy doubles the
            // phase-aligned source run, so chunk sizes grow geometrically.
            int dstPos = pos;
            int remaining = len;
            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, dstPos - src);
                output.Slice(src, chunk).CopyTo(output.Slice(dstPos, chunk));
                dstPos += chunk;
                remaining -= chunk;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeLength(ref Rc rc, ref byte input, ref ushort lenRoot, int posState)
    {
        if (Bit(ref rc, ref input, ref lenRoot) == 0) // kLenChoice == 0
        {
            // Low
            return (int)BitTree(ref rc, ref input,
                ref Unsafe.Add(ref lenRoot, kLenLow + (posState << LzmaConstants.kNumLowLenBits)),
                1u << LzmaConstants.kNumLowLenBits) - (1 << LzmaConstants.kNumLowLenBits);
        }
        if (Bit(ref rc, ref input, ref Unsafe.Add(ref lenRoot, kLenChoice2)) == 0)
        {
            // Mid
            return LzmaConstants.kNumLowLenSymbols - (1 << LzmaConstants.kNumMidLenBits)
                + (int)BitTree(ref rc, ref input,
                    ref Unsafe.Add(ref lenRoot, kLenMid + (posState << LzmaConstants.kNumMidLenBits)),
                    1u << LzmaConstants.kNumMidLenBits);
        }
        // High
        return LzmaConstants.kNumLowLenSymbols + LzmaConstants.kNumMidLenSymbols - (1 << LzmaConstants.kNumHighLenBits)
            + (int)BitTree(ref rc, ref input, ref Unsafe.Add(ref lenRoot, kLenHigh),
                1u << LzmaConstants.kNumHighLenBits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeDistance(ref Rc rc, ref byte input,
        ref ushort probs, int distSlot)
    {
        if (distSlot < LzmaConstants.kStartPosModelIndex)
            return distSlot;

        int numDirectBits = (distSlot >> 1) - 1;
        uint dist = (uint)((2 | (distSlot & 1)) << numDirectBits);

        if (distSlot < LzmaConstants.kEndPosModelIndex)
        {
            // Use position-specific bit tree
            int offset = (int)dist - distSlot - 1;
            dist += ReverseBitTree(ref rc, ref input, ref Unsafe.Add(ref probs, kPosSpec + offset), numDirectBits);
        }
        else
        {
            // Direct bits + alignment bits
            dist += DirectBits(ref rc, ref input, numDirectBits - LzmaConstants.kNumAlignBits)
                     << LzmaConstants.kNumAlignBits;
            dist += ReverseBitTree(ref rc, ref input, ref Unsafe.Add(ref probs, kAlign), LzmaConstants.kNumAlignBits);
        }

        return (int)dist;
    }
}
