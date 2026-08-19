// SPDX-License-Identifier: 0BSD

using System.Buffers;

using LzmaNet.Xz;

namespace LzmaNet;

/// <summary>
/// A read-only stream that decompresses XZ (.xz) formatted data on the fly.
/// Wraps an underlying stream containing XZ compressed data and provides
/// decompressed bytes when read.
/// </summary>
/// <remarks>
/// <para>Usage example:</para>
/// <code>
/// using var xzStream = new XzDecompressStream(File.OpenRead("data.xz"));
/// using var output = File.Create("data.bin");
/// xzStream.CopyTo(output);
/// </code>
/// </remarks>
public sealed class XzDecompressStream : Stream
{
    private readonly Stream _baseStream;
    private readonly bool _leaveOpen;
    private readonly int _threads;
    private byte[]? _blockBuffer;
    private int _blockBufferPos;
    private int _blockBufferLen;
    private bool _allBlocksRead;
    private bool _streamFinalized;
    private bool _allStreamsRead;
    private bool _disposed;
    private int _checkType;
    private bool _headerRead;
    private bool _isFirstStream = true;
    private bool _indexIndicatorSeen;
    private readonly List<(long unpaddedSize, long uncompressedSize)> _blockRecords = new();
    private readonly Queue<XzBlock.BlockBufferResult> _decodedBlocks = new();

    /// <summary>
    /// Initializes a new <see cref="XzDecompressStream"/> that reads compressed data
    /// from the specified stream.
    /// </summary>
    /// <param name="stream">The stream containing XZ compressed data.</param>
    /// <param name="leaveOpen">If <c>true</c>, the underlying stream is not closed when this stream is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.</exception>
    public XzDecompressStream(Stream stream, bool leaveOpen = false)
        : this(stream, threads: 1, leaveOpen)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="XzDecompressStream"/> that reads compressed data
    /// from the specified stream, optionally decoding XZ blocks in parallel.
    /// </summary>
    /// <param name="stream">The stream containing XZ compressed data.</param>
    /// <param name="threads">Number of decoder threads: 0 = use all available CPUs,
    /// 1 = single-threaded, N = use up to N threads. Parallelism applies per XZ block,
    /// so it only helps for multi-block streams; single-block streams decode serially.
    /// Up to <paramref name="threads"/> decoded blocks are buffered in memory at once.</param>
    /// <param name="leaveOpen">If <c>true</c>, the underlying stream is not closed when this stream is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threads"/> is negative.</exception>
    public XzDecompressStream(Stream stream, int threads, bool leaveOpen = false)
    {
        _baseStream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (threads < 0)
            throw new ArgumentOutOfRangeException(nameof(threads), "Threads must be >= 0.");
        _threads = threads == 0 ? Environment.ProcessorCount : threads;
        _leaveOpen = leaveOpen;
    }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
        CancellationToken cancellationToken)
    {
        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        return ReadAsyncCore(buffer, cancellationToken);
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_allStreamsRead)
            return 0;

        Span<byte> headerBuf = stackalloc byte[XzConstants.StreamHeaderSize];
        Span<byte> footerBuf = stackalloc byte[XzConstants.StreamFooterSize];

        int totalCopied = 0;
        while (totalCopied < buffer.Length)
        {
            // If we have data in the current block buffer, copy from it
            if (_blockBuffer != null && _blockBufferPos < _blockBufferLen)
            {
                int toCopy = Math.Min(buffer.Length - totalCopied, _blockBufferLen - _blockBufferPos);
                _blockBuffer.AsSpan(_blockBufferPos, toCopy).CopyTo(buffer.Slice(totalCopied));
                _blockBufferPos += toCopy;
                totalCopied += toCopy;

                if (_blockBufferPos >= _blockBufferLen)
                {
                    ArrayPool<byte>.Shared.Return(_blockBuffer);
                    _blockBuffer = null;
                }
                continue;
            }

            // Read stream header on first access or after a concatenated stream
            if (!_headerRead)
            {
                if (_isFirstStream)
                {
                    // First stream — read header directly (no padding skipping)
                    ReadExact(_baseStream, headerBuf);
                    _checkType = XzHeader.ReadStreamHeader(headerBuf);
                    _isFirstStream = false;
                }
                else
                {
                    // After a finalized stream — try to read concatenated stream with padding
                    if (!TryReadStreamHeader(headerBuf))
                    {
                        _allStreamsRead = true;
                        break;
                    }
                    _checkType = XzHeader.ReadStreamHeader(headerBuf);
                }
                _headerRead = true;
                _allBlocksRead = false;
                _streamFinalized = false;
                _indexIndicatorSeen = false;
                _blockRecords.Clear();
            }

            // Need to decompress the next block
            if (_allBlocksRead)
            {
                if (!_streamFinalized)
                {
                    // Read and cross-validate index
                    long indexSize = XzIndex.ReadIndex(_baseStream, out var indexRecords);
                    ValidateIndexRecords(indexRecords);

                    // Read and validate footer
                    ReadExact(_baseStream, footerBuf);
                    long backwardSize = XzHeader.ReadStreamFooter(footerBuf, _checkType);

                    // Validate backward size matches actual index size
                    if (backwardSize != indexSize)
                        throw new LzmaDataErrorException(
                            $"XZ stream footer backward size ({backwardSize}) does not match index size ({indexSize}).");

                    _streamFinalized = true;
                    _headerRead = false; // Allow reading next concatenated stream
                }
                continue;
            }

            if (_threads > 1)
            {
                // Parallel mode: read raw blocks in batches and decode them concurrently.
                if (_decodedBlocks.Count == 0 && !_indexIndicatorSeen)
                    FillDecodedBlocks();

                if (_decodedBlocks.Count == 0)
                {
                    _allBlocksRead = true;
                    continue;
                }

                XzBlock.BlockBufferResult decoded = _decodedBlocks.Dequeue();
                _blockRecords.Add((decoded.UnpaddedSize, decoded.UncompressedSize));

                if (decoded.Length > 0)
                {
                    _blockBuffer = decoded.Buffer;
                    _blockBufferLen = decoded.Length;
                    _blockBufferPos = 0;
                }
                else if (decoded.Buffer != null)
                {
                    ArrayPool<byte>.Shared.Return(decoded.Buffer);
                }
                continue;
            }

            // Decompress next block directly into a pooled buffer.
            if (!XzBlock.ReadBlockToBuffer(_baseStream, _checkType,
                                           out byte[]? blockBuffer, out int blockLength,
                                           out long unpaddedSize, out long uncompressedSize))
            {
                _allBlocksRead = true;
                continue;
            }

            _blockRecords.Add((unpaddedSize, uncompressedSize));

            if (blockLength > 0)
            {
                _blockBuffer = blockBuffer;
                _blockBufferLen = blockLength;
                _blockBufferPos = 0;
            }
            else if (blockBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(blockBuffer);
            }
        }

        return totalCopied;
    }

    /// <summary>
    /// Reads up to <see cref="_threads"/> raw blocks from the base stream (sequential I/O)
    /// and decodes them in parallel (CPU-bound), queueing the results in stream order.
    /// </summary>
    private void FillDecodedBlocks()
    {
        var rawBlocks = new List<MemoryStream>();
        try
        {
            while (rawBlocks.Count < _threads)
            {
                if (!XzBlock.ReadRawBlock(_baseStream, _checkType, out MemoryStream? raw))
                {
                    _indexIndicatorSeen = true;
                    break;
                }
                rawBlocks.Add(raw!);
            }

            if (rawBlocks.Count == 0)
                return;

            var results = new XzBlock.BlockBufferResult[rawBlocks.Count];

            if (rawBlocks.Count == 1)
            {
                results[0] = DecodeRawBlock(rawBlocks[0], _checkType);
            }
            else
            {
                int checkType = _checkType;
                try
                {
                    Parallel.For(0, rawBlocks.Count,
                        new ParallelOptions { MaxDegreeOfParallelism = _threads },
                        i => results[i] = DecodeRawBlock(rawBlocks[i], checkType));
                }
                catch (AggregateException ae)
                {
                    // Return any pooled buffers from blocks that did decode, then
                    // surface the original exception type (tests and callers expect
                    // LzmaDataErrorException, not AggregateException).
                    foreach (var result in results)
                    {
                        if (result.Buffer != null)
                            ArrayPool<byte>.Shared.Return(result.Buffer);
                    }
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo
                        .Capture(ae.InnerExceptions[0]).Throw();
                }
            }

            foreach (var result in results)
                _decodedBlocks.Enqueue(result);
        }
        finally
        {
            foreach (var raw in rawBlocks)
                raw.Dispose();
        }
    }

    private static XzBlock.BlockBufferResult DecodeRawBlock(MemoryStream raw, int checkType)
    {
        bool hasBlock = XzBlock.ReadBlockToBuffer(raw, checkType,
            out byte[]? buffer, out int length,
            out long unpaddedSize, out long uncompressedSize);
        // ReadRawBlock never returns an index indicator, so hasBlock is always true.
        return new XzBlock.BlockBufferResult(hasBlock, buffer, length, unpaddedSize, uncompressedSize);
    }

    private async ValueTask<int> ReadAsyncCore(
        Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (_allStreamsRead)
            return 0;

        byte[]? headerBuffer = null;
        byte[]? footerBuffer = null;
        int totalCopied = 0;

        while (totalCopied < buffer.Length)
        {
            if (_blockBuffer != null && _blockBufferPos < _blockBufferLen)
            {
                int toCopy = Math.Min(buffer.Length - totalCopied,
                    _blockBufferLen - _blockBufferPos);
                _blockBuffer.AsMemory(_blockBufferPos, toCopy)
                    .CopyTo(buffer[totalCopied..]);
                _blockBufferPos += toCopy;
                totalCopied += toCopy;

                if (_blockBufferPos >= _blockBufferLen)
                {
                    ArrayPool<byte>.Shared.Return(_blockBuffer);
                    _blockBuffer = null;
                }
                continue;
            }

            if (!_headerRead)
            {
                if (_isFirstStream)
                {
                    headerBuffer ??= new byte[XzConstants.StreamHeaderSize];
                    await ReadExactAsync(_baseStream, headerBuffer, cancellationToken)
                        .ConfigureAwait(false);
                    _checkType = XzHeader.ReadStreamHeader(headerBuffer);
                    _isFirstStream = false;
                }
                else
                {
                    headerBuffer ??= new byte[XzConstants.StreamHeaderSize];
                    if (!await TryReadStreamHeaderAsync(headerBuffer, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        _allStreamsRead = true;
                        break;
                    }
                    _checkType = XzHeader.ReadStreamHeader(headerBuffer);
                }

                _headerRead = true;
                _allBlocksRead = false;
                _streamFinalized = false;
                _indexIndicatorSeen = false;
                _blockRecords.Clear();
            }

            if (_allBlocksRead)
            {
                if (!_streamFinalized)
                {
                    var (indexSize, indexRecords) = await XzIndex.ReadIndexAsync(
                        _baseStream, cancellationToken).ConfigureAwait(false);
                    ValidateIndexRecords(indexRecords);

                    footerBuffer ??= new byte[XzConstants.StreamFooterSize];
                    await ReadExactAsync(_baseStream, footerBuffer, cancellationToken)
                        .ConfigureAwait(false);
                    long backwardSize = XzHeader.ReadStreamFooter(footerBuffer, _checkType);
                    if (backwardSize != indexSize)
                        throw new LzmaDataErrorException(
                            $"XZ stream footer backward size ({backwardSize}) does not match index size ({indexSize}).");

                    _streamFinalized = true;
                    _headerRead = false;
                }
                continue;
            }

            XzBlock.BlockBufferResult block = await XzBlock.ReadBlockToBufferAsync(
                _baseStream, _checkType, cancellationToken).ConfigureAwait(false);
            if (!block.HasBlock)
            {
                _allBlocksRead = true;
                continue;
            }

            _blockRecords.Add((block.UnpaddedSize, block.UncompressedSize));
            if (block.Length > 0)
            {
                _blockBuffer = block.Buffer;
                _blockBufferLen = block.Length;
                _blockBufferPos = 0;
            }
            else if (block.Buffer != null)
            {
                ArrayPool<byte>.Shared.Return(block.Buffer);
            }
        }

        return totalCopied;
    }

    /// <summary>
    /// Tries to read a stream header, skipping any stream padding (null bytes).
    /// Returns false at end of input.
    /// </summary>
    private bool TryReadStreamHeader(Span<byte> header)
    {
        // The XZ spec allows stream padding (multiples of 4 null bytes) between concatenated streams
        int firstByte;
        int paddingCount = 0;
        while (true)
        {
            firstByte = _baseStream.ReadByte();
            if (firstByte < 0)
            {
                // End of stream — if we had padding it must be a multiple of 4
                if (paddingCount > 0 && (paddingCount % 4) != 0)
                    throw new LzmaDataErrorException("XZ stream padding is not a multiple of 4 bytes.");
                return false;
            }

            if (firstByte == 0x00)
            {
                paddingCount++;
                continue;
            }

            // Validate padding was a multiple of 4
            if (paddingCount > 0 && (paddingCount % 4) != 0)
                throw new LzmaDataErrorException("XZ stream padding is not a multiple of 4 bytes.");

            break;
        }

        // We have a non-zero byte — it should be the start of the magic
        header[0] = (byte)firstByte;
        ReadExact(_baseStream, header[1..]);
        return true;
    }

    private async ValueTask<bool> TryReadStreamHeaderAsync(
        Memory<byte> header, CancellationToken cancellationToken)
    {
        byte[] oneByte = new byte[1];
        int paddingCount = 0;
        while (true)
        {
            int read = await _baseStream.ReadAsync(oneByte, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (paddingCount > 0 && (paddingCount & 3) != 0)
                    throw new LzmaDataErrorException(
                        "XZ stream padding is not a multiple of 4 bytes.");
                return false;
            }

            if (oneByte[0] == 0)
            {
                paddingCount++;
                continue;
            }

            if (paddingCount > 0 && (paddingCount & 3) != 0)
                throw new LzmaDataErrorException(
                    "XZ stream padding is not a multiple of 4 bytes.");

            header.Span[0] = oneByte[0];
            await ReadExactAsync(_baseStream, header[1..], cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
    }

    private void ValidateIndexRecords(
        IReadOnlyList<(long unpaddedSize, long uncompressedSize)> indexRecords)
    {
        if (indexRecords.Count != _blockRecords.Count)
            throw new LzmaDataErrorException(
                $"XZ index record count ({indexRecords.Count}) does not match block count ({_blockRecords.Count}).");

        for (int i = 0; i < indexRecords.Count; i++)
        {
            if (indexRecords[i].unpaddedSize != _blockRecords[i].unpaddedSize)
                throw new LzmaDataErrorException($"XZ index unpadded size mismatch at block {i}.");
            if (indexRecords[i].uncompressedSize != _blockRecords[i].uncompressedSize)
                throw new LzmaDataErrorException($"XZ index uncompressed size mismatch at block {i}.");
        }
    }

    /// <inheritdoc/>
    public override void Flush() { }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                if (_blockBuffer != null)
                {
                    ArrayPool<byte>.Shared.Return(_blockBuffer);
                    _blockBuffer = null;
                }
                while (_decodedBlocks.Count > 0)
                {
                    var decoded = _decodedBlocks.Dequeue();
                    if (decoded.Buffer != null)
                        ArrayPool<byte>.Shared.Return(decoded.Buffer);
                }
                if (!_leaveOpen)
                    _baseStream.Dispose();
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    private static void ReadExact(Stream stream, Span<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer[offset..]);
            if (read == 0)
                throw new LzmaDataErrorException("Unexpected end of XZ stream.");
            offset += read;
        }
    }

    private static async ValueTask ReadExactAsync(
        Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new LzmaDataErrorException("Unexpected end of XZ stream.");
            offset += read;
        }
    }
}
