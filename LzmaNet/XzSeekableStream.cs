// SPDX-License-Identifier: 0BSD

using System.Buffers;
using System.Buffers.Binary;

using LzmaNet.Check;
using LzmaNet.Xz;

namespace LzmaNet;

/// <summary>
/// A read-only, seekable stream over XZ compressed data, providing random access
/// to the uncompressed content without decompressing the whole file.
/// </summary>
/// <remarks>
/// <para>
/// The XZ index (parsed once from the end of the file) maps uncompressed positions
/// to blocks, so a <see cref="Seek"/> followed by <see cref="Read(Span{byte})"/>
/// decodes only the block containing the requested position. The most recently
/// decoded block is cached, making sequential and locality-friendly access patterns
/// cheap. Random access granularity is the XZ block size, so files compressed with
/// smaller blocks (e.g. <see cref="XzCompressOptions.BlockSize"/> = 1 MB) seek more
/// efficiently than single-block files, which require decoding the entire block.
/// </para>
/// <para>Concatenated XZ streams (with optional stream padding) are supported.</para>
/// <para>This class is not thread-safe.</para>
/// <para>Usage example:</para>
/// <code>
/// using var xz = new XzSeekableStream(File.OpenRead("data.xz"));
/// xz.Position = 1_000_000;
/// int read = xz.Read(buffer);
/// </code>
/// </remarks>
public sealed class XzSeekableStream : Stream
{
    private readonly record struct BlockEntry(
        long CompressedOffset, long UncompressedOffset, long UncompressedSize, int CheckType);

    private readonly Stream _baseStream;
    private readonly bool _leaveOpen;
    private readonly long _maxBlockOutputSize;
    private readonly List<BlockEntry> _blocks = new();
    private readonly long _totalUncompressedSize;

    private long _position;
    private int _cachedBlockIndex = -1;
    private byte[]? _cachedBlockBuffer;
    private int _cachedBlockLength;
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="XzSeekableStream"/> over the specified seekable
    /// stream of XZ compressed data. The XZ index is parsed immediately.
    /// </summary>
    /// <param name="stream">A readable, seekable stream containing XZ data.</param>
    /// <param name="options">Decompression options; <see cref="XzDecompressOptions.MaxOutputSize"/>
    /// is applied per block. When <c>null</c>, uses defaults.</param>
    /// <param name="leaveOpen">If <c>true</c>, the underlying stream is not closed when this stream is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is not readable and seekable.</exception>
    /// <exception cref="LzmaFormatException">The data is not in valid XZ format.</exception>
    /// <exception cref="LzmaDataErrorException">The XZ index or footer is corrupt.</exception>
    public XzSeekableStream(Stream stream, XzDecompressOptions? options = null, bool leaveOpen = false)
    {
        _baseStream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("Random access requires a readable, seekable stream.", nameof(stream));

        var opts = options ?? XzDecompressOptions.Default;
        opts.Validate();
        _maxBlockOutputSize = opts.MaxOutputSize;
        _leaveOpen = leaveOpen;

        _totalUncompressedSize = ParseStreams();
    }

    /// <summary>Number of XZ blocks (the granularity of random access).</summary>
    public int BlockCount => _blocks.Count;

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => true;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => _totalUncompressedSize;

    /// <inheritdoc/>
    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            _position = value;
        }
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _totalUncompressedSize + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0)
            throw new IOException("Cannot seek before the beginning of the stream.");
        _position = target;
        return _position;
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

        int totalCopied = 0;
        while (totalCopied < buffer.Length && _position < _totalUncompressedSize)
        {
            int blockIndex = FindBlock(_position);
            EnsureBlockDecoded(blockIndex);

            var entry = _blocks[blockIndex];
            int offsetInBlock = (int)(_position - entry.UncompressedOffset);
            int toCopy = Math.Min(buffer.Length - totalCopied, _cachedBlockLength - offsetInBlock);
            _cachedBlockBuffer.AsSpan(offsetInBlock, toCopy).CopyTo(buffer.Slice(totalCopied));
            _position += toCopy;
            totalCopied += toCopy;
        }

        return totalCopied;
    }

    /// <summary>
    /// Finds the block containing the given uncompressed position (binary search).
    /// </summary>
    private int FindBlock(long position)
    {
        int lo = 0, hi = _blocks.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_blocks[mid].UncompressedOffset <= position)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo;
    }

    private void EnsureBlockDecoded(int blockIndex)
    {
        if (_cachedBlockIndex == blockIndex)
            return;

        var entry = _blocks[blockIndex];
        _baseStream.Position = entry.CompressedOffset;
        if (!XzBlock.ReadBlockToBuffer(_baseStream, entry.CheckType,
                out byte[]? buffer, out int length,
                out _, out long uncompressedSize, _maxBlockOutputSize))
        {
            throw new LzmaDataErrorException("Expected an XZ block, found the index indicator.");
        }

        if (uncompressedSize != entry.UncompressedSize)
        {
            if (buffer != null)
                ArrayPool<byte>.Shared.Return(buffer);
            throw new LzmaDataErrorException(
                $"XZ block size ({uncompressedSize}) does not match the index record ({entry.UncompressedSize}).");
        }

        ReturnCachedBuffer();
        _cachedBlockBuffer = buffer;
        _cachedBlockLength = length;
        _cachedBlockIndex = blockIndex;
    }

    /// <summary>
    /// Locates every block by walking the streams backward from the end of the
    /// file: footer → index → stream header, skipping stream padding.
    /// </summary>
    private long ParseStreams()
    {
        // Collected back-to-front, then offsets assigned front-to-back.
        var streamsBackward = new List<(long BlocksStart, int CheckType, List<(long unpadded, long uncompressed)> Records)>();

        long end = _baseStream.Length;
        Span<byte> footer = stackalloc byte[XzConstants.StreamFooterSize];
        Span<byte> header = stackalloc byte[XzConstants.StreamHeaderSize];

        while (end > 0)
        {
            if (end < XzConstants.StreamHeaderSize + XzConstants.StreamFooterSize)
                throw new LzmaFormatException("Truncated XZ stream.");

            // Skip stream padding (runs of 4 zero bytes) before the footer.
            _baseStream.Position = end - 4;
            Span<byte> four = footer[..4];
            ReadExact(_baseStream, four);
            if (four[0] == 0 && four[1] == 0 && four[2] == 0 && four[3] == 0)
            {
                end -= 4;
                continue;
            }

            // Parse the stream footer.
            _baseStream.Position = end - XzConstants.StreamFooterSize;
            ReadExact(_baseStream, footer);
            if (footer[10] != 0x59 || footer[11] != 0x5A) // "YZ"
                throw new LzmaFormatException("Missing XZ stream footer magic.");
            if (!Crc32.Verify(footer.Slice(4, 6), footer[..4]))
                throw new LzmaDataErrorException("XZ stream footer CRC32 mismatch.");
            if (footer[8] != 0x00 || (footer[9] & 0xF0) != 0)
                throw new LzmaFormatException("Unsupported XZ stream flags in footer.");
            int checkType = footer[9] & 0x0F;
            long indexSize = ((long)BinaryPrimitives.ReadUInt32LittleEndian(footer.Slice(4, 4)) + 1) * 4;

            // Parse the index.
            long indexPos = end - XzConstants.StreamFooterSize - indexSize;
            if (indexPos < XzConstants.StreamHeaderSize)
                throw new LzmaDataErrorException("XZ index position is out of range.");
            _baseStream.Position = indexPos;
            if (_baseStream.ReadByte() != 0x00)
                throw new LzmaDataErrorException("Missing XZ index indicator.");
            long actualIndexSize = XzIndex.ReadIndex(_baseStream, out var records);
            if (actualIndexSize != indexSize)
                throw new LzmaDataErrorException(
                    $"XZ footer backward size ({indexSize}) does not match index size ({actualIndexSize}).");

            // Locate the stream start and validate its header.
            long blocksTotal = 0;
            foreach (var (unpadded, _) in records)
            {
                if (unpadded <= 0)
                    throw new LzmaDataErrorException("Invalid unpadded size in XZ index.");
                blocksTotal += (unpadded + 3) & ~3L;
            }

            long streamStart = indexPos - blocksTotal - XzConstants.StreamHeaderSize;
            if (streamStart < 0)
                throw new LzmaDataErrorException("XZ block sizes exceed the stream extent.");
            _baseStream.Position = streamStart;
            ReadExact(_baseStream, header);
            int headerCheckType = XzHeader.ReadStreamHeader(header);
            if (headerCheckType != checkType)
                throw new LzmaDataErrorException("XZ stream header/footer check type mismatch.");

            streamsBackward.Add((streamStart + XzConstants.StreamHeaderSize, checkType, records));
            end = streamStart;
        }

        // Assign global offsets front-to-back (streams were collected backward).
        long uncompressedOffset = 0;
        for (int s = streamsBackward.Count - 1; s >= 0; s--)
        {
            var (blocksStart, checkType, records) = streamsBackward[s];
            long compressedOffset = blocksStart;
            foreach (var (unpadded, uncompressed) in records)
            {
                // Zero-size blocks contribute no content — exclude them from the
                // position map (they would stall the read loop).
                if (uncompressed > 0)
                    _blocks.Add(new BlockEntry(compressedOffset, uncompressedOffset, uncompressed, checkType));
                compressedOffset += (unpadded + 3) & ~3L;
                uncompressedOffset += uncompressed;
            }
        }

        return uncompressedOffset;
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

    private void ReturnCachedBuffer()
    {
        if (_cachedBlockBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(_cachedBlockBuffer);
            _cachedBlockBuffer = null;
            _cachedBlockIndex = -1;
            _cachedBlockLength = 0;
        }
    }

    /// <inheritdoc/>
    public override void Flush() { }

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
                ReturnCachedBuffer();
                if (!_leaveOpen)
                    _baseStream.Dispose();
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}
