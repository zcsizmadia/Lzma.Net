// SPDX-License-Identifier: 0BSD

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using LzmaNet.RangeCoder;

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
    /// <see cref="_litProbs"/>; a decoder can only be reused for the same lc+lp sum.</summary>
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

    private bool DecodeCore(ref RangeDecoder rc, Span<byte> output, ref int outPos,
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

        // Hoist all hot state into locals so the JIT can keep them in registers;
        // fields are written back once at the end.
        int state = _state;
        int rep0 = _rep0, rep1 = _rep1, rep2 = _rep2, rep3 = _rep3;

        // A matched literal at the start of a continuation chunk consumes rep0 before
        // any match validates it; reject a stale rep0 that reaches outside the window.
        if (state >= 7 && (rep0 < 0 || rep0 >= outPos - dictStart))
            throw new LzmaDataErrorException("Invalid rep distance at chunk start.");
        int posMask = _posMask;
        int litPosMask = _litPosMask;
        int lc = _lc;
        int pos = outPos;
        int remaining = uncompressedSize;

        ref ushort isMatchRoot = ref MemoryMarshal.GetArrayDataReference(_isMatch);
        ref ushort isRepRoot = ref MemoryMarshal.GetArrayDataReference(_isRep);
        ref ushort isRepG0Root = ref MemoryMarshal.GetArrayDataReference(_isRepG0);
        ref ushort isRepG1Root = ref MemoryMarshal.GetArrayDataReference(_isRepG1);
        ref ushort isRepG2Root = ref MemoryMarshal.GetArrayDataReference(_isRepG2);
        ref ushort isRep0LongRoot = ref MemoryMarshal.GetArrayDataReference(_isRep0Long);
        ref ushort litRoot = ref MemoryMarshal.GetArrayDataReference(_litProbs);

        while (remaining > 0)
        {
            int posState = (pos - dictStart) & posMask;

            if (rc.DecodeBit(ref Unsafe.Add(ref isMatchRoot,
                    (state << LzmaConstants.kNumPosStatesBitsMax) + posState)) == 0)
            {
                // Literal
                byte prevByte = pos > dictStart ? output[pos - 1] : (byte)0;
                int litState = (((pos - dictStart) & litPosMask) << lc) + (prevByte >> (8 - lc));
                ref ushort litSub = ref Unsafe.Add(ref litRoot, litState * LzmaConstants.kLitSubcoderSize);

                uint symbol = 1;
                if (state >= 7) // !StateIsLiteral: matched literal, use match byte for context
                {
                    byte matchByte = output[pos - rep0 - 1];
                    do
                    {
                        uint matchBit = (uint)(matchByte >> 7) & 1;
                        matchByte <<= 1;
                        uint bit = rc.DecodeBit(ref Unsafe.Add(ref litSub,
                            (int)(((1 + matchBit) << 8) + symbol)));
                        symbol = (symbol << 1) | bit;
                        if (matchBit != bit)
                            break;
                    } while (symbol < 0x100);
                }

                // Normal literal decoding (or finishing after match divergence)
                while (symbol < 0x100)
                    symbol = (symbol << 1) | rc.DecodeBit(ref Unsafe.Add(ref litSub, (int)symbol));

                output[pos++] = (byte)symbol;
                state = LzmaConstants.StateUpdateLiteral(state);
                remaining--;
            }
            else
            {
                int len;
                if (rc.DecodeBit(ref Unsafe.Add(ref isRepRoot, state)) != 0)
                {
                    // Rep match
                    if (rc.DecodeBit(ref Unsafe.Add(ref isRepG0Root, state)) == 0)
                    {
                        // Rep0
                        if (rc.DecodeBit(ref Unsafe.Add(ref isRep0LongRoot,
                                (state << LzmaConstants.kNumPosStatesBitsMax) + posState)) == 0)
                        {
                            // Short rep (single byte at rep0 distance)
                            if (rep0 >= pos - dictStart)
                                throw new LzmaDataErrorException("Invalid distance in short rep.");
                            output[pos] = output[pos - rep0 - 1];
                            pos++;
                            state = LzmaConstants.StateUpdateShortRep(state);
                            remaining--;
                            continue;
                        }
                        // Long rep0
                    }
                    else
                    {
                        int dist;
                        if (rc.DecodeBit(ref Unsafe.Add(ref isRepG1Root, state)) == 0)
                        {
                            dist = rep1;
                        }
                        else
                        {
                            if (rc.DecodeBit(ref Unsafe.Add(ref isRepG2Root, state)) == 0)
                            {
                                dist = rep2;
                            }
                            else
                            {
                                dist = rep3;
                                rep3 = rep2;
                            }
                            rep2 = rep1;
                        }
                        rep1 = rep0;
                        rep0 = dist;
                    }

                    len = DecodeLength(ref rc, _repLenProbs, posState);
                    state = LzmaConstants.StateUpdateLongRep(state);
                }
                else
                {
                    // Match
                    rep3 = rep2;
                    rep2 = rep1;
                    rep1 = rep0;

                    len = DecodeLength(ref rc, _matchLenProbs, posState);
                    int distSlot = DecodeDistSlot(ref rc,
                        LzmaConstants.GetLenToPosState(len + LzmaConstants.kMatchMinLen));
                    rep0 = DecodeDistance(ref rc, distSlot);
                    state = LzmaConstants.StateUpdateMatch(state);
                }

                len += LzmaConstants.kMatchMinLen;

                if (allowEndMarker && rep0 == -1)
                {
                    // Distance 0xFFFFFFFF marks the end of a .lzma payload.
                    outPos = pos;
                    _state = state;
                    _rep0 = rep0;
                    _rep1 = rep1;
                    _rep2 = rep2;
                    _rep3 = rep3;
                    return true;
                }

                if (exactSize && len > remaining)
                    throw new LzmaDataErrorException("LZMA match exceeds chunk boundary.");
                if (rep0 < 0 || rep0 >= pos - dictStart)
                    throw new LzmaDataErrorException("Invalid match distance.");

                CopyMatch(output, pos, rep0, len);
                pos += len;
                remaining -= len;
            }
        }

        outPos = pos;
        _state = state;
        _rep0 = rep0;
        _rep1 = rep1;
        _rep2 = rep2;
        _rep3 = rep3;
        return false;
    }

    /// <summary>
    /// Copies a match of <paramref name="len"/> bytes at distance <paramref name="dist"/>
    /// (0-based: 0 = previous byte) inside the output buffer, handling overlap.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    private int DecodeLength(ref RangeDecoder rc, ushort[] lenProbs, int posState)
    {
        ref ushort lenRoot = ref MemoryMarshal.GetArrayDataReference(lenProbs);

        if (rc.DecodeBit(ref lenRoot) == 0) // kLenChoice == 0
        {
            // Low
            return (int)rc.DecodeBitTree(
                ref Unsafe.Add(ref lenRoot, kLenLow + (posState << LzmaConstants.kNumLowLenBits)),
                LzmaConstants.kNumLowLenBits);
        }
        if (rc.DecodeBit(ref Unsafe.Add(ref lenRoot, kLenChoice2)) == 0)
        {
            // Mid
            return LzmaConstants.kNumLowLenSymbols
                + (int)rc.DecodeBitTree(
                    ref Unsafe.Add(ref lenRoot, kLenMid + (posState << LzmaConstants.kNumMidLenBits)),
                    LzmaConstants.kNumMidLenBits);
        }
        // High
        return LzmaConstants.kNumLowLenSymbols + LzmaConstants.kNumMidLenSymbols
            + (int)rc.DecodeBitTree(ref Unsafe.Add(ref lenRoot, kLenHigh), LzmaConstants.kNumHighLenBits);
    }

    private int DecodeDistSlot(ref RangeDecoder rc, int lenToPosState)
    {
        return (int)rc.DecodeBitTree(
            ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_posSlotCoders),
                lenToPosState * LzmaConstants.kNumPosSlots),
            LzmaConstants.kNumPosSlotBits);
    }

    private int DecodeDistance(ref RangeDecoder rc, int distSlot)
    {
        if (distSlot < LzmaConstants.kStartPosModelIndex)
            return distSlot;

        int numDirectBits = (distSlot >> 1) - 1;
        uint dist = (uint)((2 | (distSlot & 1)) << numDirectBits);

        if (distSlot < LzmaConstants.kEndPosModelIndex)
        {
            // Use position-specific bit tree
            int offset = (int)dist - distSlot - 1;
            dist += rc.DecodeReverseBitTree(
                ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_posSpecProbs), offset),
                numDirectBits);
        }
        else
        {
            // Direct bits + alignment bits
            dist += rc.DecodeDirectBits(numDirectBits - LzmaConstants.kNumAlignBits)
                     << LzmaConstants.kNumAlignBits;
            dist += rc.DecodeReverseBitTree(
                ref MemoryMarshal.GetArrayDataReference(_alignProbs),
                LzmaConstants.kNumAlignBits);
        }

        return (int)dist;
    }
}
