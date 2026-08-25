// SPDX-License-Identifier: 0BSD

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.ExceptionServices;

using LzmaNet.Lzma;
using LzmaNet.RangeCoder;

namespace LzmaNet;

/// <summary>
/// A write-only stream producing the legacy <c>.lzma</c> ("LZMA-alone") format:
/// a 13-byte header (properties, dictionary size, uncompressed size) followed by
/// a single raw LZMA stream. Compatible with <c>xz --format=lzma</c> and 7-Zip.
/// </summary>
/// <remarks>
/// The legacy format has no blocks or integrity check; the whole input is
/// buffered and compressed as one stream when the stream is disposed. Prefer
/// the XZ format (<see cref="XzCompressStream"/>) for new applications.
/// </remarks>
public sealed class LzmaAloneCompressStream : Stream
{
    private readonly Stream _baseStream;
    private readonly bool _leaveOpen;
    private readonly LzmaEncoderProperties _props;
    private readonly MemoryStream _inputBuffer = new();
    private bool _finished;
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="LzmaAloneCompressStream"/> writing .lzma data
    /// to the specified output stream.
    /// </summary>
    /// <param name="stream">The output stream.</param>
    /// <param name="preset">Compression preset (0-9), same scale as XZ. Default 6.</param>
    /// <param name="leaveOpen">If <c>true</c>, the underlying stream is not closed on dispose.</param>
    public LzmaAloneCompressStream(Stream stream, int preset = 6, bool leaveOpen = false)
    {
        _baseStream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (preset < 0 || preset > 9)
            throw new ArgumentOutOfRangeException(nameof(preset), "Preset must be 0-9.");
        _props = LzmaEncoderProperties.FromPreset(preset);
        _leaveOpen = leaveOpen;
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
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished)
            throw new InvalidOperationException("Stream has been finalized.");
        _inputBuffer.Write(buffer);
    }

    /// <inheritdoc/>
    public override void Flush() { }

    private void Finalize_()
    {
        if (_finished) return;
        _finished = true;

        // The whole input is buffered, so cap the dictionary at the input size:
        // larger cannot help but costs proportional match-finder memory. The
        // header value is rounded up to a power of two — xz's alone decoder
        // mis-decodes streams whose header carries a non-canonical dictionary
        // size (observed empirically: silent truncation with exit code 0).
        long capped = Math.Clamp(_inputBuffer.Length, 4096, _props.DictionarySize);
        _props.DictionarySize = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)capped);

        // 13-byte header: properties byte, dictionary size (LE32),
        // uncompressed size (LE64; known, so no end marker is needed).
        Span<byte> header = stackalloc byte[13];
        header[0] = _props.PropertiesByte;
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(1, 4), (uint)_props.DictionarySize);
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(5, 8), (ulong)_inputBuffer.Length);
        _baseStream.Write(header);

        using var encoder = new LzmaEncoder(_props);
        encoder.Encode(_inputBuffer.GetBuffer().AsSpan(0, (int)_inputBuffer.Length), _baseStream);
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
                _inputBuffer.Dispose();
                if (!_leaveOpen)
                    _baseStream.Dispose();
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// A read-only stream that decompresses the legacy <c>.lzma</c> ("LZMA-alone")
/// format. Both known and unknown (0xFFFFFFFFFFFFFFFF, end-marker terminated)
/// uncompressed sizes are supported. Compatible with <c>xz --format=lzma</c> and 7-Zip.
/// </summary>
/// <remarks>
/// The legacy format is a single unblocked stream, so the entire payload is
/// decompressed into memory on first read.
/// <see cref="XzDecompressOptions.MaxOutputSize"/> is honored and recommended
/// when the input is untrusted.
/// </remarks>
public sealed class LzmaAloneDecompressStream : Stream
{
    private readonly Stream _baseStream;
    private readonly bool _leaveOpen;
    private readonly long _maxOutputSize;
    private readonly IProgress<long>? _progress;
    private byte[]? _outputBuffer;
    private int _outputLength;
    private int _outputPos;
    private bool _decoded;
    private ExceptionDispatchInfo? _failure;
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="LzmaAloneDecompressStream"/> reading .lzma data
    /// from the specified stream.
    /// </summary>
    /// <param name="stream">The stream containing .lzma data.</param>
    /// <param name="options">Decompression options; <see cref="XzDecompressOptions.Threads"/>
    /// is ignored (the format has no blocks). When <c>null</c>, uses defaults.</param>
    /// <param name="leaveOpen">If <c>true</c>, the underlying stream is not closed on dispose.</param>
    public LzmaAloneDecompressStream(Stream stream, XzDecompressOptions? options = null, bool leaveOpen = false)
    {
        _baseStream = stream ?? throw new ArgumentNullException(nameof(stream));
        var opts = options ?? XzDecompressOptions.Default;
        opts.Validate();
        _maxOutputSize = opts.MaxOutputSize;
        _progress = opts.Progress;
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
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureDecoded();

        int toCopy = Math.Min(buffer.Length, _outputLength - _outputPos);
        if (toCopy > 0)
        {
            _outputBuffer.AsSpan(_outputPos, toCopy).CopyTo(buffer);
            _outputPos += toCopy;
        }
        return toCopy;
    }

    /// <summary>
    /// Decodes the stream on first read. A failed decode is latched and rethrown
    /// on every later read: the payload has already been consumed, so retrying
    /// cannot succeed, and reporting end-of-stream instead would let corrupt
    /// input pass for an empty one.
    /// </summary>
    private void EnsureDecoded()
    {
        _failure?.Throw();
        if (_decoded)
            return;

        try
        {
            DecodeAll();
            _decoded = true;
        }
        catch (Exception ex)
        {
            _failure = ExceptionDispatchInfo.Capture(ex);
            throw;
        }
    }

    private void DecodeAll()
    {
        // 13-byte header
        Span<byte> header = stackalloc byte[13];
        ReadExact(_baseStream, header);
        if (!LzmaConstants.DecodeProperties(header[0], out int lc, out int lp, out int pb))
            throw new LzmaFormatException("Invalid .lzma properties byte.");
        // Dictionary size (header[1..5]) is not needed: the output buffer serves
        // as the window.
        ulong declaredSize = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(5, 8));
        bool sizeKnown = declaredSize != ulong.MaxValue;

        if (sizeKnown && declaredSize > (ulong)Math.Min(_maxOutputSize, int.MaxValue - LzmaConstants.kMatchMaxLen))
            throw new LzmaMemoryLimitException(
                $".lzma header claims {declaredSize:N0} uncompressed bytes, exceeding the configured or supported limit.");

        // The range decoder needs the whole compressed payload in memory.
        using var compressed = new MemoryStream();
        _baseStream.CopyTo(compressed);
        var input = compressed.GetBuffer().AsSpan(0, (int)compressed.Length);

        var decoder = new LzmaDecoder(lc, lp, pb);
        var rc = new RangeDecoder();
        rc.Init(input, 0);

        if (sizeKnown)
        {
            int size = (int)declaredSize;
            _outputBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(size, 1));
            int outPos = 0;
            decoder.DecodeChunk(ref rc, _outputBuffer.AsSpan(0, size), ref outPos, 0, size);
            _outputLength = outPos;
        }
        else
        {
            // Unknown size: decode in steps until the end marker, growing the
            // buffer (the whole output is the dictionary window, so it must
            // stay contiguous).
            const int Step = 1 << 20;
            byte[] buf = ArrayPool<byte>.Shared.Rent(Step + LzmaConstants.kMatchMaxLen);
            int outPos = 0;
            while (true)
            {
                if (outPos + Step + LzmaConstants.kMatchMaxLen > buf.Length)
                {
                    long needed = (long)outPos + Step + LzmaConstants.kMatchMaxLen;
                    if (outPos >= _maxOutputSize || needed > MaxOutputCapacity)
                    {
                        ArrayPool<byte>.Shared.Return(buf);
                        throw new LzmaMemoryLimitException();
                    }
                    byte[] bigger = ArrayPool<byte>.Shared.Rent(NextOutputCapacity(buf.Length, needed));
                    buf.AsSpan(0, outPos).CopyTo(bigger);
                    ArrayPool<byte>.Shared.Return(buf);
                    buf = bigger;
                }

                // Decode up to Step bytes, or just past the remaining allowance
                // (so the limit check below fires). Careful: naive
                // "_maxOutputSize - outPos + 1" overflows when the limit is
                // long.MaxValue.
                long allowance = _maxOutputSize - outPos;
                int softTarget = allowance >= Step ? Step : (int)allowance + 1;

                bool sawMarker;
                try
                {
                    sawMarker = decoder.DecodeWithEndMarker(ref rc, buf, ref outPos, 0, softTarget);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(buf);
                    throw;
                }

                if (sawMarker)
                    break;
                if (outPos > _maxOutputSize)
                {
                    ArrayPool<byte>.Shared.Return(buf);
                    throw new LzmaMemoryLimitException();
                }
            }
            _outputBuffer = buf;
            _outputLength = outPos;
        }

        if (_outputLength > _maxOutputSize)
        {
            ReturnOutputBuffer();
            throw new LzmaMemoryLimitException();
        }
        _progress?.Report(_outputLength);
    }

    /// <summary>
    /// Largest output buffer an unknown-size decode can grow to. ArrayPool serves
    /// requests this large by allocating directly, and the runtime refuses any
    /// array longer than <see cref="Array.MaxLength"/>.
    /// </summary>
    internal static int MaxOutputCapacity => Array.MaxLength;

    /// <summary>
    /// Next capacity for the growing unknown-size output buffer: double, but
    /// never past <see cref="MaxOutputCapacity"/>. Clamping to int.MaxValue
    /// instead asked for more than the runtime can allocate, so the 1 GB
    /// doubling step failed with <see cref="OutOfMemoryException"/> however much
    /// memory was free.
    /// </summary>
    internal static int NextOutputCapacity(int currentLength, long needed)
        => (int)Math.Min(Math.Max((long)currentLength * 2, needed), MaxOutputCapacity);

    private void ReturnOutputBuffer()
    {
        if (_outputBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(_outputBuffer);
            _outputBuffer = null;
        }
    }

    private static void ReadExact(Stream stream, Span<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer[offset..]);
            if (read == 0)
                throw new LzmaDataErrorException("Unexpected end of .lzma stream.");
            offset += read;
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
                ReturnOutputBuffer();
                if (!_leaveOpen)
                    _baseStream.Dispose();
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}
