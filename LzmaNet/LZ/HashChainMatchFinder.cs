// SPDX-License-Identifier: 0BSD

using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace LzmaNet.LZ;

/// <summary>
/// Hash chain (HC4) match finder for LZMA compression.
/// Finds longest matches using 4-byte hashing with chain traversal.
/// The chain table size is rounded up to a power of two so slot mapping uses a
/// mask instead of an integer modulo in the chain-walk inner loop, and match
/// lengths are computed 8 bytes at a time.
/// </summary>
internal sealed class HashChainMatchFinder : SlidingWindowMatchFinder
{
    private int[] _chain;

    public HashChainMatchFinder(int dictSize, int matchMaxLen, int cutValue)
        : base(dictSize, matchMaxLen, cutValue)
    {
        _chain = ArrayPool<int>.Shared.Rent(_cyclicBufferSize);
        Array.Fill(_chain, -1, 0, _cyclicBufferSize);
    }

    protected override void RebasePositions(int delta)
    {
        for (int i = 0; i < _cyclicBufferSize; i++)
        {
            int v = _chain[i];
            if (v >= 0) _chain[i] = v >= delta ? v - delta : -1;
        }
    }

    protected override void ClearPositions()
        => Array.Fill(_chain, -1, 0, _cyclicBufferSize);

    protected override void ReleasePositions()
    {
        ArrayPool<int>.Shared.Return(_chain);
        _chain = null!;
    }

    /// <summary>
    /// Finds matches at the current position. Updates hash tables and chain.
    /// Does NOT advance position — call MovePos or Skip afterward.
    /// </summary>
    public override int FindMatches(Span<int> distances, Span<int> lengths, int maxMatches)
    {
        int avail = Available;
        if (avail < 2) return 0;

        int matchCount = 0;
        int cur = _pos;
        int maxLen = Math.Min(_matchMaxLen, avail);

        // Need at least 4 bytes for 4-byte hashing
        if (avail >= 4)
        {
            byte[] buffer = _buffer;
            LzHash.Compute(buffer, cur, _hashMask, out uint h2, out uint h3, out uint h4);

            // Save old heads before updating
            int pos2 = _hash[h2];
            int pos3 = _hash[h3];
            int curMatch = _hash[h4];

            // Update hash table heads to current position
            _hash[h2] = _pos;
            _hash[h3] = _pos;
            _hash[h4] = _pos;

            // Update chain: current position chains to old head
            _chain[_pos & _cyclicMask] = curMatch;

            _hashUpdatedAtPos = true;

            // Check 2-byte hash match
            if (pos2 >= 0 && pos2 >= _pos - _windowSize
                && buffer[pos2] == buffer[cur] && buffer[pos2 + 1] == buffer[cur + 1])
            {
                if (matchCount < maxMatches)
                {
                    distances[matchCount] = _pos - pos2 - 1;
                    lengths[matchCount] = 2;
                    matchCount++;
                }
            }

            // Check 3-byte hash match
            if (pos3 >= 0 && pos3 >= _pos - _windowSize && pos3 != pos2
                && buffer[pos3] == buffer[cur] && buffer[pos3 + 1] == buffer[cur + 1]
                && buffer[pos3 + 2] == buffer[cur + 2])
            {
                if (matchCount > 0 && lengths[matchCount - 1] < 3)
                {
                    distances[matchCount - 1] = _pos - pos3 - 1;
                    lengths[matchCount - 1] = 3;
                }
                else if (matchCount < maxMatches)
                {
                    distances[matchCount] = _pos - pos3 - 1;
                    lengths[matchCount] = 3;
                    matchCount++;
                }
            }

            // Walk hash chain for 4+ byte matches
            int bestLen = matchCount > 0 ? lengths[matchCount - 1] : 1;
            int count = _cutValue;

            while (curMatch >= 0 && curMatch >= _pos - _windowSize && count-- > 0)
            {
                if (buffer[curMatch + bestLen] == buffer[cur + bestLen])
                {
                    int limit = Math.Min(maxLen, _streamPos - curMatch);
                    int len = MatchLength.Common(buffer, curMatch, cur, limit);

                    if (len > bestLen)
                    {
                        bestLen = len;
                        if (matchCount < maxMatches)
                        {
                            distances[matchCount] = _pos - curMatch - 1;
                            lengths[matchCount] = len;
                            matchCount++;
                        }
                        else
                        {
                            distances[matchCount - 1] = _pos - curMatch - 1;
                            lengths[matchCount - 1] = len;
                        }
                        if (len >= maxLen) break;
                    }
                }
                curMatch = _chain[curMatch & _cyclicMask];
            }
        }

        return matchCount;
    }

    /// <summary>
    /// Advances position by one byte, updating hash tables and chain if not already done by FindMatches.
    /// </summary>
    public override void MovePos()
    {
        if (!_hashUpdatedAtPos && Available >= 4)
            UpdateHashAtCurrentPos();
        _hashUpdatedAtPos = false;
        _pos++;
    }

    private void UpdateHashAtCurrentPos()
    {
        int cur = _pos;
        LzHash.Compute(_buffer, cur, _hashMask, out uint h2, out uint h3, out uint h4);

        int oldHead = _hash[h4];
        _hash[h2] = _pos;
        _hash[h3] = _pos;
        _hash[h4] = _pos;
        _chain[_pos & _cyclicMask] = oldHead;
    }

}
