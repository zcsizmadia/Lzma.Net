// SPDX-License-Identifier: 0BSD

using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

using LzmaNet.Compatibility;

#if !NET8_0_OR_GREATER
// netstandard has no MemoryMarshal.GetArrayDataReference; see
// Compatibility/MemoryMarshal.cs.
using MemoryMarshal = LzmaNet.Compatibility.MemoryMarshal;
#else
using System.Runtime.InteropServices;
#endif

namespace LzmaNet.LZ;

/// <summary>
/// Binary tree (BT4) match finder for LZMA compression, following the reference
/// LZMA encoder's algorithm: previous positions with the same 4-byte hash form a
/// binary search tree ordered lexicographically by suffix, which is rebalanced
/// (positions re-linked) as each new position is inserted. Finds better matches
/// than the hash chain — the nearest distance for every length, with strictly
/// increasing lengths — at higher CPU and memory cost. Used by the optimal parser.
/// </summary>
internal sealed class BinaryTreeMatchFinder : SlidingWindowMatchFinder
{
    private readonly int _maxMatchDelta;

    private int[] _son; // two entries per slot: [2*slot] = left child, [2*slot+1] = right child

    public BinaryTreeMatchFinder(int dictSize, int matchMaxLen, int cutValue)
        : base(dictSize, matchMaxLen, cutValue, LzHash.BinaryTreeHashBits(dictSize))
    {
        // Tree nodes are addressed by cyclic slot, so a candidate at delta ==
        // _cyclicBufferSize maps onto the son[] slot pair of the position being
        // inserted: adopting its subtrees would read the new node's own
        // half-written links and can re-link a walk ancestor as its descendant,
        // breaking the ordering that the len0/len1 shortcut relies on. Cut one
        // short of the cyclic size (the reference LzFind instead oversizes the
        // cyclic buffer to windowSize + 1, which here would double _son).
        // Distances must also stay within the dictionary to remain decodable,
        // which binds first when the window is not a power of two.
        _maxMatchDelta = Math.Min(_windowSize, _cyclicBufferSize - 1);

        _son = ArrayPool<int>.Shared.Rent(2 * _cyclicBufferSize);
        Portable.Fill(_son, -1, 0, 2 * _cyclicBufferSize);
    }

    protected override void RebasePositions(int delta)
    {
        int sonSize = 2 * _cyclicBufferSize;
        for (int i = 0; i < sonSize; i++)
        {
            int v = _son[i];
            if (v >= 0) _son[i] = v >= delta ? v - delta : -1;
        }
    }

    protected override void ClearPositions()
        => Portable.Fill(_son, -1, 0, 2 * _cyclicBufferSize);

    protected override void ReleasePositions()
    {
        ArrayPool<int>.Shared.Return(_son);
        _son = null!;
    }

    public override int FindMatches(Span<int> distances, Span<int> lengths, int maxMatches)
    {
        int avail = Available;
        if (avail < 4)
        {
            // Cannot hash; nothing is inserted (same as the hash-chain finder).
            return 0;
        }

        byte[] buffer = _buffer;
        int cur = _pos;
        int lenLimit = Math.Min(_matchMaxLen, avail);
        int matchCount = 0;

        LzHash.Compute(buffer, cur, _hashMask, out uint h2, out uint h3, out uint h4);

        int pos2 = _hash[h2];
        int pos3 = _hash[h3];
        int curMatch = _hash[h4];

        _hash[h2] = _pos;
        _hash[h3] = _pos;
        _hash[h4] = _pos;
        _hashUpdatedAtPos = true;
        PrefetchHashHead(cur + 1);

        int maxLen = 1;

        // 2-byte hash candidate
        if (pos2 >= 0 && pos2 >= _pos - _windowSize
            && buffer[pos2] == buffer[cur] && buffer[pos2 + 1] == buffer[cur + 1])
        {
            int len = MatchLength.Common(buffer, pos2, cur, lenLimit);
            if (len > maxLen && matchCount < maxMatches)
            {
                distances[matchCount] = _pos - pos2 - 1;
                lengths[matchCount] = len;
                matchCount++;
                maxLen = len;
            }
        }

        // 3-byte hash candidate
        if (pos3 >= 0 && pos3 >= _pos - _windowSize && pos3 != pos2
            && buffer[pos3] == buffer[cur] && buffer[pos3 + 1] == buffer[cur + 1]
            && buffer[pos3 + 2] == buffer[cur + 2])
        {
            int len = MatchLength.Common(buffer, pos3, cur, lenLimit);
            if (len > maxLen && matchCount < maxMatches)
            {
                distances[matchCount] = _pos - pos3 - 1;
                lengths[matchCount] = len;
                matchCount++;
                maxLen = len;
            }
        }

        if (maxLen >= lenLimit)
        {
            // Nothing longer can exist; insert into the tree without collecting.
            TreeInsert(curMatch, cur, lenLimit);
            return matchCount;
        }

        matchCount = TreeWalk(curMatch, cur, lenLimit, maxLen, distances, lengths, matchCount, maxMatches);
        return matchCount;
    }

    /// <summary>
    /// Walks (and re-links) the binary tree for the current position, collecting
    /// matches with strictly increasing lengths (nearest distance first for each
    /// new length). This is the LZMA reference GetMatchesSpec1 algorithm.
    /// </summary>
    /// <remarks>
    /// Tree descent is the hottest loop of BT4 encoding and is bound by cache
    /// misses into <c>_son</c> and the buffer, so it runs on raw references:
    /// every <c>son</c> index is <c>2 * (p &amp; _cyclicMask) + {0,1}</c>, inside
    /// the table by construction, and every buffer read stays below
    /// <c>cur + lenLimit</c> plus the word-compare slack the buffer is sized with.
    /// Fields are copied to locals so the stores into <c>son</c> do not force
    /// the JIT to reload them on every step.
    /// </remarks>
    private int TreeWalk(int curMatch, int cur, int lenLimit, int maxLen,
        Span<int> distances, Span<int> lengths, int matchCount, int maxMatches)
    {
        ref int son = ref MemoryMarshal.GetArrayDataReference(_son);
        ref byte buf = ref MemoryMarshal.GetArrayDataReference(_buffer);
        int pos = _pos;
        int cyclicMask = _cyclicMask;
        int ptr0 = 2 * (pos & cyclicMask) + 1; // right subtree slot of the new node
        int ptr1 = 2 * (pos & cyclicMask);     // left subtree slot of the new node
        int len0 = 0, len1 = 0;
        int count = _cutValue;
        int windowFloor = pos - _maxMatchDelta;

        while (true)
        {
            if (curMatch < 0 || curMatch < windowFloor || count-- == 0)
            {
                Unsafe.Add(ref son, ptr0) = -1;
                Unsafe.Add(ref son, ptr1) = -1;
                return matchCount;
            }

            int pair = 2 * (curMatch & cyclicMask);
            int len = Math.Min(len0, len1);

            if (Unsafe.Add(ref buf, curMatch + len) == Unsafe.Add(ref buf, cur + len))
            {
                len = ExtendMatch(ref buf, curMatch, cur, len + 1, lenLimit);
                if (len > maxLen)
                {
                    maxLen = len;
                    if (matchCount < maxMatches)
                    {
                        distances[matchCount] = pos - curMatch - 1;
                        lengths[matchCount] = len;
                        matchCount++;
                    }
                    else
                    {
                        distances[matchCount - 1] = pos - curMatch - 1;
                        lengths[matchCount - 1] = len;
                    }
                }
                if (len == lenLimit)
                {
                    // Full-length match: adopt this node's subtrees and stop.
                    Unsafe.Add(ref son, ptr1) = Unsafe.Add(ref son, pair);
                    Unsafe.Add(ref son, ptr0) = Unsafe.Add(ref son, pair + 1);
                    return matchCount;
                }
            }

            if (Unsafe.Add(ref buf, curMatch + len) < Unsafe.Add(ref buf, cur + len))
            {
                Unsafe.Add(ref son, ptr1) = curMatch;
                ptr1 = pair + 1;
                curMatch = Unsafe.Add(ref son, ptr1);
                len1 = len;
            }
            else
            {
                Unsafe.Add(ref son, ptr0) = curMatch;
                ptr0 = pair;
                curMatch = Unsafe.Add(ref son, ptr0);
                len0 = len;
            }
        }
    }

    /// <summary>
    /// Inserts the current position into the tree without collecting matches
    /// (LZMA reference SkipMatchesSpec).
    /// </summary>
    /// <remarks>
    /// This is <see cref="TreeWalk"/> with the match-collection block removed,
    /// and it stays a separate method on purpose: it runs for every skipped
    /// position — the bulk of positions once a match is taken — and folding the
    /// two would put a collect-or-not branch inside the tree descent, the
    /// hottest loop in BT4 encoding. The two share their termination condition
    /// and re-link steps, so a change to one needs the same change here.
    /// </remarks>
    private void TreeInsert(int curMatch, int cur, int lenLimit)
    {
        ref int son = ref MemoryMarshal.GetArrayDataReference(_son);
        ref byte buf = ref MemoryMarshal.GetArrayDataReference(_buffer);
        int pos = _pos;
        int cyclicMask = _cyclicMask;
        int ptr0 = 2 * (pos & cyclicMask) + 1;
        int ptr1 = 2 * (pos & cyclicMask);
        int len0 = 0, len1 = 0;
        int count = _cutValue;
        int windowFloor = pos - _maxMatchDelta;

        while (true)
        {
            if (curMatch < 0 || curMatch < windowFloor || count-- == 0)
            {
                Unsafe.Add(ref son, ptr0) = -1;
                Unsafe.Add(ref son, ptr1) = -1;
                return;
            }

            int pair = 2 * (curMatch & cyclicMask);
            int len = Math.Min(len0, len1);

            if (Unsafe.Add(ref buf, curMatch + len) == Unsafe.Add(ref buf, cur + len))
            {
                len = ExtendMatch(ref buf, curMatch, cur, len + 1, lenLimit);
                if (len == lenLimit)
                {
                    Unsafe.Add(ref son, ptr1) = Unsafe.Add(ref son, pair);
                    Unsafe.Add(ref son, ptr0) = Unsafe.Add(ref son, pair + 1);
                    return;
                }
            }

            if (Unsafe.Add(ref buf, curMatch + len) < Unsafe.Add(ref buf, cur + len))
            {
                Unsafe.Add(ref son, ptr1) = curMatch;
                ptr1 = pair + 1;
                curMatch = Unsafe.Add(ref son, ptr1);
                len1 = len;
            }
            else
            {
                Unsafe.Add(ref son, ptr0) = curMatch;
                ptr0 = pair;
                curMatch = Unsafe.Add(ref son, ptr0);
                len0 = len;
            }
        }
    }

    /// <summary>
    /// Extends a match known to agree on its first <paramref name="len"/> bytes,
    /// up to <paramref name="lenLimit"/>.
    /// </summary>
    /// <remarks>
    /// Tree-walk extensions are usually short, so this compares 8-byte words
    /// rather than going through <see cref="MatchLength.Extend"/>'s 32-byte
    /// vector path, whose setup dominates for a few bytes. The last word may read
    /// up to 7 bytes past <c>cur + lenLimit</c>; the buffer is sized with at
    /// least <c>matchMaxLen</c> bytes of slack past the stream end, and the
    /// result is clamped, so those bytes never affect the length.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ExtendMatch(ref byte buf, int a, int b, int len, int lenLimit)
    {
        if (BitConverter.IsLittleEndian)
        {
            while (len < lenLimit)
            {
                ulong x = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref buf, a + len))
                        ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref buf, b + len));
                if (x != 0)
                {
                    len += BitOperations.TrailingZeroCount(x) >> 3;
                    return Math.Min(len, lenLimit);
                }
                len += 8;
            }
            return lenLimit;
        }

        while (len < lenLimit && Unsafe.Add(ref buf, a + len) == Unsafe.Add(ref buf, b + len))
            len++;
        return len;
    }

    public override void MovePos()
    {
        if (!_hashUpdatedAtPos && Available >= 4)
            InsertCurrentPos();
        _hashUpdatedAtPos = false;
        _pos++;
    }

    private void InsertCurrentPos()
    {
        byte[] buffer = _buffer;
        int cur = _pos;
        int lenLimit = Math.Min(_matchMaxLen, Available);

        LzHash.Compute(buffer, cur, _hashMask, out uint h2, out uint h3, out uint h4);

        int curMatch = _hash[h4];
        _hash[h2] = _pos;
        _hash[h3] = _pos;
        _hash[h4] = _pos;
        PrefetchHashHead(cur + 1);

        TreeInsert(curMatch, cur, lenLimit);
    }

    /// <summary>
    /// Software-pipelines the start of the next tree walks: prefetches the
    /// 4-byte hash slot of position <paramref name="p"/> + 1, and the tree node
    /// and buffer bytes of the head that position <paramref name="p"/>'s slot
    /// (prefetched one step earlier) currently points to.
    /// </summary>
    /// <remarks>
    /// Positions are inserted strictly in order (by <see cref="FindMatches"/> or
    /// the skip path), and with liblzma-sized tables the hash, child and buffer
    /// arrays are tens to hundreds of MB, so the first three reads of every walk
    /// are near-certain misses that otherwise stall it. Measured ~11% faster
    /// preset-9 encoding on Silesia. A prefetch is only a hint: the head read
    /// here may be stale (the current insertion can overwrite it), and the
    /// hashes may read up to 4 bytes past the stream end (inside the buffer's
    /// match-length slack); at worst the wrong line is fetched. The finder's
    /// results are unchanged.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void PrefetchHashHead(int p)
    {
#if NET8_0_OR_GREATER
        if (System.Runtime.Intrinsics.X86.Sse.IsSupported)
        {
            ref int hash = ref MemoryMarshal.GetArrayDataReference(_hash);
            LzHash.Compute(_buffer, p + 1, _hashMask, out _, out _, out uint h4Far);
            System.Runtime.Intrinsics.X86.Sse.Prefetch0(Unsafe.AsPointer(ref Unsafe.Add(ref hash, (nint)h4Far)));

            LzHash.Compute(_buffer, p, _hashMask, out _, out _, out uint h4);
            int head = Unsafe.Add(ref hash, (nint)h4);
            if (head >= 0)
            {
                System.Runtime.Intrinsics.X86.Sse.Prefetch0(Unsafe.AsPointer(
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_son), (nint)(2 * (head & _cyclicMask)))));
                System.Runtime.Intrinsics.X86.Sse.Prefetch0(Unsafe.AsPointer(
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buffer), (nint)head)));
            }
        }
#endif
    }
}
