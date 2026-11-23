// SPDX-License-Identifier: 0BSD

using System.Buffers;

using LzmaNet.Compatibility;
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
#if NETSTANDARD2_0
    // netstandard2.0's Stream does not implement IAsyncDisposable.
    , IAsyncDisposable
#endif
{
    private readonly Stream _baseStream;
    private readonly bool _leaveOpen;
    private readonly int _threads;
    private readonly long _maxOutputSize;
    private long _totalProduced;
    private readonly IProgress<long>? _progress;
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
    // Parallel mode: blocks being decoded on the thread pool, in stream order.
    private readonly Queue<Task<XzBlock.BlockBufferResult>> _inFlight = new();

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
    /// Up to <paramref name="threads"/> blocks decode at once, and up to
    /// <paramref name="threads"/> + 1 decoded blocks are held in memory.</param>
    /// <param name="leaveOpen">If <c>true</c>, the underlying stream is not closed when this stream is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threads"/> is negative.</exception>
    public XzDecompressStream(Stream stream, int threads, bool leaveOpen = false)
        : this(stream, new XzDecompressOptions { Threads = threads }, leaveOpen)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="XzDecompressStream"/> with the specified options
    /// (thread count and output size limit).
    /// </summary>
    /// <param name="stream">The stream containing XZ compressed data.</param>
    /// <param name="options">Decompression options. When <c>null</c>, uses defaults
    /// (single-threaded, no output limit).</param>
    /// <param name="leaveOpen">If <c>true</c>, the underlying stream is not closed when this stream is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.</exception>
    public XzDecompressStream(Stream stream, XzDecompressOptions? options, bool leaveOpen = false)
    {
        _baseStream = stream ?? throw new ArgumentNullException(nameof(stream));
        var opts = options ?? XzDecompressOptions.Default;
        opts.Validate();
        _threads = opts.ResolvedThreads;
        _maxOutputSize = opts.MaxOutputSize;
        _progress = opts.Progress;
        _leaveOpen = leaveOpen;
    }

    private void AddProduced(long bytes)
    {
        _totalProduced += bytes;
        _progress?.Report(_totalProduced);
    }

    private long RemainingAllowance()
    {
        // Never throws here: a stream may legitimately end exactly at the limit.
        // Blocks claiming more than the remaining allowance are rejected inside
        // the block reader, before any allocation.
        return Math.Max(0, _maxOutputSize - _totalProduced);
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
#if !NETSTANDARD2_0
    public override ValueTask<int> ReadAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default)
#else
    public ValueTask<int> ReadAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default)
#endif
    {
        return ReadAsyncCore(buffer, cancellationToken);
    }

    /// <inheritdoc/>
#if !NETSTANDARD2_0
    public override int Read(Span<byte> buffer)
#else
    public int Read(Span<byte> buffer)
#endif
    {
        Portable.ThrowIfDisposed(_disposed, this);
        _failure?.Throw();
        try
        {
            return ReadCore(buffer);
        }
        catch (Exception e)
        {
            _failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e);
            throw;
        }
    }

    // A failed read leaves the input mid-block; later reads rethrow instead of
    // resuming at the next block and returning data past the gap.
    private System.Runtime.ExceptionServices.ExceptionDispatchInfo? _failure;

    private int ReadCore(Span<byte> buffer)
    {
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
                // Parallel mode: keep up to _threads blocks decoding (see TopUpInFlight)
                // and hand them out in stream order.
                TopUpInFlight();
                if (_inFlight.Count == 0)
                {
                    _allBlocksRead = true;
                    continue;
                }

                XzBlock.BlockBufferResult decoded;
                try
                {
                    decoded = _inFlight.Dequeue().GetAwaiter().GetResult();
                }
                catch
                {
                    DrainInFlight();
                    throw;
                }
                AcceptDecoded(decoded);
                SetBlockBuffer(decoded);
                // Start the next block now, so it decodes while this one is read.
                TopUpInFlight();
                continue;
            }

            // Decompress next block directly into a pooled buffer.
            if (!XzBlock.ReadBlockToBuffer(_baseStream, _checkType,
                                           out byte[]? blockBuffer, out int blockLength,
                                           out long unpaddedSize, out long uncompressedSize,
                                           RemainingAllowance()))
            {
                _allBlocksRead = true;
                continue;
            }

            AddProduced(uncompressedSize);
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
    /// Reads raw blocks from the base stream (sequential I/O) and starts decoding
    /// each one on the thread pool, until <see cref="_threads"/> decodes are in
    /// flight or the stream's blocks are exhausted.
    /// </summary>
    /// <remarks>
    /// A sliding window rather than batches: a new block starts as soon as the
    /// consumer takes the oldest one, so all threads stay busy and no block waits
    /// for the slowest of a batch before it can be handed out.
    /// </remarks>
    private void TopUpInFlight()
    {
        while (_inFlight.Count < _threads && !_indexIndicatorSeen)
        {
            if (!XzBlock.ReadRawBlock(_baseStream, _checkType, out MemoryStream? raw))
            {
                _indexIndicatorSeen = true;
                break;
            }
            StartDecode(raw!);
        }
    }

    private void StartDecode(MemoryStream raw)
    {
        // Each block is checked against the allowance left when it starts; the
        // cumulative limit is enforced as blocks are handed out (AcceptDecoded).
        int checkType = _checkType;
        long allowance = RemainingAllowance();
        _inFlight.Enqueue(Task.Run(() => DecodeRawBlock(raw, checkType, allowance)));
    }

    /// <summary>
    /// Records a decoded block, enforces the cumulative output limit, and makes
    /// its buffer the current read buffer.
    /// </summary>
    private void AcceptDecoded(XzBlock.BlockBufferResult decoded)
    {
        if (decoded.UncompressedSize > RemainingAllowance())
        {
            if (decoded.Buffer != null)
                ArrayPool<byte>.Shared.Return(decoded.Buffer);
            DrainInFlight();
            throw new LzmaMemoryLimitException();
        }
        AddProduced(decoded.UncompressedSize);
        _blockRecords.Add((decoded.UnpaddedSize, decoded.UncompressedSize));
    }

    private void SetBlockBuffer(XzBlock.BlockBufferResult decoded)
    {
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
    }

    /// <summary>
    /// Waits for every in-flight decode and returns its buffer to the pool,
    /// ignoring failures (used when abandoning the stream).
    /// </summary>
    private void DrainInFlight()
    {
        while (_inFlight.Count > 0)
        {
            var task = _inFlight.Dequeue();
            try
            {
                var result = task.GetAwaiter().GetResult();
                if (result.Buffer != null)
                    ArrayPool<byte>.Shared.Return(result.Buffer);
            }
            catch
            {
                // The block's own error is moot once the stream is abandoned.
            }
        }
    }

    private static XzBlock.BlockBufferResult DecodeRawBlock(MemoryStream raw, int checkType, long maxOutputSize)
    {
        using (raw)
        {
            bool hasBlock = XzBlock.ReadBlockToBuffer(raw, checkType,
                out byte[]? buffer, out int length,
                out long unpaddedSize, out long uncompressedSize, maxOutputSize);
            // ReadRawBlock never returns an index indicator, so hasBlock is always true.
            return new XzBlock.BlockBufferResult(hasBlock, buffer, length, unpaddedSize, uncompressedSize);
        }
    }

    private async ValueTask<int> ReadAsyncCore(
        Memory<byte> buffer, CancellationToken cancellationToken)
    {
        Portable.ThrowIfDisposed(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _failure?.Throw();
        try
        {
            return await ReadAsyncCoreUnlatched(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e);
            throw;
        }
    }

    private async ValueTask<int> ReadAsyncCoreUnlatched(
        Memory<byte> buffer, CancellationToken cancellationToken)
    {
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

            if (_threads > 1)
            {
                // Parallel mode: mirrors the sync path, with asynchronous reads.
                await TopUpInFlightAsync(cancellationToken).ConfigureAwait(false);
                if (_inFlight.Count == 0)
                {
                    _allBlocksRead = true;
                    continue;
                }

                XzBlock.BlockBufferResult decoded;
                try
                {
                    decoded = await _inFlight.Dequeue().ConfigureAwait(false);
                }
                catch
                {
                    DrainInFlight();
                    throw;
                }
                AcceptDecoded(decoded);
                SetBlockBuffer(decoded);
                await TopUpInFlightAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            XzBlock.BlockBufferResult block = await XzBlock.ReadBlockToBufferAsync(
                _baseStream, _checkType, RemainingAllowance(), cancellationToken).ConfigureAwait(false);
            if (!block.HasBlock)
            {
                _allBlocksRead = true;
                continue;
            }

            AddProduced(block.UncompressedSize);
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
    /// Async counterpart of <see cref="TopUpInFlight"/>: the raw block reads are
    /// asynchronous, the decodes run on the thread pool.
    /// </summary>
    private async ValueTask TopUpInFlightAsync(CancellationToken cancellationToken)
    {
        while (_inFlight.Count < _threads && !_indexIndicatorSeen)
        {
            MemoryStream? raw = await XzBlock.ReadRawBlockAsync(
                _baseStream, _checkType, cancellationToken).ConfigureAwait(false);
            if (raw == null)
            {
                _indexIndicatorSeen = true;
                break;
            }
            StartDecode(raw);
        }
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

#if NETSTANDARD2_0
    /// <summary>
    /// Releases the stream. Decompression has no asynchronous teardown — the
    /// work is all in <see cref="Dispose(bool)"/> — so this exists only because
    /// netstandard2.0's <see cref="Stream"/> supplies no <c>DisposeAsync</c> for
    /// <c>await using</c> to find.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
#endif

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
                DrainInFlight();
                if (!_leaveOpen)
                    _baseStream.Dispose();
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    private static void ReadExact(Stream stream, Span<byte> buffer)
        => stream.ReadExact(buffer, "Unexpected end of XZ stream.");

    private static ValueTask ReadExactAsync(
        Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
        => stream.ReadExactAsync(buffer, "Unexpected end of XZ stream.", cancellationToken);
}
