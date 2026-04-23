// SPDX-License-Identifier: 0BSD

using System.Buffers;
using System.Runtime.CompilerServices;

namespace LzmaNet.LZ;

/// <summary>
/// Hash chain (HC4) match finder for LZMA compression.
/// Finds longest matches using 4-byte hashing with chain traversal.
/// </summary>
internal sealed class HashChainMatchFinder : IDisposable
{
    private const int kHash2Size = 1 << 10;
    private const int kHash3Size = 1 << 16;
    private const int kFixHashSize = kHash2Size + kHash3Size;

    private readonly int _cyclicBufferSize;
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
        _cyclicBufferSize = dictSize;
        _matchMaxLen = matchMaxLen;
        _cutValue = cutValue;

        int hashBits = dictSize < (1 << 16) ? 16 : dictSize < (1 << 20) ? 18 : 20;
        _hashMask = (1 << hashBits) - 1;

        int hashSize = kFixHashSize + (1 << hashBits);
        _hash = ArrayPool<int>.Shared.Rent(hashSize);
        Array.Fill(_hash, -1, 0, hashSize);

        _chain = ArrayPool<int>.Shared.Rent(_cyclicBufferSize);
        Array.Fill(_chain, -1, 0, _cyclicBufferSize);

        // Buffer must hold: cyclicBufferSize (lookback) + max chunk size + matchMaxLen
        // LZMA2 chunk size is capped at 64 KB to fit the 16-bit compressed size field.
        // Use a generous estimate to handle any chunk size passed via SetInput.
        int maxChunkSize = Math.Min(1 << 21, dictSize * 2);
        _bufferSize = dictSize + maxChunkSize + matchMaxLen + 4096;
        _buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
        _pos = 0;
        _streamPos = 0;
    }

    public void SetInput(ReadOnlySpan<byte> data)
    {
        if (_pos > _bufferSize - data.Length - _matchMaxLen)
        {
            int moveFrom = _pos - _cyclicBufferSize;
            if (moveFrom < 0) moveFrom = 0;
            int moveSize = _streamPos - moveFrom;
            Buffer.BlockCopy(_buffer, moveFrom, _buffer, 0, moveSize);
            _pos -= moveFrom;
            _streamPos -= moveFrom;
        }
        data.CopyTo(_buffer.AsSpan(_streamPos));
        _streamPos += data.Length;
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
            uint hash2Val = CrcTable[_buffer[cur]] ^ _buffer[cur + 1];
            uint hash3Val = hash2Val ^ ((uint)CrcTable[_buffer[cur + 2]] << 5);
            uint hash4Val = hash3Val ^ ((uint)CrcTable[_buffer[cur + 3]] << 13);

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
            _chain[_pos % _cyclicBufferSize] = curMatch;

            _hashUpdatedAtPos = true;

            // Check 2-byte hash match
            if (pos2 >= 0 && pos2 >= _pos - _cyclicBufferSize
                && _buffer[pos2] == _buffer[cur] && _buffer[pos2 + 1] == _buffer[cur + 1])
            {
                if (matchCount < maxMatches)
                {
                    distances[matchCount] = _pos - pos2 - 1;
                    lengths[matchCount] = 2;
                    matchCount++;
                }
            }

            // Check 3-byte hash match
            if (pos3 >= 0 && pos3 >= _pos - _cyclicBufferSize && pos3 != pos2
                && _buffer[pos3] == _buffer[cur] && _buffer[pos3 + 1] == _buffer[cur + 1]
                && _buffer[pos3 + 2] == _buffer[cur + 2])
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

            while (curMatch >= 0 && curMatch >= _pos - _cyclicBufferSize && count-- > 0)
            {
                if (_buffer[curMatch + bestLen] == _buffer[cur + bestLen])
                {
                    int len = 0;
                    int limit = Math.Min(maxLen, _streamPos - curMatch);
                    while (len < limit && _buffer[curMatch + len] == _buffer[cur + len])
                        len++;

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
                curMatch = _chain[curMatch % _cyclicBufferSize];
            }
        }

        return matchCount;
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
        _chain[_pos % _cyclicBufferSize] = oldHead;
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
