// SPDX-License-Identifier: 0BSD

using LzmaNet.Lzma;
using LzmaNet.Lzma2;
using LzmaNet.Xz;

namespace LzmaNet;

/// <summary>
/// A write-only stream that compresses data into the XZ (.xz) format.
/// Data written to this stream is compressed and written to the underlying output stream.
/// Dispose the stream to finalize the XZ output.
/// </summary>
/// <remarks>
/// <para>Usage example:</para>
/// <code>
/// using var output = File.Create("data.xz");
/// using (var xzStream = new XzCompressStream(output))
/// {
///     xzStream.Write(data);
/// }
/// </code>
/// </remarks>
public sealed class XzCompressStream : Stream
{
    private readonly Stream _baseStream;
    private readonly bool _leaveOpen;
    private readonly int _checkType;
    private readonly Lzma2Encoder _encoder;
    private readonly LzmaEncoderProperties _props;
    private readonly MemoryStream _inputBuffer;
    private readonly int _blockSize;
    private readonly int _threads;
    private readonly List<(long unpaddedSize, long uncompressedSize)> _indexRecords;
    private bool _headerWritten;
    private bool _finished;
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="XzCompressStream"/> that writes compressed data
    /// to the specified output stream using the given options.
    /// </summary>
    /// <param name="stream">The output stream to write compressed data to.</param>
    /// <param name="options">Compression options. When <c>null</c>, uses default settings (preset 6, CRC64, single-threaded).</param>
    /// <param name="leaveOpen">If <c>true</c>, the underlying stream is not closed when this stream is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.</exception>
    public XzCompressStream(Stream stream, XzCompressOptions? options = null, bool leaveOpen = false)
    {
        _baseStream = stream ?? throw new ArgumentNullException(nameof(stream));
        var opts = options ?? XzCompressOptions.Default;
        opts.Validate();

        _leaveOpen = leaveOpen;
        _checkType = opts.CheckTypeValue;
        _threads = opts.ResolvedThreads;

        var props = LzmaEncoderProperties.FromPreset(opts.Preset, opts.Extreme);
        if (opts.DictionarySize.HasValue)
            props.DictionarySize = opts.DictionarySize.Value;
        _props = props;
        _encoder = new Lzma2Encoder(props);
        _inputBuffer = new MemoryStream();
        _blockSize = opts.BlockSize ?? Math.Max(props.DictionarySize * 2, 1 << 20);
        _indexRecords = new List<(long, long)>();
    }

    /// <inheritdoc/>
    public override bool CanRead => false;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => true;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
#if NET7_0_OR_GREATER
        ObjectDisposedException.ThrowIf(_disposed, this);
#else
        if (_disposed) throw new ObjectDisposedException(GetType().FullName);
#endif
        if (_finished)
            throw new InvalidOperationException("Stream has been finalized.");

        EnsureHeader();

        _inputBuffer.Write(buffer);

        // Flush blocks when buffer exceeds block size
        if (_threads == 1)
        {
            while (_inputBuffer.Length >= _blockSize)
            {
                FlushBlock();
            }
        }
        else
        {
            // For multi-threaded mode, flush when we have enough blocks to parallelize
            while (_inputBuffer.Length >= _blockSize * _threads)
            {
                FlushBlocksParallel();
            }
        }
    }

    /// <inheritdoc/>
    public override void Flush()
    {
#if NET7_0_OR_GREATER
        ObjectDisposedException.ThrowIf(_disposed, this);
#else
        if (_disposed) throw new ObjectDisposedException(GetType().FullName);
#endif
        _baseStream.Flush();
    }

    private void EnsureHeader()
    {
        if (!_headerWritten)
        {
            Span<byte> header = stackalloc byte[XzConstants.StreamHeaderSize];
            XzHeader.WriteStreamHeader(header, _checkType);
            _baseStream.Write(header);
            _headerWritten = true;
        }
    }

    private void FlushBlock()
    {
        if (_inputBuffer.Length == 0) return;

        // Use GetBuffer + Length to avoid a full copy
        var data = _inputBuffer.GetBuffer().AsMemory(0, (int)_inputBuffer.Length);

        var (unpaddedSize, uncompressedSize) = XzBlock.WriteBlock(
            _baseStream, data, _encoder, _checkType);

        _indexRecords.Add((unpaddedSize, uncompressedSize));
        _inputBuffer.SetLength(0);
    }

    private void FlushBlocksParallel()
    {
        if (_inputBuffer.Length == 0) return;

        var buffer = _inputBuffer.GetBuffer();
        int totalLen = (int)_inputBuffer.Length;

        // Split into blocks
        var blocks = new List<byte[]>();
        int pos = 0;
        while (pos < totalLen && blocks.Count < _threads)
        {
            int len = Math.Min(totalLen - pos, _blockSize);
            // Must copy because parallel encoders need independent buffers
            var block = new byte[len];
            Buffer.BlockCopy(buffer, pos, block, 0, len);
            blocks.Add(block);
            pos += len;
        }

        // Compress blocks in parallel — each produces a complete XZ block
        var results = new (MemoryStream blockData, long unpaddedSize, long uncompressedSize)[blocks.Count];
        Parallel.For(0, blocks.Count, new ParallelOptions { MaxDegreeOfParallelism = _threads }, i =>
        {
            var encoder = new Lzma2Encoder(_props);
            try
            {
                var blockStream = new MemoryStream();
                var (unpaddedSize, uncompressedSize) = XzBlock.WriteBlock(
                    blockStream, blocks[i].AsMemory(), encoder, _checkType);
                results[i] = (blockStream, unpaddedSize, uncompressedSize);
            }
            finally
            {
                encoder.Dispose();
            }
        });

        // Write blocks sequentially to output (XZ requires sequential block order)
        foreach (var (blockData, unpaddedSize, uncompressedSize) in results)
        {
            using (blockData)
            {
                blockData.Position = 0;
                blockData.CopyTo(_baseStream);
                _indexRecords.Add((unpaddedSize, uncompressedSize));
            }
        }

        // Shift remaining data to front
        int remaining = totalLen - pos;
        if (remaining > 0)
        {
            Buffer.BlockCopy(buffer, pos, buffer, 0, remaining);
        }
        _inputBuffer.SetLength(remaining);
        _inputBuffer.Position = remaining;
    }

    private void Finalize_()
    {
        if (_finished) return;
        _finished = true;

        EnsureHeader();

        // Flush remaining data
        if (_inputBuffer.Length > 0)
        {
            if (_threads > 1 && _inputBuffer.Length > _blockSize)
                FlushBlocksParallel();
            while (_inputBuffer.Length > 0)
                FlushBlock();
        }

        // Write index
        long indexSize = XzIndex.WriteIndex(_baseStream, _indexRecords);

        // Write stream footer
        Span<byte> footer = stackalloc byte[XzConstants.StreamFooterSize];
        XzHeader.WriteStreamFooter(footer, _checkType, indexSize);
        _baseStream.Write(footer);

        _baseStream.Flush();
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                Finalize_();
                _encoder.Dispose();
                _inputBuffer.Dispose();
                if (!_leaveOpen)
                    _baseStream.Dispose();
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}
