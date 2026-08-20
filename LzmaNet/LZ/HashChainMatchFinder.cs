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
internal sealed class HashChainMatchFinder : IDisposable
{
    private const int kHash2Size = 1 << 10;
    private const int kHash3Size = 1 << 16;
    private const int kFixHashSize = kHash2Size + kHash3Size;

    private readonly int _windowSize;
    private readonly int _cyclicBufferSize;
    private readonly int _cyclicMask;
    private readonly int _hashMask;
    private readonly int _cutValue;
    private readonly int _matchMaxLen;

    private byte[] _buffer;
    private int[] _hash;
    private int[] _chain;
    private int _bufferSize;
    private int _pos;
    private int _streamPos;
    private bool _disposed;
    private bool _hashUpdatedAtPos; // prevents double hash update when FindMatches + MovePos at same pos

    private static readonly uint[] CrcTable = CreateCrcTable();

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    public HashChainMatchFinder(int dictSize, int matchMaxLen, int cutValue)
    {
        // The true match window (max look-back distance). Distances must stay
        // below this so they remain valid for the dictionary size declared in
        // the XZ block header.
        _windowSize = Math.Max(dictSize, 2);

        // Chain slot count rounded up to a power of two so slots can be
        // computed with a mask instead of an integer modulo.
        _cyclicBufferSize = (int)BitOperations.RoundUpToPowerOf2((uint)_windowSize);
        _cyclicMask = _cyclicBufferSize - 1;
        _matchMaxLen = matchMaxLen;
        _cutValue = cutValue;

        int hashBits = dictSize < (1 << 16) ? 16 : dictSize < (1 << 20) ? 18 : 20;
        _hashMask = (1 << hashBits) - 1;

        int hashSize = kFixHashSize + (1 << hashBits);
        _hash = ArrayPool<int>.Shared.Rent(hashSize);
        Array.Fill(_hash, -1, 0, hashSize);

        _chain = ArrayPool<int>.Shared.Rent(_cyclicBufferSize);
        Array.Fill(_chain, -1, 0, _cyclicBufferSize);

        // Sized so the buffer only slides after at least one full cyclic-size
        // span has accumulated behind the window; slides then move by a
        // multiple of the cyclic size, which keeps chain slot mapping valid
        // (slot(p - k*cyclic) == slot(p)).
        _bufferSize = _windowSize + _cyclicBufferSize + (1 << 16) + matchMaxLen + 4096;
        _buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
        _pos = 0;
        _streamPos = 0;
    }

    public void SetInput(ReadOnlySpan<byte> data)
    {
        EnsureCapacity(data.Length);
        data.CopyTo(_buffer.AsSpan(_streamPos));
        _streamPos += data.Length;
    }

    private void EnsureCapacity(int incoming)
    {
        if (_streamPos <= _bufferSize - incoming - _matchMaxLen)
            return;

        // Slide the window down by a multiple of the cyclic size so existing
        // hash/chain entries stay slot-consistent after rebasing.
        int keepFrom = _pos - _windowSize;
        if (keepFrom > 0)
        {
            int delta = keepFrom & ~_cyclicMask; // round down to cyclic multiple
            if (delta > 0)
            {
                Buffer.BlockCopy(_buffer, delta, _buffer, 0, _streamPos - delta);
                _pos -= delta;
                _streamPos -= delta;
                RebaseTables(delta);
            }
        }

        if (_streamPos > _bufferSize - incoming - _matchMaxLen)
        {
            // Input larger than the buffer (direct one-shot encodes) — grow.
            int newSize = Math.Max(_streamPos + incoming + _matchMaxLen + 4096, _bufferSize * 2);
            byte[] bigger = ArrayPool<byte>.Shared.Rent(newSize);
            Buffer.BlockCopy(_buffer, 0, bigger, 0, _streamPos);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = bigger;
            _bufferSize = newSize;
        }
    }

    /// <summary>
    /// Shifts all stored positions down by <paramref name="delta"/> after a buffer
    /// slide. <paramref name="delta"/> is a multiple of the cyclic size, so chain
    /// slot indices are unaffected.
    /// </summary>
    private void RebaseTables(int delta)
    {
        int hashSize = kFixHashSize + _hashMask + 1;
        for (int i = 0; i < hashSize; i++)
        {
            int v = _hash[i];
            if (v >= 0) _hash[i] = v >= delta ? v - delta : -1;
        }
        for (int i = 0; i < _cyclicBufferSize; i++)
        {
            int v = _chain[i];
            if (v >= 0) _chain[i] = v >= delta ? v - delta : -1;
        }
    }

    /// <summary>
    /// Resets position counters and clears hash/chain tables.
    /// Call this when starting a new independent encoding unit (e.g., LZMA2 chunk with state reset).
    /// </summary>
    public void Reset()
    {
        _pos = 0;
        _streamPos = 0;
        _hashUpdatedAtPos = false;
        int hashSize = kFixHashSize + _hashMask + 1;
        Array.Fill(_hash, -1, 0, hashSize);
        Array.Fill(_chain, -1, 0, _cyclicBufferSize);
    }

    public int Available => _streamPos - _pos;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetByte(int offset) => _buffer[_pos + offset];

    /// <summary>
    /// Finds matches at the current position. Updates hash tables and chain.
    /// Does NOT advance position — call MovePos or Skip afterward.
    /// </summary>
    public int FindMatches(Span<int> distances, Span<int> lengths, int maxMatches)
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
            uint hash2Val = CrcTable[buffer[cur]] ^ buffer[cur + 1];
            uint hash3Val = hash2Val ^ ((uint)CrcTable[buffer[cur + 2]] << 5);
            uint hash4Val = hash3Val ^ ((uint)CrcTable[buffer[cur + 3]] << 13);

            uint h2 = hash2Val & (kHash2Size - 1);
            uint h3 = kHash2Size + (hash3Val & (kHash3Size - 1));
            uint h4 = kFixHashSize + (hash4Val & (uint)_hashMask);

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
                    int len = MatchLength(buffer, curMatch, cur, limit);

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
    /// Computes the common-prefix length of buffer[a..] and buffer[b..], up to limit,
    /// comparing 32 bytes at a time with SIMD where available, then 8 bytes at a time.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MatchLength(byte[] buffer, int a, int b, int limit)
    {
        int len = 0;
        ref byte bufRef = ref System.Runtime.InteropServices.MemoryMarshal
            .GetArrayDataReference(buffer);

        if (Vector256.IsHardwareAccelerated)
        {
            while (len + 32 <= limit)
            {
                var va = Vector256.LoadUnsafe(ref bufRef, (nuint)(a + len));
                var vb = Vector256.LoadUnsafe(ref bufRef, (nuint)(b + len));
                uint neq = ~Vector256.Equals(va, vb).ExtractMostSignificantBits();
                if (neq != 0)
                    return len + BitOperations.TrailingZeroCount(neq);
                len += 32;
            }
        }

        if (BitConverter.IsLittleEndian)
        {
            while (len + 8 <= limit)
            {
                ulong x = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bufRef, a + len))
                        ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bufRef, b + len));
                if (x != 0)
                    return len + (BitOperations.TrailingZeroCount(x) >> 3);
                len += 8;
            }
        }

        while (len < limit && buffer[a + len] == buffer[b + len])
            len++;
        return len;
    }

    /// <summary>
    /// Advances position by one byte, updating hash tables and chain if not already done by FindMatches.
    /// </summary>
    public void MovePos()
    {
        if (!_hashUpdatedAtPos && Available >= 4)
            UpdateHashAtCurrentPos();
        _hashUpdatedAtPos = false;
        _pos++;
    }

    /// <summary>
    /// Advances position by count bytes. First call skips hash update if FindMatches was called.
    /// Subsequent positions get full hash updates.
    /// </summary>
    public void Skip(int count)
    {
        for (int i = 0; i < count; i++)
            MovePos();
    }

    private void UpdateHashAtCurrentPos()
    {
        int cur = _pos;
        uint hash2Val = CrcTable[_buffer[cur]] ^ _buffer[cur + 1];
        uint hash3Val = hash2Val ^ ((uint)CrcTable[_buffer[cur + 2]] << 5);
        uint hash4Val = hash3Val ^ ((uint)CrcTable[_buffer[cur + 3]] << 13);

        uint h2 = hash2Val & (kHash2Size - 1);
        uint h3 = kHash2Size + (hash3Val & (kHash3Size - 1));
        uint h4 = kFixHashSize + (hash4Val & (uint)_hashMask);

        int oldHead = _hash[h4];
        _hash[h2] = _pos;
        _hash[h3] = _pos;
        _hash[h4] = _pos;
        _chain[_pos & _cyclicMask] = oldHead;
    }

    public int Position => _pos;

    public void Dispose()
    {
        if (!_disposed)
        {
            ArrayPool<int>.Shared.Return(_hash);
            ArrayPool<int>.Shared.Return(_chain);
            ArrayPool<byte>.Shared.Return(_buffer);
            _hash = null!;
            _chain = null!;
            _buffer = null!;
            _disposed = true;
        }
    }
}
