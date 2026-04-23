// SPDX-License-Identifier: 0BSD

using System.Buffers;
using System.Runtime.CompilerServices;

namespace LzmaNet.Lzma;

/// <summary>
/// Sliding-window dictionary buffer for LZMA decoding.
/// Maintains decoded output and provides lookback for LZ match copying.
/// </summary>
internal sealed class OutputWindow : IDisposable
{
    private byte[] _buffer;
    private readonly int _size;
    private int _pos;
    private long _totalPos;
    private bool _disposed;

    /// <summary>Current write position in the circular buffer.</summary>
    public int Pos => _pos;

    /// <summary>Total bytes written since last reset.</summary>
    public long TotalPos => _totalPos;

    /// <summary>
    /// Creates an output window with the given dictionary size.
    /// </summary>
    /// <param name="dictSize">Size of the dictionary in bytes.</param>
    public OutputWindow(int dictSize)
    {
        _size = dictSize;
        _buffer = ArrayPool<byte>.Shared.Rent(dictSize);
        _pos = 0;
        _totalPos = 0;
    }

    /// <summary>
    /// Puts a single decoded byte into the dictionary.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PutByte(byte b)
    {
        _buffer[_pos] = b;
        _pos++;
        _totalPos++;
        if (_pos >= _size)
            _pos = 0;
    }

    /// <summary>
    /// Gets a byte at the given distance behind the current position.
    /// Distance 0 is the most recently written byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetByte(int distance)
    {
        int idx = _pos - distance - 1;
        if (idx < 0)
            idx += _size;
        return _buffer[idx];
    }

    /// <summary>
    /// Copies a match from the dictionary at the given distance and length,
    /// writing directly to both the dictionary and the output span.
    /// </summary>
    /// <param name="distance">Distance back (0-based: 0 = previous byte).</param>
    /// <param name="length">Number of bytes to copy.</param>
    /// <param name="output">Output buffer to copy to.</param>
    /// <param name="outPos">Current position in output; advanced by length.</param>
    public void CopyMatch(int distance, int length, Span<byte> output, ref int outPos)
    {
        int src = _pos - distance - 1;
        if (src < 0)
            src += _size;

        // For overlapping matches (distance < length), must copy byte-by-byte
        // since later bytes depend on earlier ones (run-length encoding pattern).
        // For non-overlapping, we can use bulk copies.
        int remaining = length;
        while (remaining > 0)
        {
            // How many bytes can we copy before src or _pos wraps?
            int srcRun = _size - src;
            int dstRun = _size - _pos;
            int chunk = Math.Min(remaining, Math.Min(srcRun, dstRun));

            // For overlapping copies (src ahead of _pos by <= remaining),
            // limit chunk to the non-overlapping portion.
            if (distance + 1 < remaining)
                chunk = Math.Min(chunk, distance + 1);

            _buffer.AsSpan(src, chunk).CopyTo(_buffer.AsSpan(_pos, chunk));
            _buffer.AsSpan(_pos, chunk).CopyTo(output.Slice(outPos, chunk));

            src += chunk;
            if (src >= _size) src -= _size;
            _pos += chunk;
            if (_pos >= _size) _pos -= _size;
            outPos += chunk;
            remaining -= chunk;
        }
        _totalPos += length;
    }

    /// <summary>
    /// Copies uncompressed data directly into the dictionary.
    /// </summary>
    public void CopyUncompressed(ReadOnlySpan<byte> data, Span<byte> output, ref int outPos)
    {
        int srcPos = 0;
        int remaining = data.Length;
        while (remaining > 0)
        {
            int run = Math.Min(remaining, _size - _pos);
            data.Slice(srcPos, run).CopyTo(_buffer.AsSpan(_pos, run));
            data.Slice(srcPos, run).CopyTo(output.Slice(outPos, run));
            _pos += run;
            if (_pos >= _size) _pos = 0;
            outPos += run;
            srcPos += run;
            remaining -= run;
        }
        _totalPos += data.Length;
    }

    /// <summary>
    /// Checks if enough data exists in the dictionary for a lookback at the given distance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasDistance(int distance) => distance < _totalPos;  // distance is int, _totalPos is long — safe for >2GB

    /// <summary>
    /// Resets the dictionary position counters without reallocating.
    /// </summary>
    public void Reset()
    {
        _pos = 0;
        _totalPos = 0;
    }

    /// <summary>
    /// Returns the buffer to the pool.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null!;
            _disposed = true;
        }
    }
}
