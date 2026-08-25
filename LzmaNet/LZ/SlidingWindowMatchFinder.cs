// SPDX-License-Identifier: 0BSD

using System.Buffers;
using System.Numerics;

namespace LzmaNet.LZ;

/// <summary>
/// The search window both match finders sit on: a byte buffer holding recent
/// input, a hash table of candidate positions, and the sliding that keeps the
/// buffer bounded as input streams through.
/// </summary>
/// <remarks>
/// <para>
/// The slide is the delicate part. Every stored position is an index into the
/// buffer, and the derived finders also index their own tables by cyclic slot
/// (<c>position &amp; _cyclicMask</c>). Sliding by an arbitrary amount would
/// change every position's slot and invalidate those tables, so the buffer only
/// ever moves by a whole multiple of the cyclic size — which leaves
/// <c>slot(p - k*cyclic) == slot(p)</c> — and the buffer is sized with a full
/// cyclic span of slack so such a slide is always available before it fills.
/// </para>
/// <para>
/// That invariant used to be maintained in two independent copies, one per
/// finder. It lives here now; a derived finder only says how to rebase, clear,
/// and release its own position table.
/// </para>
/// </remarks>
internal abstract class SlidingWindowMatchFinder : IMatchFinder
{
    /// <summary>Maximum look-back distance, i.e. the dictionary size.</summary>
    protected readonly int _windowSize;

    /// <summary>
    /// Position-table slot count, the window size rounded up to a power of two so
    /// slots come from a mask rather than an integer modulo.
    /// </summary>
    protected readonly int _cyclicBufferSize;

    protected readonly int _cyclicMask;
    protected readonly int _hashMask;
    protected readonly int _cutValue;
    protected readonly int _matchMaxLen;

    protected byte[] _buffer;
    protected int[] _hash;
    protected int _bufferSize;
    protected int _pos;
    protected int _streamPos;

    /// <summary>
    /// Set by <see cref="FindMatches"/> so the following <see cref="MovePos"/>
    /// does not insert the same position twice.
    /// </summary>
    protected bool _hashUpdatedAtPos;

    private bool _disposed;

    protected SlidingWindowMatchFinder(int dictSize, int matchMaxLen, int cutValue)
    {
        _windowSize = Math.Max(dictSize, 2);
        _cyclicBufferSize = (int)BitOperations.RoundUpToPowerOf2((uint)_windowSize);
        _cyclicMask = _cyclicBufferSize - 1;
        _matchMaxLen = matchMaxLen;
        _cutValue = cutValue;

        _hashMask = (1 << LzHash.HashBits(dictSize)) - 1;
        int hashSize = LzHash.TableSize(_hashMask);
        _hash = ArrayPool<int>.Shared.Rent(hashSize);
        Array.Fill(_hash, -1, 0, hashSize);

        // Sized so a full cyclic span can accumulate behind the window before the
        // buffer fills, which is what guarantees a cyclic-multiple slide is
        // always available. See the remarks above.
        //
        // The cyclic term costs roughly one extra dictionary of bytes (~64 MB at
        // preset 9) purely to satisfy that slide restriction. The reference
        // liblzma finder indexes its position table by cyclic position rather
        // than by buffer offset, which decouples the two and lets it slide by any
        // amount. Adopting that would remove this term, but it means reworking
        // how every stored position is interpreted — a change to the same
        // invariant that produced the corruption in #11, so it is not folded in
        // with a deduplication pass.
        _bufferSize = _windowSize + _cyclicBufferSize + (1 << 16) + matchMaxLen + 4096;
        _buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
        _pos = 0;
        _streamPos = 0;
    }

    public int Available => _streamPos - _pos;

    /// <summary>
    /// Current search-buffer length. Exposed for tests: feeding the finder
    /// incrementally must let the slide keep this near window + cyclic, rather
    /// than growing it to the size of the whole input.
    /// </summary>
    internal int BufferLength => _bufferSize;

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

        // Slide by a multiple of the cyclic size so stored slots stay valid.
        int keepFrom = _pos - _windowSize;
        if (keepFrom > 0)
        {
            int delta = keepFrom & ~_cyclicMask; // round down to a cyclic multiple
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
            // More data than the window needs at once (a one-shot encode of an
            // input larger than the dictionary) — grow instead of sliding.
            int newSize = Math.Max(_streamPos + incoming + _matchMaxLen + 4096, _bufferSize * 2);
            byte[] bigger = ArrayPool<byte>.Shared.Rent(newSize);
            Buffer.BlockCopy(_buffer, 0, bigger, 0, _streamPos);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = bigger;
            _bufferSize = newSize;
        }
    }

    /// <summary>
    /// Shifts every stored position down by <paramref name="delta"/> after a
    /// slide, dropping any that fall off the front. <paramref name="delta"/> is a
    /// multiple of the cyclic size, so slot indices are unaffected.
    /// </summary>
    private void RebaseTables(int delta)
    {
        int hashSize = LzHash.TableSize(_hashMask);
        for (int i = 0; i < hashSize; i++)
        {
            int v = _hash[i];
            if (v >= 0) _hash[i] = v >= delta ? v - delta : -1;
        }
        RebasePositions(delta);
    }

    /// <summary>
    /// Applies the same shift as <see cref="RebaseTables"/> to the derived
    /// finder's position table (chain links or tree children).
    /// </summary>
    protected abstract void RebasePositions(int delta);

    /// <summary>Empties the derived finder's position table.</summary>
    protected abstract void ClearPositions();

    /// <summary>Returns the derived finder's position table to the pool.</summary>
    protected abstract void ReleasePositions();

    public void Reset()
    {
        _pos = 0;
        _streamPos = 0;
        _hashUpdatedAtPos = false;
        Array.Fill(_hash, -1, 0, LzHash.TableSize(_hashMask));
        ClearPositions();
    }

    public abstract int FindMatches(Span<int> distances, Span<int> lengths, int maxMatches);

    public abstract void MovePos();

    public void Skip(int count)
    {
        for (int i = 0; i < count; i++)
            MovePos();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        ArrayPool<int>.Shared.Return(_hash);
        ArrayPool<byte>.Shared.Return(_buffer);
        ReleasePositions();
        _hash = null!;
        _buffer = null!;
        _disposed = true;
    }
}
