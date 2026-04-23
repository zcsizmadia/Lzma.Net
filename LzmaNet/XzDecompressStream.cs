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
    private readonly List<(long unpaddedSize, long uncompressedSize)> _blockRecords = new();

    /// <summary>
    /// Initializes a new <see cref="XzDecompressStream"/> that reads compressed data
    /// from the specified stream.
    /// </summary>
    /// <param name="stream">The stream containing XZ compressed data.</param>
    /// <param name="leaveOpen">If <c>true</c>, the underlying stream is not closed when this stream is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.</exception>
    public XzDecompressStream(Stream stream, bool leaveOpen = false)
    {
        _baseStream = stream ?? throw new ArgumentNullException(nameof(stream));
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
                _blockRecords.Clear();
            }

            // Need to decompress the next block
            if (_allBlocksRead)
            {
                if (!_streamFinalized)
                {
                    // Read and cross-validate index
                    long indexSize = XzIndex.ReadIndex(_baseStream, out var indexRecords);

                    // Cross-validate: number of records must match blocks decoded
                    if (indexRecords.Count != _blockRecords.Count)
                        throw new LzmaDataErrorException(
                            $"XZ index record count ({indexRecords.Count}) does not match block count ({_blockRecords.Count}).");

                    for (int i = 0; i < indexRecords.Count; i++)
                    {
                        if (indexRecords[i].unpaddedSize != _blockRecords[i].unpaddedSize)
                            throw new LzmaDataErrorException(
                                $"XZ index unpadded size mismatch at block {i}.");
                        if (indexRecords[i].uncompressedSize != _blockRecords[i].uncompressedSize)
                            throw new LzmaDataErrorException(
                                $"XZ index uncompressed size mismatch at block {i}.");
                    }

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

            // Decompress next block into a buffer
            using var blockOutput = new MemoryStream();
            if (!XzBlock.ReadBlock(_baseStream, _checkType, blockOutput,
                                    out long unpaddedSize, out long uncompressedSize))
            {
                _allBlocksRead = true;
                continue;
            }

            _blockRecords.Add((unpaddedSize, uncompressedSize));

            int len = (int)blockOutput.Length;
            if (len > 0)
            {
                _blockBuffer = ArrayPool<byte>.Shared.Rent(len);
                _blockBufferLen = len;
                _blockBufferPos = 0;
                blockOutput.Position = 0;
                blockOutput.Read(_blockBuffer, 0, len);
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
}
